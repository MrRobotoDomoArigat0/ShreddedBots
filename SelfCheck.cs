using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ShreddedBots
{
    /// <summary>
    /// Startup self-check: logs how many hooks applied per category, lists every hook that failed / was not found, resolves every
    /// type and member reference this mod makes against the game build that is actually installed (so a game update that renames
    /// something shows up in one log block), and refuses to start the game if a CRITICAL (reporting/telemetry/online) block is missing.
    /// </summary>
    internal static class SelfCheck
    {
        public static string Summary = "";
        /// <summary>True when any hook did not apply (the F7 menu shows a warning line only then).</summary>
        public static bool Problems;

        public static void Run()
        {
            List<Hooks.Entry> all; lock (Hooks.All) all = Hooks.All.ToList();
            int ok = all.Count(e => e.Status == "ok");
            Log.Info("[selfcheck] ===== HOOK SELF-CHECK =====");
            foreach (var g in all.GroupBy(e => e.Category).OrderBy(g => g.Key))
                Log.Info("[selfcheck] " + g.Key.PadRight(14) + " ok " + g.Count(e => e.Status == "ok") + " / " + g.Count() + (g.Any(e => e.Status != "ok") ? "   <-- " + g.Count(e => e.Status != "ok") + " problem(s)" : ""));
            Log.Info("[selfcheck] total hooks: " + ok + " ok, " + all.Count(e => e.Status == "FAILED") + " failed, " + all.Count(e => e.Status == "NOT FOUND") + " not found (of " + all.Count + ")");
            int patched = 0;
            try { patched = Harmony.GetAllPatchedMethods().Count(); } catch { }
            Log.Info("[selfcheck] methods currently patched by Harmony (all owners): " + patched);

            var critical = all.Where(e => e.Status != "ok" && e.Critical).ToList();
            foreach (var e in all.Where(e => e.Status != "ok"))
                Log.Warn("[selfcheck] " + e.Status + (e.Critical ? " (CRITICAL)" : "") + " [" + e.Category + "] " + e.Description + (e.Error != null ? " :: " + e.Error : ""));

            Diag.Suppress = true;
            try { ResolveRefs(); } catch (Exception ex) { Log.Warn("[selfcheck] reference resolution failed: " + ex.Message); }
            finally { Diag.Suppress = false; }

            Problems = all.Count > ok;
            Summary = ok + "/" + all.Count + " hooks ok" + (all.Count > ok ? ", " + (all.Count - ok) + " problem(s)" : "");
            if (critical.Count > 0)
            {
                Log.Error("[selfcheck] " + critical.Count + " CRITICAL block(s) are missing -> NOT starting the game (fail closed). This usually means the game was updated; ShreddedBots must be updated to match.");
                throw new FailClosedException(critical.Count + " critical block(s) could not be applied:\n- " + string.Join("\n- ", critical.Take(8).Select(e => e.Description)) + "\nSee ShreddedBots.log. The game was NOT started.");
            }
            Log.Info("[selfcheck] all critical reporting/telemetry/online blocks are in place (" + Summary + ")");
            Log.Info("[selfcheck] ===== END SELF-CHECK =====");
        }

        /// <summary>Resolves every type/member reference of this assembly (the CLR's own metadata tokens) against the installed game build.</summary>
        private static void ResolveRefs()
        {
            Module mod = typeof(SelfCheck).Module;
            var missing = new List<string>(); var seen = new HashSet<string>();
            int types = 0, members = 0, typeBad = 0, memberBad = 0;
            for (int rid = 1; rid < 200000; rid++)
            {
                try { mod.ResolveType(0x01000000 | rid); types++; }
                catch (ArgumentOutOfRangeException) { break; }
                catch (Exception ex) { typeBad++; Add(missing, seen, "type   ", ex); }
            }
            for (int rid = 1; rid < 200000; rid++)
            {
                try { mod.ResolveMember(0x0A000000 | rid); members++; }
                catch (ArgumentOutOfRangeException) { break; }
                catch (Exception ex) { memberBad++; Add(missing, seen, "member ", ex); }
            }
            Log.Info("[selfcheck] resolved " + types + " type refs and " + members + " member refs against this game build; unresolved: " + typeBad + " types, " + memberBad + " members");
            foreach (string s in missing) Log.Warn("[selfcheck] MISSING " + s);
        }

        private static void Add(List<string> missing, HashSet<string> seen, string kind, Exception ex)
        {
            string msg = ex.Message ?? ex.GetType().Name;
            int nl = msg.IndexOf('\n'); if (nl > 0) msg = msg.Substring(0, nl).Trim();
            string line = kind + ex.GetType().Name + ": " + msg;
            if (seen.Add(line)) missing.Add(line);
        }

        /// <summary>For the F7 menu: write the full hook table to the log.</summary>
        public static void DumpToLog()
        {
            List<Hooks.Entry> all; lock (Hooks.All) all = Hooks.All.ToList();
            var sb = new StringBuilder("[selfcheck] hook table (" + all.Count + "):");
            foreach (var e in all) sb.Append(Environment.NewLine).Append("  [").Append(e.Status).Append("] ").Append(e.Critical ? "(critical) " : "").Append(e.Category).Append(": ").Append(e.Description);
            Log.Info(sb.ToString());
            Log.Info("[selfcheck] blocked calls so far: " + Blocks.Summary());
        }
    }
}
