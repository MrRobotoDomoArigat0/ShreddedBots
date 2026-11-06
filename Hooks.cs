using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShreddedBots
{
    /// <summary>Thrown when a patch that keeps reporting/telemetry/online play blocked could not be applied. The launcher then refuses to start the game (fail closed).</summary>
    public sealed class FailClosedException : Exception
    {
        public FailClosedException(string message) : base(message) { }
    }

    /// <summary>Registry of every Harmony patch we try to apply, so the startup self-check can report hook counts and failures.</summary>
    public static class Hooks
    {
        public sealed class Entry
        {
            public string Category, Description, Status;   // Status: ok | FAILED | NOT FOUND
            public bool Critical;
            public string Error;
        }

        public static readonly List<Entry> All = new List<Entry>();
        public static Harmony H;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        public static bool Patch(string category, string description, MethodBase m, bool critical, Type host, string prefix = null, string postfix = null, string transpiler = null, string finalizer = null)
        {
            var e = new Entry { Category = category, Description = description, Critical = critical };
            lock (All) All.Add(e);
            if (m == null) { e.Status = "NOT FOUND"; Log.Warn("[hook] NOT FOUND: " + description); return false; }
            try
            {
                H.Patch(m,
                    prefix: prefix == null ? null : new HarmonyMethod(host, prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(host, postfix),
                    transpiler: transpiler == null ? null : new HarmonyMethod(host, transpiler),
                    finalizer: finalizer == null ? null : new HarmonyMethod(host, finalizer));
                e.Status = "ok";
                Log.Verbose("[hook] ok: " + description);
                return true;
            }
            catch (Exception ex)
            {
                e.Status = "FAILED"; e.Error = ex.GetType().Name + ": " + ex.Message;
                Log.Warn("[hook] FAILED: " + description + " (" + e.Error + ")");
                return false;
            }
        }

        public static void NotFound(string category, string description, bool critical)
        {
            lock (All) All.Add(new Entry { Category = category, Description = description, Critical = critical, Status = "NOT FOUND" });
            Log.Warn("[hook] NOT FOUND: " + description);
        }

        public static void Info(string category, string description, bool ok, bool critical = false)
        {
            lock (All) All.Add(new Entry { Category = category, Description = description, Critical = critical, Status = ok ? "ok" : "FAILED" });
            if (!ok) Log.Warn("[hook] FAILED: " + description);
        }

        /// <summary>All non-generic, non-abstract methods named <paramref name="method"/> declared on <paramref name="type"/> (every overload).</summary>
        public static List<MethodBase> Find(Type type, string method)
        {
            var l = new List<MethodBase>();
            if (type == null) return l;
            if (method == ".ctor") { l.AddRange(type.GetConstructors(Any).Where(c => !c.IsStatic)); return l; }
            foreach (MethodInfo m in type.GetMethods(Any))
                if (m.Name == method && !m.IsGenericMethodDefinition && !m.IsAbstract) l.Add(m);
            return l;
        }

        public static string Sig(MethodBase m) => m.DeclaringType.Name + "." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        /// <summary>Patch every overload of type:method with one of the generic prefixes in <see cref="Blocks"/>. Picks the prefix from the return type unless given.</summary>
        public static int Block(string category, Type type, string typeName, string method, bool critical, string note = null, string prefixName = null, bool optional = false)
        {
            List<MethodBase> ms = Find(type, method);
            if (ms.Count == 0)
            {
                if (optional) { Log.Verbose("[hook] optional target absent: " + typeName + "." + method); return 0; }
                NotFound(category, typeName + "." + method + (note != null ? " - " + note : ""), critical);
                return 0;
            }
            int ok = 0;
            foreach (MethodBase m in ms)
            {
                string p = prefixName ?? Blocks.PrefixFor(m);
                if (p == null) { Info(category, Sig(m) + " (unexpected return type; not patched)", false, critical); continue; }
                if (Patch(category, Sig(m) + (note != null ? " - " + note : ""), m, critical, typeof(Blocks), prefix: p)) ok++;
            }
            return ok;
        }

        public static Type T(string fullName) => AccessTools.TypeByName(fullName);

        /// <summary>True when the category has entries and every one applied.</summary>
        public static bool CategoryOk(string category) { lock (All) { bool any = false; foreach (var e in All) if (e.Category == category) { any = true; if (e.Status != "ok") return false; } return any; } }

        public static int Count(string status) { lock (All) return All.Count(e => e.Status == status); }
        public static List<Entry> Problems() { lock (All) return All.Where(e => e.Status != "ok").ToList(); }
    }

    /// <summary>Prefixes that replace a method with a no-op (returns default / a safe value), and count every blocked call (first one is logged).</summary>
    public static class Blocks
    {
        private static readonly ConcurrentDictionary<string, int> _counts = new ConcurrentDictionary<string, int>();

        public static string PrefixFor(MethodBase m)
        {
            Type r = m is MethodInfo mi ? mi.ReturnType : typeof(void);
            if (r == typeof(void)) return nameof(Skip);
            if (r == typeof(bool)) return nameof(SkipFalse);
            if (r == typeof(string)) return nameof(SkipEmptyString);
            if (r == typeof(byte[])) return nameof(SkipNull);
            if (r.FullName == "Paris.Engine.WebServiceResponse") return nameof(SkipWebServiceResponse);
            return nameof(Skip);   // any other return type: the original is skipped and default(T) is returned
        }

        public static void Hit(MethodBase m)
        {
            string k = (m?.DeclaringType?.Name ?? "?") + "." + (m?.Name ?? "?");
            int n = _counts.AddOrUpdate(k, 1, (_, c) => c + 1);
            if (n == 1) Log.Info("[block] " + k + ": first call blocked (later calls are only counted)");
            else if (Config.LogEveryBlockedCall) Log.Verbose("[block] " + k + " #" + n);
        }

        public static string Summary()
        {
            if (_counts.IsEmpty) return "no blocked calls yet";
            return string.Join(", ", _counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + " x" + kv.Value));
        }
        public static int TotalHits => _counts.Values.Sum();

        public static bool Skip(MethodBase __originalMethod) { Hit(__originalMethod); return false; }
        public static bool SkipFalse(ref bool __result, MethodBase __originalMethod) { __result = false; Hit(__originalMethod); return false; }
        public static bool SkipTrue(ref bool __result, MethodBase __originalMethod) { __result = true; Hit(__originalMethod); return false; }
        public static bool SkipEmptyString(ref string __result, MethodBase __originalMethod) { __result = ""; Hit(__originalMethod); return false; }
        public static bool SkipNull(ref byte[] __result, MethodBase __originalMethod) { __result = null; Hit(__originalMethod); return false; }
        public static bool SkipHttpResponse(ref System.Net.HttpWebResponse __result, MethodBase __originalMethod) { __result = null; Hit(__originalMethod); return false; }
        public static bool SkipWebServiceResponse(ref Paris.Engine.WebServiceResponse __result, MethodBase __originalMethod) { __result = Paris.Engine.WebServiceResponse.Empty; Hit(__originalMethod); return false; }
        public static bool SkipNow(ref DateTime __result, MethodBase __originalMethod) { __result = DateTime.UtcNow; Hit(__originalMethod); return false; }
    }
}
