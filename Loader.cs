using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShreddedBots
{
    /// <summary>
    /// Entry point (ShreddedBots.exe, placed next to TMNT.exe). Game files are never modified: TMNT.exe's own Main is started IN THIS PROCESS
    /// after the Harmony patches are installed (no DLL injection, no appDomainManager, no separate AppDomain). Assembly.GetEntryAssembly() is
    /// patched to return TMNT.exe so the game's own entry-assembly checks behave as if it had been started normally.
    ///
    /// Usage:   ShreddedBots.exe [game arguments]            (the game's own switches, e.g. -windowed, pass straight through)
    /// Steam launch option:   "C:\...\TMNT\ShreddedBots.exe" %command%      (Steam appends the path of TMNT.exe / Launcher.exe; it is dropped)
    /// Extra switches handled here (not passed on): -nosteamid
    /// </summary>
    public static class Loader
    {
        public const string GameExe = "TMNT.exe";
        public const string BuildTag = "0.8";

        public static string VersionLabel
        {
            get
            {
                try
                {
                    var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(Loader).Assembly, typeof(AssemblyInformationalVersionAttribute));
                    return a != null ? a.InformationalVersion : typeof(Loader).Assembly.GetName().Version.ToString();
                }
                catch { return "?"; }
            }
        }

        /// <summary>Everything is wrapped: whatever goes wrong before or while the game is being prepared is written (with full stack and any
        /// LoaderExceptions) to ShreddedBots.log - or, if the log itself cannot be used, to startup-error.txt - before anything else is shown.</summary>
        public static int Main(string[] args)
        {
            try { return Start(args); }
            catch (Exception ex)
            {
                EmergencyReport(ex);
                return 1;
            }
        }

        private static void EmergencyReport(Exception ex)
        {
            try { Log.Exception("Startup failed", ex); Log.FlushForExit(); }
            catch
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShreddedBots");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "startup-error.txt"), DateTime.Now.ToString("u") + Environment.NewLine + Log.Redact(ex.ToString()) + Environment.NewLine + Diag.LoaderInfo(ex));
                }
                catch { }
            }
            Fatal("ShreddedBots failed to start (" + ex.GetType().Name + "). See ShreddedBots.log in the game folder.");
        }

        private static int Start(string[] args)
        {
            string gameDir = AppDomain.CurrentDomain.BaseDirectory;
            Log.Init(truncate: true);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Exception("Unhandled exception, terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
                Log.Info("[crash] note: the game's own crash handler (unchanged vanilla behaviour) renames Documents\\Tribute Games\\TMNT\\log.txt to log.old.txt after a crash; the game log of this run is in log.old.txt");
            };
            Log.Info("ShreddedBots " + BuildTag + " [" + VersionLabel + "]" +
                " (release build)" +
                " - unofficial fan mod, offline use only - starting");
            Log.Info("Launcher: " + typeof(Loader).Assembly.Location + " | folder: " + gameDir + " | OS: " + Environment.OSVersion + " | 64-bit process: " + Environment.Is64BitProcess + " | CLR " + Environment.Version);
            string gamePath = Path.Combine(gameDir, GameExe);
            if (!File.Exists(gamePath))
            {
                string msg = GameExe + " was not found next to ShreddedBots.exe (" + gameDir + "). Copy the ShreddedBots files into the TMNT game folder.";
                Log.Error(msg);
                Fatal(msg);
                return 2;
            }
            try { Directory.SetCurrentDirectory(gameDir); } catch { }

            string[] gameArgs = FilterArgs(args);
            Log.Info("Game arguments: " + (gameArgs.Length == 0 ? "(none)" : string.Join(" ", gameArgs)));
            SteamFix.PrepareEnvironment(gameDir, args);
            Config.Load();   // sets up Log.VerboseEnabled etc. before anything else logs

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            _gameDir = gameDir;
            Log.StartAsync();   // from here on log lines are queued and written by a background thread
            try
            {
                return Run(gamePath, gameArgs);
            }
            catch (FailClosedException ex)
            {
                Log.Error("FAIL CLOSED: " + ex.Message);
                Log.FlushForExit();
                Fatal("ShreddedBots did not start the game because a safety block could not be installed:\n\n" + ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Log.Exception("Loader failed", ex);
                Log.FlushForExit();
                Fatal("ShreddedBots failed to start (" + ex.GetType().Name + "). See ShreddedBots.log.");
                return 1;
            }
            finally { Log.FlushForExit(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string gamePath, string[] gameArgs)
        {
            Assembly game;
            try { game = Assembly.Load(AssemblyName.GetAssemblyName(gamePath)); }
            catch (Exception ex) { Log.Warn("Assembly.Load by name failed (" + ex.GetType().Name + "); using LoadFrom"); game = Assembly.LoadFrom(gamePath); }
            Log.Info("Game: " + game.GetName().Name + " build " + game.GetName().Version + " (" + GameExe + ", " + new FileInfo(gamePath).Length + " bytes)");
            Mod.Apply(game);   // throws FailClosedException when a critical block is missing

            MethodInfo main = game.EntryPoint;
            if (main == null) throw new InvalidOperationException("TMNT.exe has no entry point");
            Log.Info("Starting " + main.DeclaringType.FullName + "." + main.Name + " in-process");
            object r = main.Invoke(null, main.GetParameters().Length == 0 ? null : new object[] { gameArgs });
            Log.Info("Game returned from Main");
            return r is int i ? i : 0;
        }

        /// <summary>Drops our own switches and, for the Steam launch option "ShreddedBots.exe %command%", the path of the game / launcher that Steam appends.</summary>
        public static string[] FilterArgs(string[] args)
        {
            var l = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "-nosteamid", StringComparison.OrdinalIgnoreCase)) continue;
                string file = Path.GetFileName(a.Trim('"'));
                if (i == 0 && (string.Equals(file, "TMNT.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(file, "Launcher.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(file, "ShreddedBots.exe", StringComparison.OrdinalIgnoreCase)))
                    continue;
                l.Add(a);
            }
            return l.ToArray();
        }

        private static string _gameDir;
        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            try
            {
                string name = new AssemblyName(e.Name).Name;
                foreach (string ext in new[] { ".dll", ".exe" })
                {
                    string p = Path.Combine(_gameDir ?? AppDomain.CurrentDomain.BaseDirectory, name + ext);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
            }
            catch { }
            return null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        private static void Fatal(string text)
        {
            try { MessageBoxW(IntPtr.Zero, text, "ShreddedBots", 0x10 /* MB_ICONERROR */); } catch { }   // no-op on non-Windows
        }
    }
}
