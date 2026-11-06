using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine.Physic;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace ShreddedBots
{
    /// <summary>
    /// M4.11: what the bots know about the Statue of Tyranny fight (episode 16, before Super Shredder). Everything here is empty / false in every
    /// other scene, so every Statue-only branch in the brain is skipped there (other bosses, Shredder / Super Shredder and normal play are untouched).
    /// Refreshed at most every 50 ms for all bots together.
    /// </summary>
    internal static class StatueScene
    {
        private static object _sc; private static float _t = -1f;

        internal static StatueOfTyranny Statue;
        internal static bool On;                    // a living Statue of Tyranny is in the scene
        internal static string State = "";          // the Statue's FSM state name (StatePoundWave, StateTiaraLaser, ...)
        internal static bool Busy;                  // attacking / sliding: revives wait (see StatueBrain.StatueRevivePause)
        internal static bool Scream;                // StateBallisticScream (slides off-screen, comes back, screen-wide beam)
        internal static bool Hidden;                // not targetable right now (off-screen slide, KO)
        internal static readonly List<Danger> Dangers = new List<Danger>(48);   // Kind 2 projectile, 6 laser lane, 7 attack box, 8 scream lane

        private static readonly AccessTools.FieldRef<StatueOfTyranny, Vector3> AtkPos = MakeAtkPos();
        private static readonly AccessTools.FieldRef<StatueOfTyranny, byte> NextState = MakeNext();
        private static AccessTools.FieldRef<StatueOfTyranny, Vector3> MakeAtkPos() { try { return AccessTools.FieldRefAccess<StatueOfTyranny, Vector3>("_attackPosition"); } catch { return null; } }
        private static AccessTools.FieldRef<StatueOfTyranny, byte> MakeNext() { try { return AccessTools.FieldRefAccess<StatueOfTyranny, byte>("_moveToAttackNextState"); } catch { return null; } }

        /// <summary>Pure: the states in which the Statue is attacking or sliding about (revives wait, bots stay out of its lanes).</summary>
        internal static bool IsBusyState(string sn)
        {
            switch (sn)
            {
                case "StatePoundWave": case "StateMoveToAttackPosition": case "StateTiaraLaser": case "StateReturnFromAttackPosition": case "StateMaraPalms":
                case "StatePreBallisticScream": case "StateBallisticScream": case "StatePreSleepMode": case "StatePostSleepMode":
                    return true;
            }
            return false;
        }

        private static void Reset()
        {
            Statue = null; On = false; State = ""; Busy = false; Scream = false; Hidden = false; Dangers.Clear();
        }

        internal static void Update(Scene2d sc, float now)
        {
            if (sc == null) return;
            if (ReferenceEquals(sc, _sc) && now - _t < 0.05f && now >= _t) return;
            if (!ReferenceEquals(sc, _sc)) Reset();
            _sc = sc; _t = now;
            try { UpdateCore(sc, now); }
            catch (Exception ex) { Mod.LogOnce("statue-scene", ex); Reset(); }
        }

        private static void UpdateCore(Scene2d sc, float now)
        {
            StatueOfTyranny st = null;
            foreach (Enemy e in BotScene.Enemies(sc, now))
            {
                StatueOfTyranny s = e as StatueOfTyranny;
                if (s != null && s.Active) { st = s; break; }
            }
            if (st == null) { if (On || Dangers.Count > 0) Reset(); return; }
            bool dead = false; try { dead = st.IsDead; } catch { }
            Statue = st; On = !dead; Dangers.Clear();
            if (!On) { Busy = false; Scream = false; Hidden = true; return; }
            State = BotController.StateNameOf(st);
            Busy = IsBusyState(State); Scream = State == "StateBallisticScream";
            Hidden = SafeBool(() => !st.CanBeTargeted);
            BuildDangers(sc);
        }

        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        private static void BuildDangers(Scene2d sc)
        {
            StatueOfTyranny st = Statue;
            Vector2 sp = st.Position2D;
            // 1) the Statue's own live attack boxes (arms, floor wave, laser, scream beam): whatever the game is damaging players with right now
            int n = 0; try { n = st.GetAttackBoxCount(); } catch { }
            for (int i = 0; i < n && i < 16; i++)
            {
                try
                {
                    Rectangle r = st.GetAttackBox(i, true);
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    CollisionFlag f = st.GetAttackBoxType(i);
                    if ((f & CollisionFlag.PLAYER) == 0 || !Busy) continue;   // only while it is attacking / sliding: no constant sidestepping from a resting body box
                    Danger d; if (BoxDanger(r, st.Get3DAttackBoxDepth(i), sp.Y, 7, out d)) Dangers.Add(d);
                }
                catch { }
            }
            // 2) projectiles it fired (Mara Palms shots, Pound Wave floor waves): box, speed
            foreach (object o in sc.GetGameObjectsOfType<Projectile>())
            {
                Projectile pr = o as Projectile;
                if (pr == null || !pr.Active) continue;
                object owner = null; try { owner = pr.Owner; } catch { }
                if (!(owner is StatueOfTyranny)) continue;
                Vector2 pp = pr.Position2D; float vx = 0f, vy = 0f;
                try { Vector3 v = pr.BodyComponent.Displacement.Speed; vx = v.X; vy = v.Y; } catch { }
                Danger d = new Danger { Cx = pp.X, Cy = pp.Y, Hx = 26f, Hy = 14f, Vx = vx, Vy = vy, Kind = 2 };
                try
                {
                    if (pr.GetAttackBoxCount() > 0)
                    {
                        Rectangle r = pr.GetAttackBox(0, true);
                        Danger b;
                        if (r.Width > 0 && BoxDanger(r, pr.Get3DAttackBoxDepth(0), pp.Y, 2, out b)) { d.Cx = b.Cx; d.Hx = Math.Max(14f, Math.Min(b.Hx, 160f)); d.Hy = Math.Max(10f, Math.Min(b.Hy, 40f)); d.Cy = pp.Y; }
                    }
                }
                catch { }
                Dangers.Add(d);
                if (Dangers.Count >= 44) break;
            }
            // 3) lanes we can predict: the Tiara laser fires left along the Statue's own row; the Ballistic Scream beam too (row picked when the state starts)
            if (State == "StateTiaraLaser") Dangers.Add(Lane(sp.X, sp.Y, 26f, 6));
            else if (State == "StateMoveToAttackPosition" && NextIsTiara()) { Vector3 ap = AttackPos(sp); Dangers.Add(Lane(ap.X, ap.Y, 26f, 6)); }
            else if (State == "StateBallisticScream") { Vector3 ap = AttackPos(sp); Dangers.Add(Lane(ap.X, ap.Y, 36f, 8)); }
        }

        private static Danger Lane(float fromX, float y, float hy, byte kind)
        {
            return new Danger { Cx = fromX - 1300f, Cy = y, Hx = 1300f, Hy = hy, Kind = kind };
        }

        private static bool NextIsTiara() { try { return NextState != null && NextState(Statue) == 22; } catch { return false; } }
        private static Vector3 AttackPos(Vector2 fallback)
        {
            try { if (AtkPos != null) { Vector3 v = AtkPos(Statue); if (v.X > 1f) return v; } } catch { }
            return new Vector3(fallback.X, fallback.Y, 0f);
        }

        /// <summary>Pure: a world rectangle plus a depth range as a Danger box. The depth range may be empty (then +-24 around the object's row).</summary>
        internal static bool BoxDanger(Rectangle r, Paris.Engine.Types.Range z, float rowY, byte kind, out Danger d)
        {
            d = new Danger();
            if (r.Width <= 0) return false;
            float zl = Math.Min(z.Min, z.Max), zh = Math.Max(z.Min, z.Max);
            if (zh - zl < 6f) { zl = rowY - 24f; zh = rowY + 24f; }
            d.Cx = (r.Left + r.Right) * 0.5f; d.Hx = r.Width * 0.5f; d.Cy = (zl + zh) * 0.5f; d.Hy = Math.Max(10f, (zh - zl) * 0.5f); d.Kind = kind;
            return true;
        }

        internal static bool Risk(float x, float y, float t0, float t1, out byte kind) { return BossScene.RiskIn(Dangers, x, y, t0, t1, out kind); }

        internal static string DescribeNear(Vector2 me) { return ""; }
    }
}
