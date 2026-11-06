using System;

namespace ShreddedBots
{
    /// <summary>
    /// M4.12: bot difficulty (Config.BotDifficulty: 0 Easy, 1 Normal, 2 Hard). Hard is the default and is exactly the 0.4.11 behaviour:
    /// every helper here returns the old value (or the old random draw) on Hard and draws no extra random number, so the Hard decision path
    /// is unchanged. Easy / Normal only ever make the bot a little slower to react, less likely to dodge, and less likely to use the
    /// showy moves. They never turn off reviving, pit / gap crossing, boss targeting, supers, the boss-fight dodges or the team moves.
    /// </summary>
    public partial class BotController
    {
        private static int Diff { get { return Config.BotDifficulty; } }

        // ---- pure tables (unit tested) ----
        /// <summary>Chance to dodge: Hard = p, Normal = 75 % of p (boss attacks 90 %), Easy = 45 % of p (boss attacks 65 %).</summary>
        internal static double DodgeP(int diff, double p, bool boss)
        {
            if (diff >= Config.DiffHard) return p;
            if (diff <= Config.DiffEasy) return p * (boss ? 0.65 : 0.45);
            return p * (boss ? 0.90 : 0.75);
        }
        /// <summary>Advanced-move chance: Hard = p, Normal = half, Easy = never.</summary>
        internal static double AdvP(int diff, double p)
        {
            if (diff >= Config.DiffHard) return p;
            if (diff <= Config.DiffEasy) return 0.0;
            return p * 0.5;
        }
        /// <summary>Chance to start a grab (throw) after a combo: Hard / Normal = p, Easy = a bit more (throws are one of the basics).</summary>
        internal static double GrabP(int diff, double p) { return diff <= Config.DiffEasy ? Math.Min(0.6, p + 0.15) : p; }
        /// <summary>Extra rest after a 3+ hit combo (seconds): Hard 0, Normal 0.1, Easy 0.25.</summary>
        internal static float ComboPausePlus(int diff) { return diff >= Config.DiffHard ? 0f : (diff <= Config.DiffEasy ? 0.25f : 0.1f); }
        /// <summary>Seconds before a melee dodge may start after the attack was first seen: Hard 0, Normal 0.08, Easy 0.18.</summary>
        internal static float EvadeDelay(int diff) { return diff >= Config.DiffHard ? 0f : (diff <= Config.DiffEasy ? 0.18f : 0.08f); }
        /// <summary>Seconds before the bot sidesteps a boss-fight danger it is standing in: Hard 0, Normal 0.08, Easy 0.2.</summary>
        internal static float DangerDelay(int diff) { return diff >= Config.DiffHard ? 0f : (diff <= Config.DiffEasy ? 0.2f : 0.08f); }
        /// <summary>Seconds before the bot reacts to a hazard warning (a car in a lane): Hard 0, Normal 0.15, Easy 0.35.</summary>
        internal static float HazardDelay(int diff) { return diff >= Config.DiffHard ? 0f : (diff <= Config.DiffEasy ? 0.35f : 0.15f); }
        /// <summary>Chance that a hazard is noticed late (only when it is nearly there): Hard 0, Normal 8 %, Easy 20 %.</summary>
        internal static double HazardMiss(int diff) { return diff >= Config.DiffHard ? 0.0 : (diff <= Config.DiffEasy ? 0.20 : 0.08); }

        // ---- brain helpers ----
        /// <summary>Same as <c>Rng.NextDouble() &lt; p</c> on Hard (one draw). Normal: half the chance. Easy: never (no draw).</summary>
        private static bool RollAdv(double p)
        {
            int d = Diff;
            if (d >= Config.DiffHard) return Rng.NextDouble() < p;
            if (d <= Config.DiffEasy) return false;
            return Rng.NextDouble() < AdvP(d, p);
        }
        /// <summary>true on Hard without any draw; Normal keeps with chance <paramref name="keep"/>; Easy false.</summary>
        private static bool AdvOk(double keep)
        {
            int d = Diff;
            if (d >= Config.DiffHard) return true;
            if (d <= Config.DiffEasy) return false;
            return Rng.NextDouble() < keep;
        }
        private static double DiffDodgeP(double p, bool boss) { return DodgeP(Diff, p, boss); }
        private static double DiffGrabP(double p) { return GrabP(Diff, p); }
        private static float DiffComboPause { get { return ComboPausePlus(Diff); } }
        private static float DiffEvadeDelay { get { return EvadeDelay(Diff); } }
        private static double DiffHazardMiss { get { return HazardMiss(Diff); } }

        private float _dgSince = -1f, _dgLast = -9f;
        /// <summary>Boss-fight danger (Super Shredder / Statue): may the bot start its sidestep now? Always true on Hard; otherwise only after the danger has been on it for DangerDelay seconds.</summary>
        private bool DangerReact(float now)
        {
            int d = Diff;
            if (d >= Config.DiffHard) return true;
            if (_dgSince < 0f || now - _dgLast > 0.4f) _dgSince = now;
            _dgLast = now;
            return now - _dgSince >= DangerDelay(d);
        }

        /// <summary>Easy / Normal only: has the hazard been in view long enough, and (when it was "missed") is it nearly on us or has it been 0.9 s?</summary>

        // ---- 0.4.16 aggression (Normal returns the exact 0.4.15 numbers; no extra RNG) ----
        /// <summary>Engage range in px. Normal = Config.BotEngageRange exactly.</summary>
        internal static float AggEngageRange()
        {
            int a = Config.BotAggression;
            if (a == Config.AggNormal) return Config.BotEngageRange;
            if (a >= Config.AggHigh) return Config.BotEngageRange * 1.3f;
            return Config.BotEngageRange * 0.8f;
        }
        /// <summary>Multiplier on charge / sprint approach distances. Normal = 1 (identical thresholds).</summary>
        internal static float AggChargeHold()
        {
            int a = Config.BotAggression;
            if (a == Config.AggNormal) return 1f;
            if (a >= Config.AggHigh) return 0.85f;
            return 1.15f;
        }
        private bool HazardReady(float now, float arrivesIn)
        {
            if (now - _hzSeenAt < HazardDelay(Diff)) return false;
            if (!_hzMissed) return true;
            return arrivesIn < 0.8f || now - _hzSeenAt > 0.9f;
        }
    }
}
