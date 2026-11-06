using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ShreddedBots
{
    /// <summary>Settings persisted to ShreddedBots.ini next to the loader. (No [Debug] section in this build.)</summary>
    public static class Config
    {
        public static string MenuKey = "F7";
        /// <summary>Delete NBug reports that the game's own crash handler queued earlier (%TEMP%\TMNT\Exception_*.zip). Default off: only logged.</summary>
        public static bool PurgeQueuedCrashReports = false;

        // ---- [Bots] (M2) ----
        public const int MaxBots = 5;
        public static bool TeamAttacks = true;                              // bots try the two-player sandwich attack with each other
        /// <summary>M4.12: bot difficulty, set in the F7 menu. 0 = Easy, 1 = Normal, 2 = Hard. Hard is the default and is exactly the 0.4.11 behaviour: every difficulty change in the brain is gated on this being below Hard.</summary>
        public const int DiffEasy = 0, DiffNormal = 1, DiffHard = 2;
        public static int BotDifficulty = DiffHard;
        public static string DifficultyName => BotDifficulty <= DiffEasy ? "Chill" : BotDifficulty >= DiffHard ? "Gnarly" : "Okay";
        // 0.4.16: how far bots roam to pick a fight. Normal is exactly the 0.4.15 engage numbers (no multiplier).
        public const int AggLow = 0, AggNormal = 1, AggHigh = 2;
        public static int BotAggression = AggNormal;
        public static string AggressionName => BotAggression <= AggLow ? "Low" : BotAggression >= AggHigh ? "High" : "Normal";
        public static int ParseDifficulty(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v == "0" || v == "chill" || v == "easy") return DiffEasy;   // M4.13: Chill (old name Easy)
            if (v == "1" || v == "okay" || v == "normal") return DiffNormal;   // Okay (old name Normal); anything else = Gnarly (old name Hard)
            return DiffHard;
        }
        public static int ParseAggression(string v)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v == "0" || v == "low") return AggLow;
            if (v == "2" || v == "high") return AggHigh;
            return AggNormal;
        }
        public static readonly string[] BotHero = new string[MaxBots] { "", "", "", "", "" };   // "" = auto (a free hero); else the hero's internal name
        public static readonly int[] BotPalette = new int[MaxBots];          // 0 = first / default palette; else palette number (2..)
        public static readonly bool[] BotOn = new bool[MaxBots] { false, false, false, false, false };   // M4.12: the per-slot switches are the only bot control (the old Enabled / Count settings are migrated on load); 0.8: default = all bots off (switch them on in the F7 menu)
        /// <summary>Bots that actually join: the slots that are switched on.</summary>
        public static int ActiveBotCount() { int n = 0; for (int i = 0; i < MaxBots; i++) if (BotOn[i]) n++; return n; }
        /// <summary>M4.12: an ini written by 0.4.11 or older has Enabled / Count: a slot was active only when Enabled was true, its index was below Count and its own switch was on. Fold that into the per-slot switches once (the next save no longer writes the two keys).</summary>
        internal static void MigrateLegacy(bool[] on, bool seenEnabled, bool enabled, bool seenCount, int count)
        {
            for (int i = 0; i < on.Length; i++)
            {
                if (seenEnabled && !enabled) on[i] = false;
                if (seenCount && i >= count) on[i] = false;
            }
        }
        public static float BotReachX = 34f;                                 // how close (horizontal) a bot stands to an enemy before swinging
        public static float BotReachY = 7f;                                  // how well it must line up in depth first
        public static float BotFollowNear = 70f, BotFollowFar = 130f;        // follows the human when farther than FollowFar, stops inside FollowNear
        public static float BotEngageRange = 190f;                           // enemies farther than this (from the bot) are ignored

        public const bool BotLog = false, VerboseLog = false, DebugOverlay = false, ShowStatusHud = false, AsyncGameLog = false, HitchDetector = false, LogEveryBlockedCall = false;

        public static string PathIni => Path.Combine(Log.DataDir, "ShreddedBots.ini");

        public static void Load()
        {
            try
            {
                if (!File.Exists(PathIni)) { Save(); Log.Info("Config: created " + PathIni + " with defaults"); return; }
                string section = "";
                bool seenSkillKey = false, seenEnabled = false, legacyEnabled = true, seenCount = false; int legacyCount = 3;
                foreach (string raw in File.ReadAllLines(PathIni))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim().ToUpperInvariant(); continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (section == "GENERAL")
                    {
                        switch (k)
                        {
                            case "menukey": MenuKey = v; break;
                            case "purgequeuedcrashreports": PurgeQueuedCrashReports = ParseBool(v, false); break;
                        }
                    }
                    else if (section == "BOTS")
                    {
                        switch (k)
                        {
                            case "enabled": legacyEnabled = ParseBool(v, true); seenEnabled = true; break;   // M4.12: legacy (migrated below)
                            case "botskill": case "skill": BotDifficulty = ParseDifficulty(v); seenSkillKey = true; break;   // M4.13: Chill / Okay / Gnarly
                            case "aggression": BotAggression = ParseAggression(v); break;   // 0.4.16: Low / Normal / High
                            case "difficulty": case "botdifficulty": if (!seenSkillKey) BotDifficulty = ParseDifficulty(v); break;   // 0.4.12 ini (Easy / Normal / Hard); a BotSkill line wins
                            case "teamattacks": TeamAttacks = ParseBool(v, true); break;
                            case "count": legacyCount = Math.Max(0, Math.Min(MaxBots, ParseInt(v, 3))); seenCount = true; break;   // M4.12: legacy (migrated below)
                            case "reachx": BotReachX = ParseF(v, 34f, 10f, 80f); break;
                            case "reachy": BotReachY = ParseF(v, 7f, 2f, 20f); break;
                            case "follownear": BotFollowNear = ParseF(v, 70f, 20f, 300f); break;
                            case "followfar": BotFollowFar = ParseF(v, 130f, 40f, 400f); break;
                            case "engagerange": BotEngageRange = ParseF(v, 190f, 60f, 400f); break;
                            default:
                                for (int i = 0; i < MaxBots; i++)
                                {
                                    if (k == "bot" + (i + 1) + "hero") BotHero[i] = v.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : v;
                                    else if (k == "bot" + (i + 1) + "on") BotOn[i] = ParseBool(v, true);
                                    else if (k == "bot" + (i + 1) + "palette") BotPalette[i] = v.Equals("first", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(0, Math.Min(12, ParseInt(v, 0)));
                                }
                                break;
                        }
                    }
                }
                MigrateLegacy(BotOn, seenEnabled, legacyEnabled, seenCount, legacyCount);
                ApplyDerived();
                Log.Info("Config loaded from " + PathIni + (seenEnabled || seenCount ? " (old Enabled / Count settings folded into the per-bot switches)" : "") + ", bot skill " + DifficultyName + ", aggression " + AggressionName);
                Save();   // rewrite so new settings appear with their defaults
            }
            catch (Exception ex) { Log.Exception("Config load failed (using defaults)", ex); }
        }

        public static void ApplyDerived()
        {
        }

        private static int _saveSeq; private static readonly object _saveLock = new object();
        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; ShreddedBots settings. Edited by the F7 menu; safe to edit by hand while the game is closed.");
                sb.AppendLine("; Crash reporting, telemetry and online play are ALWAYS blocked; there is no setting to turn that off.");
                sb.AppendLine("[General]");
                sb.AppendLine("; Key that opens the mod menu (XNA Keys name, e.g. F7, F8, Insert).");
                sb.AppendLine("MenuKey=" + MenuKey);
                sb.AppendLine("; true = delete crash reports the game queued before ShreddedBots existed (%TEMP%\\TMNT\\Exception_*.zip) at startup. false = only count and log them.");
                sb.AppendLine("PurgeQueuedCrashReports=" + B(PurgeQueuedCrashReports));
                sb.AppendLine();
                sb.AppendLine("[Bots]");
                sb.AppendLine("; Offline computer-controlled players. Each Bot N On switch below adds or removes that bot (all off = no bots). The game's own player limit still applies.");
                sb.AppendLine("; BotSkill = Chill, Okay or Gnarly (also in the F7 menu). Gnarly is the default. (Old Difficulty= / BotDifficulty= lines with Easy / Normal / Hard are still read.)");
                sb.AppendLine("BotSkill=" + DifficultyName);
                sb.AppendLine("; Aggression = Low, Normal or High (also in the F7 menu). Normal is the default and matches 0.4.15 engage distances exactly.");
                sb.AppendLine("Aggression=" + AggressionName);
                sb.AppendLine("; true = two bots may line up on both sides of an enemy and press attack together (the game's sandwich attack). false = never.");
                sb.AppendLine("TeamAttacks=" + B(TeamAttacks));
                for (int i = 0; i < MaxBots; i++)
                {
                    sb.AppendLine("; Bot " + (i + 1) + ": On = false keeps this slot out (the other bots keep their slots); hero = auto (a free hero you can play) or the hero's internal name; palette = first or a number (2, 3, ...).");
                    sb.AppendLine("Bot" + (i + 1) + "On=" + B(BotOn[i]));
                    sb.AppendLine("Bot" + (i + 1) + "Hero=" + (string.IsNullOrEmpty(BotHero[i]) ? "auto" : BotHero[i]));
                    sb.AppendLine("Bot" + (i + 1) + "Palette=" + (BotPalette[i] <= 0 ? "first" : BotPalette[i].ToString()));
                }
                sb.AppendLine("; Combat tuning (pixels). Reach = how close to stand horizontally before swinging; ReachY = depth tolerance.");
                sb.AppendLine("ReachX=" + F(BotReachX));
                sb.AppendLine("ReachY=" + F(BotReachY));
                sb.AppendLine("; The bot walks to you when farther than FollowFar and stops inside FollowNear.");
                sb.AppendLine("FollowNear=" + F(BotFollowNear));
                sb.AppendLine("FollowFar=" + F(BotFollowFar));
                sb.AppendLine("; Enemies farther than this from the bot are ignored.");
                sb.AppendLine("EngageRange=" + F(BotEngageRange));
                string text = sb.ToString(); int seq = System.Threading.Interlocked.Increment(ref _saveSeq);
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_saveLock)
                    {
                        if (seq != _saveSeq) return;
                        try { File.WriteAllText(PathIni, text); } catch (Exception ex) { Log.Exception("Config save failed", ex); }
                    }
                });
            }
            catch (Exception ex) { Log.Exception("Config save failed", ex); }
        }

        private static string B(bool b) => b ? "true" : "false";
        private static string F(float f) => f.ToString("0.#", CultureInfo.InvariantCulture);
        private static int ParseInt(string v, int def) { int n; return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : def; }
        private static float ParseF(string v, float def, float lo, float hi) { float f; if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return def; return Math.Max(lo, Math.Min(hi, f)); }

        private static bool ParseBool(string v, bool def)
        {
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return def;
        }
    }
}
