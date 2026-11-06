using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ShreddedBots
{
    /// <summary>
    /// Kills every outbound crash-report / telemetry path found in TMNT.exe + ParisEngine.dll + NBug.dll (checked against the game build).
    /// Layered on purpose: if one layer misses, the next one still blocks the send. Anything critical that cannot be patched aborts the launch.
    ///
    /// Crash-report chain in the game:  AppDomain.UnhandledException -> Program.CrashHandler -> NBug.Handler.UnhandledException (if SteamHelper.OwnsGame)
    ///   -> NBug writes Exception_*.zip to %TEMP%\TMNT (DeferredReporting, StoragePath=WindowsTemp) -> NBug Dispatcher (started by Handler's static
    ///   constructor) -> Settings.Destinations -> Paris.NBugDiscordProtocol.Send -> Paris.DiscordWebHook.SendFile -> Paris.FormUpload.MultipartFormDataPost (https POST).
    ///   Program.Main adds the Discord protocol to Settings.Destinations.
    /// </summary>
    public static class Telemetry
    {
        private const string CrashCat = "crash-report", NetCat = "telemetry";
        private static int _mainNBugAddRemoved, _crashHandlerReplaced;

        public static void Apply(Harmony h)
        {
            // ---- layer 0: NBug is normally already prepared by Mod.Apply (before Steamworks.NET is loaded); make sure in any case ----------
            PrepareNBug();
            Log.Info("[telemetry] step: NBug prepared");

            // ---- layer 1: rewrite Program.Main / Program.CrashHandler so NBug is never wired up -------------------------------
            MethodInfo main = AccessTools.Method("Paris.Program:Main");
            MethodInfo crash = AccessTools.Method("Paris.Program:CrashHandler");
            Hooks.Patch(CrashCat, "Program.Main transpiler: NBugDiscordProtocol registration removed", main, false, typeof(Telemetry), transpiler: nameof(MainTranspiler));
            Hooks.Patch(CrashCat, "Program.CrashHandler transpiler: NBug.Handler.UnhandledException -> ShreddedBots logger", crash, false, typeof(Telemetry), transpiler: nameof(MainTranspiler));
            // Harmony runs transpilers when Patch() builds the replacement method, so the counters below are already filled
            Log.Info("[telemetry] Program.Main: Discord protocol registration removed x" + _mainNBugAddRemoved + "; Program.CrashHandler: NBug handler replaced x" + _crashHandlerReplaced);
            Hooks.Info(CrashCat, "transpiled IL matched expectations (Add removed x" + _mainNBugAddRemoved + ", handler replaced x" + _crashHandlerReplaced + ")", _mainNBugAddRemoved >= 1 && _crashHandlerReplaced >= 1);

            Log.Info("[telemetry] step: Program.Main / CrashHandler patched");
            // ---- layer 2: the game's own Discord sender chain -------------------------------------------------------------------
            Hooks.Block(CrashCat, Hooks.T("Paris.NBugDiscordProtocol"), "NBugDiscordProtocol", "Send", true, "Discord crash webhook protocol -> no-op (returns false)");
            Hooks.Block(CrashCat, Hooks.T("Paris.DiscordWebHook"), "DiscordWebHook", "SendFile", true, "Discord webhook upload -> no-op");
            Hooks.Block(CrashCat, Hooks.T("Paris.FormUpload"), "FormUpload", "MultipartFormDataPost", true, "HTTP multipart upload -> no-op", nameof(Blocks.SkipHttpResponse));
            Hooks.Block(CrashCat, Hooks.T("Paris.FormUpload"), "FormUpload", "PostForm", true, "HTTP POST -> no-op", nameof(Blocks.SkipHttpResponse));

            // ---- layer 3: NBug itself (NBug.dll in the game folder; patched by reflection) -----------------------------------
            PatchNBug();
            Log.Info("[telemetry] step: NBug patched");

            // ---- layer 4: other HTTP senders in ParisEngine (Tribute web services) ---------------------------------------------
            Type ws = Hooks.T("Paris.Engine.WebServices");
            foreach (string n in new[] { "PostRequest", "PostFileRequest", "GetRequest", "GetBinaryRequest", "GetResponseForWebRequest" })
                Hooks.Block(NetCat, ws, "WebServices", n, true, "services.tributegames.com web service -> no-op");

            // ---- layer 5: GameAnalytics (api.gameanalytics.com; Init is already empty in this build, patched anyway) ----------
            Type ga = Hooks.T("Paris.Engine.GameAnalytics");
            foreach (string n in new[] { "Init", "AnalyticsThread", "PostUserInfo", "PostInfo" })
                Hooks.Block(NetCat, ga, "GameAnalytics", n, true, "analytics -> no-op");
            Hooks.Block(NetCat, ga, "GameAnalytics", "PostToGA", true, "analytics upload -> no-op (reports success so nothing retries)", nameof(Blocks.SkipTrue));

            // ---- layer 6: NTP (DateHelper.GetNetworkTime queries pool.ntp.org over UDP; GetNistTime calls it) -------------------
            Hooks.Block(NetCat, Hooks.T("Paris.Engine.DateHelper"), "DateHelper", "GetNetworkTime", true, "NTP query to pool.ntp.org -> returns local UtcNow", nameof(Blocks.SkipNow));

            // ---- layer 7: NLog - strip any network-type log target (the shipped config only has local file/console/debugger targets) --
            MethodInfo nlogInit = AccessTools.Method("Paris.ParisXnaLogger:Init");
            Hooks.Patch(NetCat, "NLog: network/mail/webservice/database targets removed after ParisXnaLogger.Init", nlogInit, false, typeof(Telemetry), postfix: nameof(NLogPostfix));

            CheckQueuedReports();
        }

        // ------------------------------------------------------------------------------------------------ NBug
        private static Assembly LoadNBug()
        {
            Assembly nbug = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NBug");
            if (nbug != null) return nbug;
            try { return Assembly.Load("NBug"); }
            catch (Exception ex) { Hooks.Info(CrashCat, "NBug.dll could not be loaded for patching (" + ex.GetType().Name + ")", false, true); Log.Exception("NBug load failed", ex); return null; }
        }

        private static bool _nbugPrepared;

        /// <summary>Makes NBug inert BEFORE any other NBug member is touched. Called first thing from Mod.Apply (before Steamworks.NET is preloaded).
        /// Why the order matters: Harmony's patching of ANY static NBug method makes the .NET Framework run NBug.Settings' static constructor right
        /// then (RuntimeHelpers.PrepareMethod). That constructor scans every loaded assembly with an unguarded GetTypes(); with Steamworks.NET
        /// loaded it throws ReflectionTypeLoadException, NBug.Settings (and Handler) then stay permanently in a failed state. So:
        ///  (a) a finalizer on RuntimeModule.GetTypes turns that exception into "the types that did load" for callers inside NBug only,
        ///  (b) NBug.Settings is initialised explicitly - by a direct call from this assembly - before anything else, and before Steamworks.NET
        ///      is loaded (the same order as the untouched game, where Program.Main's first line initialises it),
        ///  (c) only then are NBug members patched.</summary>
        public static void PrepareNBug()
        {
            if (_nbugPrepared) return;
            _nbugPrepared = true;
            Assembly nbug = LoadNBug();
            if (nbug == null) return;
            Log.Info("[telemetry] step: NBug assembly loaded; Steamworks.NET loaded yet: " + AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name == "Steamworks.NET"));
            // (a) scoped GetTypes guard (BCL method, no NBug member involved)
            {
                // RuntimeModule.GetTypes() is what Assembly.GetTypes() calls on every runtime (it is in the stack of the failing scan)
                MethodInfo gt = typeof(object).Module.GetType().GetMethod("GetTypes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                Hooks.Patch(CrashCat, "Module.GetTypes finalizer: NBug's all-assembly type scan can no longer throw ReflectionTypeLoadException", gt, true, typeof(Telemetry), finalizer: nameof(GetTypesFinalizer));
            }
            // (b0) NBug's own internal-error window: log instead of showing (it cannot send anything, but it must not block start-up)
            Type viewer = nbug.GetType("NBug.Core.UI.Developer.InternalExceptionViewer", false);
            if (viewer == null) Hooks.NotFound(CrashCat, "NBug.Core.UI.Developer.InternalExceptionViewer (internal error window)", false);
            else
            {
                bool any = false;
                foreach (MethodInfo m in viewer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "ShowDialog" && m.GetParameters().Length > 0)
                        any |= Hooks.Patch(CrashCat, "NBug internal error window " + Hooks.Sig(m) + " -> logged only", m, false, typeof(Telemetry), prefix: nameof(NBugUiPrefix));
                if (!any) Hooks.NotFound(CrashCat, "NBug InternalExceptionViewer.ShowDialog", false);
            }
            // (b) initialise NBug.Settings exactly as the game would. Failure is critical: we cannot guarantee NBug is inert.
            try { InitNBugSettings(); }
            catch (Exception ex)
            {
                Log.Exception("NBug.Settings static initialisation failed", ex);
                Hooks.Info(CrashCat, "NBug.Settings initialised from ShreddedBots (" + ex.GetType().Name + ")", false, true);
                return;
            }
            // (c) NBug's logger would throw / show UI for internal errors: log them instead
            Type logger = nbug.GetType("NBug.Core.Util.Logging.Logger", false);
            if (logger != null)
                foreach (MethodInfo m in logger.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "Error" && !m.IsGenericMethodDefinition)
                        Hooks.Patch(CrashCat, "NBug.Logger " + Hooks.Sig(m) + " -> logged only (no window, no throw)", m, false, typeof(Telemetry), prefix: nameof(NBugUiPrefix));
            // no destination can be built from a connection string any more
            Type settings = nbug.GetType("NBug.Settings", false);
            MethodInfo add = settings == null ? null : settings.GetMethod("AddDestinationFromConnectionString", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Hooks.Patch(CrashCat, "NBug.Settings.AddDestinationFromConnectionString -> no-op (returns null)", add, true, typeof(Telemetry), prefix: nameof(AddDestinationPrefix));
        }

        public static bool AddDestinationPrefix(ref object __result) { __result = null; return false; }

        private static int _guardLogged;

        /// <summary>Finalizer for (Runtime)Module.GetTypes(): for callers inside NBug only, a ReflectionTypeLoadException becomes the array of types that did load.</summary>
        public static Exception GetTypesFinalizer(Exception __exception, ref Type[] __result)
        {
            var rtle = __exception as ReflectionTypeLoadException;
            if (rtle == null || rtle.Types == null) return __exception;
            try
            {
                bool fromNBug = false;
                foreach (var f in new System.Diagnostics.StackTrace(1, false).GetFrames() ?? new System.Diagnostics.StackFrame[0])
                {
                    MethodBase m = f.GetMethod();
                    Type dt = m == null ? null : m.DeclaringType;
                    if (dt != null && dt.Assembly.GetName().Name == "NBug") { fromNBug = true; break; }
                }
                if (!fromNBug) return __exception;
                __result = rtle.Types.Where(t => t != null).ToArray();
                if (System.Threading.Interlocked.Increment(ref _guardLogged) <= 3)
                    Log.Warn("[telemetry] NBug's all-assembly type scan hit unloadable types; continuing with the " + __result.Length + " that loaded" + Environment.NewLine + Diag.LoaderInfo(rtle));
                return null;
            }
            catch { return __exception; }
        }

        /// <summary>Direct (non-reflection) call so the CLR's calling assembly is ShreddedBots.exe in the game folder, as for TMNT.exe's own Main.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void InitNBugSettings()
        {
            int n = NBug.Settings.Destinations.Count;   // runs NBug.Settings' static constructor (reads NBug.config)
            Log.Info("[telemetry] NBug.Settings initialised (ReleaseMode=" + NBug.Settings.ReleaseMode + ", destinations configured=" + n + "); destinations are forced empty by the hooks below");
            Hooks.Info(CrashCat, "NBug.Settings initialised from ShreddedBots (config read, " + n + " destination(s))", true, true);
        }

        /// <summary>Replaces NBug's internal-error window / throwing logger: the message (with any LoaderExceptions) goes to ShreddedBots.log only.</summary>
        public static bool NBugUiPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                string text = "";
                if (__args != null)
                    foreach (object a in __args)
                    {
                        if (a == null) continue;
                        if (a is Exception) text += Environment.NewLine + "  " + a.GetType().FullName + ": " + ((Exception)a).Message + Environment.NewLine + Diag.LoaderInfo((Exception)a) + ((Exception)a).StackTrace;
                        else if (a is string) text += " " + a;
                        else if (!(a is System.Linq.Expressions.Expression)) text += " [" + a.GetType().Name + "]";
                    }
                Log.Warn("[telemetry] NBug internal error suppressed (" + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + "):" + text);
                Blocks.Hit(__originalMethod);
            }
            catch { }
            return false;
        }

        private static void PatchNBug()
        {
            Assembly nbug = LoadNBug();
            if (nbug == null) return;
            Type[] types = Diag.SafeGetTypes(nbug, "NBug patching");   // partial failure is logged with its LoaderExceptions; loadable types are still patched
            Type Get(string n) => types.FirstOrDefault(t => t.FullName == n);

            // queued-report sender: constructors (the Dispatcher starts sending from its constructor) and Dispatch()
            Type dispatcher = Get("NBug.Core.Submission.Dispatcher");
            if (dispatcher == null) Hooks.NotFound(CrashCat, "NBug.Core.Submission.Dispatcher", true);
            else
            {
                Hooks.Block(CrashCat, dispatcher, "NBug.Dispatcher", ".ctor", true, "queued-report sender constructor -> no-op");
                Hooks.Block(CrashCat, dispatcher, "NBug.Dispatcher", "Dispatch", true, "queued-report sender -> no-op");
                Hooks.Block(CrashCat, dispatcher, "NBug.Dispatcher", "EnumerateDestinations", true, "queued-report sender -> no-op");
            }

            // every built-in submit protocol (mail / HTTP / FTP / redmine / ...): Send() -> no-op
            Type iproto = Get("NBug.Core.Submission.IProtocol");
            if (iproto == null) Hooks.NotFound(CrashCat, "NBug.Core.Submission.IProtocol", true);
            else
            {
                Type discord = Hooks.T("Paris.NBugDiscordProtocol");
                foreach (Type t in types.Where(t => t.IsClass && !t.IsAbstract && iproto.IsAssignableFrom(t) && t != discord))
                    Hooks.Block(CrashCat, t, "NBug." + t.Name, "Send", true, "NBug submit protocol -> no-op (returns false)");
            }

            // Settings.Destinations is always empty (covers destinations configured in NBug.config / app.config too)
            Type settings = Get("NBug.Settings");
            MethodInfo getDest = settings?.GetProperty("Destinations", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
            Hooks.Patch(CrashCat, "NBug.Settings.Destinations getter -> always empty", getDest, true, typeof(Telemetry), postfix: nameof(DestinationsPostfix));

            // NBug.Handler: the UnhandledException getter returns our logger; NBug's own handlers (which write the Exception_*.zip) never run
            Type handler = Get("NBug.Handler");
            if (handler == null) Hooks.NotFound(CrashCat, "NBug.Handler", true);
            else
            {
                bool any = false;
                foreach (PropertyInfo p in handler.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    MethodInfo g = p.GetGetMethod(true);
                    if (g != null && Returns(g, typeof(UnhandledExceptionEventHandler)))
                    { any |= Hooks.Patch(CrashCat, "NBug.Handler." + p.Name + " getter -> ShreddedBots logger", g, true, typeof(Telemetry), prefix: nameof(HandlerGetterPrefix)); }
                }
                if (!any) Hooks.NotFound(CrashCat, "NBug.Handler.UnhandledException getter", true);
                foreach (MethodInfo m in handler.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Returns(m, typeof(void)) && SafeParamCount(m) == 2 && m.Name.EndsWith("Handler", StringComparison.Ordinal) && m.Name.IndexOf("Exception", StringComparison.Ordinal) >= 0)
                        Hooks.Patch(CrashCat, "NBug.Handler." + m.Name + " (report writer) -> no-op", m, true, typeof(Blocks), prefix: nameof(Blocks.Skip));
            }
            Type exceptions = Get("NBug.Exceptions");
            MethodInfo report = exceptions?.GetMethod("Report", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Exception) }, null);
            Hooks.Patch(CrashCat, "NBug.Exceptions.Report(Exception) manual report API (not called by the game) -> logged only", report, false, typeof(Telemetry), prefix: nameof(ManualReportPrefix));
        }

        // reflection over NBug's handler members can throw if a signature mentions a type of an assembly that is not loadable; treat that as "not a match"
        private static bool Returns(MethodInfo m, Type t) { try { return m.ReturnType == t; } catch (Exception) { return false; } }
        private static int SafeParamCount(MethodInfo m) { try { return m.GetParameters().Length; } catch (Exception) { return -1; } }

        // ------------------------------------------------------------------------------------------------ queued reports
        /// <summary>NBug (DeferredReporting=true, StoragePath=WindowsTemp) keeps unsent reports in %TEMP%\&lt;entry assembly name&gt; = %TEMP%\TMNT\Exception_*.zip.</summary>
        private static void CheckQueuedReports()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "TMNT");
                string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir, "Exception_*.zip") : new string[0];
                Log.Info("[telemetry] queued NBug crash reports in " + dir + ": " + files.Length + (files.Length > 0 ? " (they can never be sent while ShreddedBots runs the game)" : ""));
                if (files.Length > 0 && Config.PurgeQueuedCrashReports)
                {
                    int del = 0;
                    foreach (string f in files) { try { File.Delete(f); del++; } catch { } }
                    Log.Info("[telemetry] PurgeQueuedCrashReports=true: deleted " + del + " of " + files.Length + " queued report(s)");
                }
                else if (files.Length > 0) Log.Info("[telemetry] they are left on disk (set PurgeQueuedCrashReports=true in ShreddedBots.ini to delete them, or delete them by hand)");
            }
            catch (Exception ex) { Log.Warn("[telemetry] queued report check failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------------------------------------ prefixes / postfixes
        public static bool HandlerGetterPrefix(ref UnhandledExceptionEventHandler __result) { __result = SafeUnhandled; return false; }
        public static bool ManualReportPrefix(Exception exception) { Log.Exception("NBug manual report requested (not created, not sent)", exception); return false; }

        public static void DestinationsPostfix(object __result)
        {
            try
            {
                if (__result is System.Collections.IList l) { if (l.Count > 0) l.Clear(); return; }
                MethodInfo clear = __result?.GetType().GetMethod("Clear", Type.EmptyTypes);
                PropertyInfo count = __result?.GetType().GetProperty("Count");
                if (clear != null && count != null && (int)count.GetValue(__result, null) > 0) clear.Invoke(__result, null);
            }
            catch { }
        }

        public static void NLogPostfix()
        {
            try { NLogScan(); }
            catch (Exception ex) { Log.Info("[telemetry] NLog target scan skipped: " + ex.GetType().Name); }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void NLogScan()
        {
            var cfg = NLog.LogManager.Configuration;
            if (cfg == null) return;
            var remove = new List<string>();
            foreach (NLog.Targets.Target t in cfg.AllTargets)
            {
                string n = t.GetType().Name;
                if (n.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("WebService", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Mail", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Database", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Chainsaw", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("NLogViewer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Syslog", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Http", StringComparison.OrdinalIgnoreCase) >= 0)
                    remove.Add(t.Name);
            }
            foreach (string name in remove) { cfg.RemoveTarget(name); Log.Info("[telemetry] removed NLog target " + name); }
            if (remove.Count > 0) NLog.LogManager.Configuration = cfg;
            Log.Info("[telemetry] NLog targets after scan: " + cfg.AllTargets.Count + " (network-type targets removed: " + remove.Count + ")");
        }

        // ------------------------------------------------------------------------------------------------ IL
        /// <summary>Program.Main: drop "Settings.Destinations.Add(new NBugDiscordProtocol())". Program.CrashHandler: NBug.Handler.UnhandledException -> our delegate.</summary>
        public static IEnumerable<CodeInstruction> MainTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            MethodInfo safe = AccessTools.Method(typeof(Telemetry), nameof(GetSafeHandler));
            bool sawDiscordCtor = false;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode == OpCodes.Newobj && ci.operand is ConstructorInfo ctor && ctor.DeclaringType?.FullName == "Paris.NBugDiscordProtocol")
                { sawDiscordCtor = true; continue; }
                if (sawDiscordCtor && (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo add && add.Name == "Add")
                {
                    // stack: [collection, protocol] -> pop both; nothing is registered
                    list[i] = new CodeInstruction(OpCodes.Pop) { labels = ci.labels, blocks = ci.blocks };
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
                    if (add.ReturnType != typeof(void)) list.Insert(i + 2, new CodeInstruction(OpCodes.Ldc_I4_0));   // keep the stack shape if Add returned a value
                    sawDiscordCtor = false;
                    _mainNBugAddRemoved++;
                    continue;
                }
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi.DeclaringType?.FullName == "NBug.Handler" && mi.ReturnType == typeof(UnhandledExceptionEventHandler))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, safe) { labels = ci.labels, blocks = ci.blocks };
                    _crashHandlerReplaced++;
                }
            }
            return list;
        }

        public static UnhandledExceptionEventHandler GetSafeHandler() => SafeUnhandled;

        private static void SafeUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            // ShreddedBots' own handler (registered by the loader before the game starts) already wrote the exception to ShreddedBots.log.
            Log.Info("NBug handler slot reached; crash report NOT created/sent (ShreddedBots).");
        }
    }
}
