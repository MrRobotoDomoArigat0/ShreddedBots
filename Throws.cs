using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace ShreddedBots
{
    /// <summary>
    /// 0.4.14: which throw a bot makes with the enemy it is holding (all modes, all skills). Pure: everything it needs is in <see cref="Ctx"/>, so it can be unit tested.
    /// Fwd / Back = the basic horizontal throw (bowls the enemy along the lane), Up = Fling Slam, Down = Fling Toss (instant KO with credit).
    /// Weighted pick with noise: Chill is mostly forward / back with toss / slam rare, Okay and Gnarly follow the situation (a group in a lane, others close, a pit near, low HP, alone).
    /// Never a horizontal throw toward a pit edge, never a slam with a pit near; a toss is always safe.
    /// </summary>
    internal static class ThrowPlan
    {
        internal const int Hold = -1, Fwd = 0, Back = 1, Up = 2, Down = 3;
        internal const float PitReach = 100f, PitSlamReach = 70f;

        internal struct Ctx
        {
            public int Diff;               // 0 Chill, 1 Okay, 2 Gnarly
            public bool Weak;              // the game will accept the throw (held enemy IsWeakEnoughToBeGrabbed, i.e. HP <= 4)
            public int Ahead, Behind;      // other enemies in the lane in front of / behind the bot (bowling targets)
            public int Others;             // other enemies within ~130 px (elliptical)
            public bool PitAhead, PitBehind, PitNear, HazardNear;
            public float HpFrac;
            public bool HasSuplex;         // Raph / Splinter: the game throws opposite to the stick, and a pit on either side rules out a horizontal throw
        }

        internal static string Name(int c) { return c == Fwd ? "throw-fwd" : c == Back ? "throw-back" : c == Up ? "throw-up" : c == Down ? "throw-down" : "hold"; }

        /// <summary>Returns Hold (do not press: the game would abort the grab with a pushback), Fwd, Back, Up or Down.</summary>
        internal static int Pick(Ctx c, Random rng, out string why)
        {
            if (!c.Weak) { why = "held enemy is too healthy to throw (HP above the grab threshold): not pressing, the grab resolves on its own"; return Hold; }
            bool okF = !c.PitAhead && !(c.HasSuplex && c.PitBehind);
            bool okB = !c.PitBehind && !(c.HasSuplex && c.PitAhead);
            bool slamOk = !c.PitNear && !c.PitAhead && !c.PitBehind;
            float hBase, sit;
            if (c.Diff <= 0) { hBase = 8f; sit = 0.25f; }
            else if (c.Diff == 1) { hBase = 2.0f; sit = 0.8f; }
            else { hBase = 1.2f; sit = 1.0f; }
            int sF = Math.Min(3, c.Ahead), sB = Math.Min(3, c.Behind);
            bool low = c.HpFrac <= 0.34f;
            bool alone = c.Others == 0 && c.Ahead == 0 && c.Behind == 0;
            bool crowdNoLine = c.Others >= 2 && Math.Max(sF, sB) < 2;
            float wF = okF ? (hBase * 1.15f + sit * 3.0f * sF) : 0f;
            float wB = okB ? (hBase + sit * 3.0f * sB) : 0f;
            float wD = sit * ((low ? 8f : 0f) + (crowdNoLine ? 4.0f : (c.Others >= 2 ? 0.6f : 0f)) + (c.PitNear || c.PitAhead || c.PitBehind ? 3f : 0f) + (c.HazardNear ? 2f : 0f) + 0.15f);
            float wU = slamOk ? sit * (alone ? 3.5f : (c.Others <= 1 ? 1.0f : 0.3f)) : 0f;
            // noise: no two throws in the same spot look the same
            wF *= 0.7f + 0.6f * (float)rng.NextDouble(); wB *= 0.7f + 0.6f * (float)rng.NextDouble();
            wU *= 0.7f + 0.6f * (float)rng.NextDouble(); wD *= 0.7f + 0.6f * (float)rng.NextDouble();
            float sum = wF + wB + wU + wD;
            int pick;
            if (sum <= 0.001f) pick = Down;
            else
            {
                float r = (float)rng.NextDouble() * sum;
                pick = r < wF ? Fwd : (r < wF + wB ? Back : (r < wF + wB + wU ? Up : Down));
            }
            why = Name(pick) + ": " + c.Ahead + " ahead / " + c.Behind + " behind, " + c.Others + " close, pit " + (c.PitAhead ? "ahead " : "") + (c.PitBehind ? "behind " : "") + (c.PitNear ? "near " : "") + (!c.PitAhead && !c.PitBehind && !c.PitNear ? "none " : "")
                + (c.HazardNear ? "hazard " : "") + "hp " + (int)(c.HpFrac * 100f) + "%" + (c.HasSuplex ? " suplex" : "") + " [w " + wF.ToString("0.0") + "/" + wB.ToString("0.0") + "/" + wU.ToString("0.0") + "/" + wD.ToString("0.0") + "]";
            return pick;
        }

        /// <summary>The stick direction that makes the game throw toward worldDir (Suplex users throw opposite to the press).</summary>
        internal static float PressFor(float worldDir, bool hasSuplex) { return hasSuplex ? -worldDir : worldDir; }
    }

    public partial class BotController
    {
        private static readonly System.Reflection.FieldInfo HeldFld = AccessTools.Field(typeof(Character), "_grabTarget");

        /// <summary>
        /// Step 4: pick the throw. Returns false when nothing should be pressed (a strong enemy: the game aborts the grab with a pushback; the grab times out and the enemy breaks free).
        /// On true, _move holds the stick direction and mv the move name.
        /// </summary>
        private bool ThrowDecide(Scene2d scene, Player p, Vector2 me, float now, out string mv)
        {
            mv = "throw-fwd";
            Character held = null;
            try { held = HeldFld == null ? null : HeldFld.GetValue(p) as Character; } catch { }
            ThrowPlan.Ctx c = new ThrowPlan.Ctx();
            c.Diff = Diff; c.Weak = true; c.HpFrac = 1f;
            try { if (held != null) c.Weak = held.IsWeakEnoughToBeGrabbed; } catch { }
            float face = p.HorizontalValue >= 0 ? 1f : -1f;
            if (c.Weak)
            {
                try
                {
                    foreach (Enemy e in BotScene.Enemies(scene, now))
                    {
                        if (e == null || !e.Active || e.IsDead || ReferenceEquals(e, held) || e is FootMotorcycle) continue;
                        Vector2 d = e.Position2D - me;
                        if (Math.Sqrt(d.X * d.X + 4f * d.Y * d.Y) < 130f) c.Others++;
                        if (Math.Abs(d.Y) <= 30f && Math.Abs(d.X) >= 24f && Math.Abs(d.X) <= 300f) { if (d.X * face > 0f) c.Ahead++; else c.Behind++; }
                    }
                    if (p.MaxHP > 0) c.HpFrac = p.HP / (float)p.MaxHP;
                    c.PitAhead = PitNear(p, face, ThrowPlan.PitReach); c.PitBehind = PitNear(p, -face, ThrowPlan.PitReach);
                    c.PitNear = PitNear(p, face, 140f) || PitNear(p, -face, 140f) || PitNearY(p, 1f, 60f) || PitNearY(p, -1f, 60f);
                    c.HazardNear = (_hzCur != null && now - _hzSeenAt < 3f) || (_surv && SurvScene.ZoneNear(scene, now, me, 130f));
                    c.HasSuplex = p.HasSuplex;
                }
                catch (Exception ex) { Mod.LogOnce("bot-throwctx", ex); }
            }
            string why;
            int pick = ThrowPlan.Pick(c, Rng, out why);
            if (pick == ThrowPlan.Hold)
            {
                _move = Vector2.Zero;
                BotLog.Evt(BotIndex, "throw-skip", why, 2.5f);
                SetIntent("holding an enemy (too strong to throw)");
                return false;
            }
            mv = ThrowPlan.Name(pick);
            if (pick == ThrowPlan.Up) _move = new Vector2(0f, -1f);
            else if (pick == ThrowPlan.Down) _move = new Vector2(0f, 1f);
            else _move = new Vector2(ThrowPlan.PressFor(pick == ThrowPlan.Fwd ? face : -face, c.HasSuplex), 0f);
            BotLog.Evt(BotIndex, "throw-pick", why, 0f);
            return true;
        }

        /// <summary>Is there a pit tile within dist px of the player along the Y axis (dirY = +1 / -1)?</summary>
        private bool PitNearY(Player p, float dirY, float dist)
        {
            if (p == null || dirY == 0f) return false;
            try
            {
                if (PitField() == null) return false;
                float st, w; Vector2 me = p.Position2D;
                return PitMap.Scan(PitFn, me.X, me.Y, 0f, dirY, dist, 4f, 40f, out st, out w);
            }
            catch { return false; }
        }
    }
}
