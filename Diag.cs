using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ShreddedBots
{
    /// <summary>Loader-exception diagnostics: describes ReflectionTypeLoadException / TypeInitializationException / TypeLoadException in full
    /// (first 10 LoaderExceptions) and logs the first occurrences of such exceptions anywhere in the process, with the stack that threw them.</summary>
    public static class Diag
    {
        public const int MaxLoaderExceptions = 10;
        [ThreadStatic] private static bool _inHandler;
        public static volatile bool Suppress;   // set while our own self-check deliberately probes bad metadata tokens
        private static readonly HashSet<string> _seen = new HashSet<string>();
        private static int _logged;

        /// <summary>Extra text for loader-type exceptions (empty for others): "LoaderExceptions (n, first 10): ...".</summary>
        public static string LoaderInfo(Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                for (int depth = 0; ex != null && depth < 8; depth++, ex = ex.InnerException)
                {
                    var rtle = ex as ReflectionTypeLoadException;
                    if (rtle != null)
                    {
                        Exception[] le = rtle.LoaderExceptions ?? new Exception[0];
                        int ok = rtle.Types == null ? 0 : rtle.Types.Count(t => t != null);
                        sb.AppendLine("  ReflectionTypeLoadException: " + ok + " type(s) loaded, " + le.Length + " failed; LoaderExceptions (first " + MaxLoaderExceptions + "):");
                        int i = 0;
                        foreach (Exception l in le.Where(l => l != null).GroupBy(l => l.Message).Select(g => g.First()))
                        {
                            if (i++ >= MaxLoaderExceptions) break;
                            sb.AppendLine("    [" + i + "] " + l.GetType().Name + ": " + FirstLine(l.Message));
                        }
                    }
                    var tle = ex as TypeLoadException;
                    if (tle != null && !string.IsNullOrEmpty(tle.TypeName)) sb.AppendLine("  TypeLoadException type: " + tle.TypeName);
                    var tie = ex as TypeInitializationException;
                    if (tie != null) sb.AppendLine("  TypeInitializationException for: " + tie.TypeName);
                    var fnf = ex as System.IO.FileNotFoundException;
                    if (fnf != null && !string.IsNullOrEmpty(fnf.FileName)) sb.AppendLine("  missing file/assembly: " + fnf.FileName);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string FirstLine(string s) { if (s == null) return ""; int nl = s.IndexOf('\n'); return (nl > 0 ? s.Substring(0, nl) : s).Trim(); }

        /// <summary>Logs the first 25 distinct loader-type exceptions thrown anywhere (even if the owner catches them), with stack.</summary>
        public static void InstallFirstChanceLogger()
        {
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                if (_inHandler || Suppress) return;
                Exception ex = e.Exception;
                if (!(ex is ReflectionTypeLoadException || ex is TypeInitializationException || ex is TypeLoadException || ex is BadImageFormatException)) return;
                _inHandler = true;
                try
                {
                    string key = ex.GetType().Name + ":" + FirstLine(ex.Message);
                    lock (_seen) { if (_logged >= 25 || !_seen.Add(key)) return; _logged++; }
                    string stack = "";
                    try { stack = string.Join(Environment.NewLine, new StackTrace(2, false).ToString().Split('\n').Take(16).Select(l => "    " + l.TrimEnd())); } catch { }
                    Log.Warn("[diag] first-chance " + ex.GetType().Name + ": " + FirstLine(ex.Message) + Environment.NewLine + LoaderInfo(ex) + "  thrown from:" + Environment.NewLine + stack);
                }
                catch { }
                finally { _inHandler = false; }
            };
        }

        /// <summary>Enumerates the loadable types of an assembly. A ReflectionTypeLoadException is logged (first 10 LoaderExceptions) and the types that did load are returned.</summary>
        public static Type[] SafeGetTypes(Assembly a, string who)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                Log.Warn("[diag] " + who + ": " + a.GetName().Name + ".GetTypes() partially failed" + Environment.NewLine + LoaderInfo(e));
                return (e.Types ?? new Type[0]).Where(t => t != null).ToArray();
            }
            catch (Exception e)
            {
                Log.Warn("[diag] " + who + ": " + a.GetName().Name + ".GetTypes() failed: " + e.GetType().Name + ": " + FirstLine(e.Message));
                return new Type[0];
            }
        }
    }
}
