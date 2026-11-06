using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ShreddedBots
{
    public static class Mod
    {
        public const string Name = "ShreddedBots";
        private static readonly HashSet<string> _logged = new HashSet<string>();
        public static Assembly GameAssembly;

        /// <summary>
        /// Installs every patch. Order matters: reporting/online blocks first. Throws <see cref="FailClosedException"/> if any
        /// critical block could not be applied (the loader then refuses to start the game).
        /// </summary>
        public static void Apply(Assembly game)
        {
            GameAssembly = game;
            Hooks.H = new Harmony("com.shreddedbots.mod");
            Diag.InstallFirstChanceLogger();   // logs the first loader-type exceptions anywhere in the process, with stack + LoaderExceptions

            // GetEntryAssembly -> TMNT.exe: Program.Main's single-instance check and NBug's temp folder name use it.
            Hooks.Patch("core", "Assembly.GetEntryAssembly -> TMNT.exe (in-process launch)", AccessTools.Method(typeof(Assembly), nameof(Assembly.GetEntryAssembly)), false, typeof(Mod), prefix: nameof(EntryPrefix));

            // NBug first, BEFORE Steamworks.NET (or anything else of the game) is loaded: see Telemetry.PrepareNBug.
            try { Telemetry.PrepareNBug(); }
            catch (Exception ex) { Log.Exception("NBug preparation threw", ex); throw new FailClosedException("NBug could not be made inert (" + ex.GetType().Name + ": " + ex.Message + ")."); }
            PreloadGameAssemblies();

            // 1. Crash reporting / telemetry, 2. online play. If either throws we do NOT run the game.
            try { Telemetry.Apply(Hooks.H); }
            catch (Exception ex) { Log.Exception("Telemetry blocking threw", ex); throw new FailClosedException("Crash-report / telemetry blocking could not be installed (" + ex.GetType().Name + ": " + ex.Message + ")."); }
            try { OnlineBlock.Apply(Hooks.H); }
            catch (Exception ex) { Log.Exception("Online blocking threw", ex); throw new FailClosedException("Online-play blocking could not be installed (" + ex.GetType().Name + ": " + ex.Message + ")."); }

            // 3. Non-critical helpers (a failure is logged and shown by the self-check, the game still starts)
            try { SteamFix.Apply(Hooks.H); } catch (Exception ex) { Log.Exception("Steam startup fix failed", ex); }
            try { GamePatches.Apply(Hooks.H); } catch (Exception ex) { Log.Exception("Menu / tick hooks failed (F7 menu unavailable)", ex); }
            try { BotPatches.Apply(Hooks.H); } catch (Exception ex) { Log.Exception("Bot hooks failed (bots unavailable)", ex); }
            SelfCheck.Run();   // logs hook counts + failures; throws FailClosedException on a critical failure
        }

        /// <summary>AccessTools.TypeByName only sees assemblies that are already loaded; load the game's own assemblies from the game folder first.</summary>
        private static void PreloadGameAssemblies()
        {
            foreach (string n in new[] { "ParisEngine", "ParisSerializers", "NLog", "NBug", "Steamworks.NET" })
            {
                try { if (AppDomain.CurrentDomain.GetAssemblies().All(a => a.GetName().Name != n)) Assembly.Load(n); }
                catch (Exception ex) { Log.Warn("Preload of " + n + " failed: " + ex.GetType().Name); }
            }
        }

        public static bool EntryPrefix(ref Assembly __result)
        {
            try
            {
                if (GameAssembly == null) return true;
                __result = GameAssembly;
                return false;
            }
            catch (Exception ex) { LogOnce("patch-EntryPrefix", ex); return true; }
        }

        public static void LogOnce(string key, Exception ex)
        {
            lock (_logged) { if (!_logged.Add(key + ":" + ex.GetType().Name + ":" + ex.Message)) return; }
            Log.Exception("Mod error (" + key + "), shown once", ex);
        }
    }
}
