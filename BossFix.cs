using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace ShreddedBots
{
    /// <summary>One thing on the floor / in the air the bots should stay out of (M4.10, Super Shredder fight only).</summary>
    internal struct Danger
    {
        public float Cx, Cy, Hx, Hy, Vx, Vy; public byte Kind;   // 1 floor fire, 2 moving boss projectile, 3 turret blast, 4 boss avoid zone, 5 fire-trail lane
    }

    /// <summary>
    /// M4.10: what the bots know about the two Shredder fights. Everything here is empty / null in every other scene, so all the
    /// boss-only branches in the brain are skipped there. Refreshed at most every 50 ms for all bots together (like BotScene).
    /// </summary>
    internal static class BossScene
    {
        private static object _sc; private static float _t = -1f;

        // ---- Shredder (episode 15)
        internal static Shredder Real;                                   // the real one (not a shadow clone)
        internal static readonly List<Shredder> Clones = new List<Shredder>(8);
        internal static bool RealReachable;                              // real Shredder can be hit right now (visible, hurtable, not in his counter stance)
        internal static Shredder Counter;                                // nearest Shredder (real or clone) in StateCounter, null when none

        // ---- Super Shredder (episode 16)
        internal static SuperShredder Super;
        internal static readonly List<SuperShredderClone> Figs = new List<SuperShredderClone>(8);   // living corner figures
        internal static bool ShadowLink;                                 // Shadow Link state, or any figure alive
        internal static bool Invulnerable;                               // game's ShouldBotAvoid: mutagen shield up
        internal static readonly List<Danger> Dangers = new List<Danger>(48);
        internal static float ZoneX0, ZoneY0, ZoneX1, ZoneY1; internal static bool ZoneOn;
        internal static string ZoneHow = "";

        internal static bool Any { get { return Real != null || Clones.Count > 0 || Super != null; } }

        // ---- synchronized finish
        internal static bool SyncOk, Finishing, AllIn; internal static int Pending; internal static float WindowLeft, DmgEst = 1f;
        internal static int SwingsSeen, DropsSeen, AlivePlayers;
        private static float _shadowSince = -1f, _finishAt, _lastSyncLog = -99f; private static int _maxFigs;
        private const int Slots = 8;
        private static readonly int[] _lastHp = new int[Slots];
        private static readonly bool[] _hpValid = new bool[Slots], _slotSeen = new bool[Slots], _slotAlive = new bool[Slots];
        private static readonly float[] _slotDeadAt = new float[Slots];
        private static readonly Dictionary<SuperShredderClone, int> _claim = new Dictionary<SuperShredderClone, int>();
        private static readonly Dictionary<SuperShredderClone, float> _claimT = new Dictionary<SuperShredderClone, float>();
        private static readonly List<string> _dbgTmp = new List<string>();

        private static bool SafeBool(Func<bool> f, bool dflt) { try { return f(); } catch { return dflt; } }
        private static bool HurtableNow(NonPlayerCharacter n) { try { return n.CanBeHurt && !n.IsInvulnerable; } catch { return true; } }

        internal static bool InCounter(NonPlayerCharacter n)
        {
            Shredder s = n as Shredder;
            return s != null && BotController.StateNameOf(s) == "StateCounter";
        }

        private static void ResetScene()
        {
            Real = null; Clones.Clear(); RealReachable = false; Counter = null; Super = null; Figs.Clear(); ShadowLink = false; Invulnerable = false; Dangers.Clear(); ZoneOn = false;
            SyncOk = false; Finishing = false; AllIn = false; Pending = 0; WindowLeft = 0f; DmgEst = 1f; SwingsSeen = 0; DropsSeen = 0; _shadowSince = -1f; _finishAt = 0f;
            Array.Clear(_lastHp, 0, Slots); Array.Clear(_hpValid, 0, Slots); Array.Clear(_slotSeen, 0, Slots); Array.Clear(_slotAlive, 0, Slots); Array.Clear(_slotDeadAt, 0, Slots);
            _claim.Clear(); _claimT.Clear();
        }

        internal static void Update(Scene2d sc, float now)
        {
            if (sc == null) return;
            if (ReferenceEquals(sc, _sc) && now - _t < 0.05f && now >= _t) return;
            if (!ReferenceEquals(sc, _sc)) ResetScene();
            _sc = sc; _t = now;
            try { UpdateCore(sc, now); }
            catch (Exception ex) { Mod.LogOnce("boss-scene", ex); Real = null; Clones.Clear(); Super = null; Figs.Clear(); ShadowLink = false; Dangers.Clear(); Counter = null; RealReachable = false; }
        }

        private static void UpdateCore(Scene2d sc, float now)
        {
            Real = null; Clones.Clear(); Super = null; Figs.Clear(); Dangers.Clear(); Counter = null; RealReachable = false; ShadowLink = false; Invulnerable = false; ZoneOn = false;
            foreach (Enemy e in BotScene.Enemies(sc, now))
            {
                if (e == null || !e.Active) continue;
                Shredder sh = e as Shredder;
                if (sh != null) { if (sh.IsCloneVersion) Clones.Add(sh); else Real = sh; continue; }
                SuperShredder ss = e as SuperShredder;
                if (ss != null && Super == null) Super = ss;
            }
            if (Real != null || Clones.Count > 0) UpdateShredder(sc, now);
            if (Super != null)
            {
                foreach (Breakable b in BotScene.Props(sc, now))
                {
                    SuperShredderClone c = b as SuperShredderClone;
                    if (c != null && c.Active && !c.IsDead) Figs.Add(c);
                }
                bool inLinkState = SafeBool(() => Super.IsInShadowLinkState, false);
                ShadowLink = inLinkState || Figs.Count > 0;
                Invulnerable = SafeBool(() => Super.ShouldBotAvoid, false);
                BuildDangers(sc, now);
                UpdateSync(sc, now);
            }
            else if (_shadowSince >= 0f) { _shadowSince = -1f; SyncOk = false; Finishing = false; AllIn = false; Pending = 0; Array.Clear(_slotSeen, 0, Slots); Array.Clear(_slotAlive, 0, Slots); }
        }

        // ------------------------------------------------------------ Shredder
        private static void UpdateShredder(Scene2d sc, float now)
        {
            if (Real != null)
            {
                Shredder r = Real;
                bool counter = InCounter(r);
                RealReachable = r.CanBeTargeted && HurtableNow(r) && !counter && r.IsVisibleOnScreen && !SafeBool(() => r.IsInStateKO, false);
                if (counter) Counter = r;
            }
            foreach (Shredder c in Clones) if (c.Active && InCounter(c) && Counter == null) Counter = c;
        }

        /// <summary>Any Shredder in StateCounter within reach of this point? (the one the bots must not hit and back away from)</summary>
        internal static Shredder CounterNear(Vector2 me, float rx, float ry)
        {
            Shredder best = null; float bd = float.MaxValue;
            if (Real != null && Real.Active && InCounter(Real)) { Vector2 d = Real.Position2D - me; if (Math.Abs(d.X) <= rx && Math.Abs(d.Y) <= ry && Math.Abs(d.X) < bd) { bd = Math.Abs(d.X); best = Real; } }
            foreach (Shredder c in Clones) if (c.Active && InCounter(c)) { Vector2 d = c.Position2D - me; if (Math.Abs(d.X) <= rx && Math.Abs(d.Y) <= ry && Math.Abs(d.X) < bd) { bd = Math.Abs(d.X); best = c; } }
            return best;
        }

        // ------------------------------------------------------------ Super Shredder: the avoid zone and everything flying / burning
        /// <summary>The game's BotAvoidZone is relative to the boss (mutagen) or half local / half world (frame rect). Translate it, sanity-check it, else use a plain box around him.</summary>
        private static void ComputeZone()
        {
            ZoneOn = false; ZoneHow = "";
            if (Super == null || !Invulnerable) return;
            Vector2 bp = Super.Position2D;
            float x0 = 0f, y0 = 0f, x1 = 0f, y1 = 0f; bool ok = false;
            try
            {
                Rectangle r = Super.BotAvoidZone;
                int how = ZoneSane(r.X, r.Y, r.Width, r.Height, bp.X, bp.Y, out x0, out y0, out x1, out y1);
                ok = how != 0; ZoneHow = how == 1 ? "game zone (boss-relative)" : "game zone (x relative, y world)";
            }
            catch { }
            if (!ok) { x0 = bp.X - 96f; x1 = bp.X + 96f; y0 = bp.Y - 34f; y1 = bp.Y + 34f; ZoneHow = "fallback box (game zone failed the sanity check)"; }
            // never narrower than the mutagen blast box when he is doing the mutagen move
            if (BotController.StateNameOf(Super) == "StateMutagenDefect") { x0 = Math.Min(x0, bp.X - 96f); x1 = Math.Max(x1, bp.X + 96f); y0 = Math.Min(y0, bp.Y - 34f); y1 = Math.Max(y1, bp.Y + 34f); }
            ZoneX0 = x0; ZoneX1 = x1; ZoneY0 = y0; ZoneY1 = y1; ZoneOn = true;
        }

        /// <summary>Pure: translate the game's BotAvoidZone rectangle (local to the boss, or local x / world y) to world coordinates and sanity-check it. 0 = rejected, 1 = boss-relative, 2 = x relative / y world.</summary>
        internal static int ZoneSane(float rx, float ry, float rw, float rh, float bx, float by, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = y0 = x1 = y1 = 0f;
            if (!(rw >= 40f && rw <= 520f && rh >= 10f && rh <= 220f)) return 0;
            float ax0 = bx + rx;
            if (Math.Abs(ax0 + rw * 0.5f - bx) > 260f) return 0;
            float yRel = by + ry;
            if (Math.Abs(yRel + rh * 0.5f - by) <= 80f) { x0 = ax0; x1 = ax0 + rw; y0 = yRel; y1 = yRel + rh; return 1; }
            if (Math.Abs(ry + rh * 0.5f - by) <= 80f) { x0 = ax0; x1 = ax0 + rw; y0 = ry; y1 = ry + rh; return 2; }
            return 0;
        }

        /// <summary>Pure: low-health threshold for a figure: 30 % of its max health, never less than 1.5 of the biggest single hit seen.</summary>
        internal static float ThrFor(int max, float dmgEst) { return Math.Max(0.3f * max, 1.5f * dmgEst); }
        internal static bool IsLow(int hp, int max, float dmgEst) { return hp <= ThrFor(max, dmgEst); }
        /// <summary>Pure: the game's clone respawn delay for this many living (non-KO) players.</summary>
        internal static float WindowFor(int alivePlayers) { return alivePlayers >= 4 ? 2f : (alivePlayers >= 2 ? 5f : 8f); }
        /// <summary>Pure: hold the swing at a figure? (sync on, not finishing, nobody died yet, this figure already low)</summary>
        internal static bool DecideHold(bool syncOk, bool finishing, int pending, bool low) { return syncOk && !finishing && pending == 0 && low; }

        private static void BuildDangers(Scene2d sc, float now)
        {
            ComputeZone();
            if (ZoneOn) Dangers.Add(new Danger { Cx = (ZoneX0 + ZoneX1) * 0.5f, Cy = (ZoneY0 + ZoneY1) * 0.5f, Hx = (ZoneX1 - ZoneX0) * 0.5f, Hy = (ZoneY1 - ZoneY0) * 0.5f, Kind = 4 });
            Vector2 bp = Super.Position2D;
            if (BotController.StateNameOf(Super) == "StateFireTrail")
                Dangers.Add(new Danger { Cx = bp.X, Cy = bp.Y, Hx = 3000f, Hy = 26f, Kind = 5 });
            foreach (object o in sc.GetGameObjectsOfType<Projectile>())
            {
                Projectile pr = o as Projectile;
                if (pr == null || !pr.Active) continue;
                object owner = null; try { owner = pr.Owner; } catch { }
                bool ofSuper = owner is SuperShredder, ofClone = owner is SuperShredderClone;
                if (!ofSuper && !ofClone) continue;
                Vector2 pp = pr.Position2D; float vx = 0f, vy = 0f;
                try { Vector3 sp = pr.BodyComponent.Displacement.Speed; vx = sp.X; vy = sp.Y; } catch { }
                bool still = vx * vx + vy * vy < 64f;
                Danger d = new Danger { Cx = pp.X, Cy = pp.Y, Vx = vx, Vy = vy };
                if (ofClone) { d.Hx = 18f; d.Hy = 12f; d.Kind = 3; }
                else if (still) { d.Hx = 26f; d.Hy = 14f; d.Kind = 1; }
                else { d.Hx = 22f; d.Hy = 12f; d.Kind = 2; }
                Dangers.Add(d);
                if (Dangers.Count >= 44) break;
            }
        }

        /// <summary>Would a player (box half-size pad) standing at (x, y) be inside something dangerous at any time within [t0, t1] seconds?</summary>
        internal static bool Risk(float x, float y, float t0, float t1, out byte kind) { return RiskIn(Dangers, x, y, t0, t1, out kind); }
        /// <summary>Pure version of Risk over any list of dangers.</summary>
        internal static bool RiskIn(IList<Danger> list, float x, float y, float t0, float t1, out byte kind)
        {
            kind = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Danger d = list[i];
                float padX = d.Kind == 4 ? 10f : 12f, padY = d.Kind == 4 ? 6f : 6f;
                for (float t = t0; t <= t1 + 0.001f; t += 0.05f)
                {
                    float cx = d.Cx + d.Vx * t, cy = d.Cy + d.Vy * t;
                    if (Math.Abs(x - cx) <= d.Hx + padX && Math.Abs(y - cy) <= d.Hy + padY) { kind = d.Kind; return true; }
                }
            }
            return false;
        }

        internal static string KindName(byte k)
        {
            switch (k) { case 1: return "floor fire"; case 2: return "boss projectile"; case 3: return "turret blast"; case 4: return "boss avoid zone"; case 5: return "fire-trail lane"; }
            return "?";
        }

        internal static string DescribeNear(Vector2 me) { return ""; }

        // ------------------------------------------------------------ Super Shredder: shadow link figures, claims and the synchronized finish
        private static int SlotOf(SuperShredderClone c, int fallback)
        {
            int i = fallback; try { i = c.CloneIndex; } catch { }
            if (i < 0) i = fallback;
            return i < 0 ? 0 : (i >= Slots ? Slots - 1 : i);
        }

        internal static bool Hittable(SuperShredderClone c)
        {
            try { return c != null && c.Active && !c.IsDead && c.CanBeTargeted && c.CanBeHurt && !c.IsInvulnerable; } catch { return false; }
        }

        internal static float Thr(SuperShredderClone c)
        {
            int max = 0; try { max = c.MaxHP; } catch { }
            return ThrFor(max, DmgEst);
        }
        internal static bool Low(SuperShredderClone c)
        {
            try { return IsLow(c.HP, c.MaxHP, DmgEst); } catch { return false; }
        }

        private static void UpdateSync(Scene2d sc, float now)
        {
            if (!ShadowLink)
            {
                if (_shadowSince >= 0f) { _shadowSince = -1f; Finishing = false; AllIn = false; Pending = 0; Array.Clear(_slotSeen, 0, Slots); Array.Clear(_slotAlive, 0, Slots); Array.Clear(_hpValid, 0, Slots);
                }
                return;
            }
            if (_shadowSince < 0f)
            {
                _shadowSince = now; SyncOk = true; Finishing = false; AllIn = false; DmgEst = 1f; SwingsSeen = 0; DropsSeen = 0; _maxFigs = 0;
                Array.Clear(_slotSeen, 0, Slots); Array.Clear(_slotAlive, 0, Slots); Array.Clear(_hpValid, 0, Slots); Array.Clear(_slotDeadAt, 0, Slots);
            }
            int players = 0;
            foreach (Player pl in sc.Players.Values) { try { if (pl != null && pl.Active && !pl.IsInStateKO) players++; } catch { } }
            AlivePlayers = players;
            float window = WindowFor(players);

            bool read = true;
            bool[] aliveNow = new bool[Slots];
            for (int i = 0; i < Figs.Count; i++)
            {
                SuperShredderClone f = Figs[i]; int s = SlotOf(f, i);
                int hp = -1, max = 0;
                try { hp = f.HP; max = f.MaxHP; } catch { read = false; }
                if (max <= 0 || hp < 0) read = false;
                aliveNow[s] = true; _slotSeen[s] = true; _slotAlive[s] = true; _slotDeadAt[s] = 0f;
                if (_hpValid[s] && hp < _lastHp[s]) { DropsSeen++; DmgEst = Math.Max(DmgEst, _lastHp[s] - hp); }
                _lastHp[s] = hp; _hpValid[s] = hp >= 0;
            }
            float oldest = float.MaxValue; int pend = 0;
            for (int s = 0; s < Slots; s++)
            {
                if (!_slotSeen[s] || aliveNow[s]) continue;
                if (_slotAlive[s]) { _slotAlive[s] = false; _slotDeadAt[s] = now; _hpValid[s] = false; }
                if (_slotDeadAt[s] > 0f && now - _slotDeadAt[s] <= window + 2f) { pend++; if (_slotDeadAt[s] < oldest) oldest = _slotDeadAt[s]; }
            }
            Pending = pend; WindowLeft = pend > 0 ? window - (now - oldest) : window;
            AllIn = SyncOk && pend > 0;

            if (SyncOk && !read) { SyncOk = false;
            }
            if (SyncOk && SwingsSeen >= 10 && DropsSeen == 0) { SyncOk = false;
            }
            if (SyncOk && now - _shadowSince > 40f && !Finishing) { SyncOk = false;
            }

            if (Figs.Count > _maxFigs) _maxFigs = Figs.Count;
            bool allHit = Figs.Count > 0, allLow = Figs.Count > 0;
            for (int i = 0; i < Figs.Count; i++) { if (!Hittable(Figs[i])) allHit = false; if (!Low(Figs[i])) allLow = false; }
            bool all = allHit && allLow && Figs.Count >= _maxFigs && now - _shadowSince >= 2.5f;   // every figure spawned (and in reach) first
            if (SyncOk && !Finishing && all)
            {
                Finishing = true; _finishAt = now;
            }
            else if (Finishing && (Figs.Count == 0 || !allLow || now - _finishAt > 6f))
            {
                bool relower = Figs.Count > 0;
                Finishing = false;
            }
        }


        /// <summary>A figure the bot should attack: distinct nearest unclaimed living figure (or one that needs lowering when help is wanted).</summary>
        internal static SuperShredderClone PickFigure(int bot, Vector2 me, float now, SuperShredderClone cur, bool wantHelp)
        {
            SuperShredderClone best = null; float bestC = float.MaxValue;
            if (cur != null && Hittable(cur) && !wantHelp)
            {
                int o; float ct;
                bool claimedByOther = _claim.TryGetValue(cur, out o) && o != bot && _claimT.TryGetValue(cur, out ct) && now - ct < 0.7f;
                if (!claimedByOther) { Claim(cur, bot, now); return cur; }
            }
            for (int pass = 0; pass < 2 && best == null; pass++)
            {
                for (int i = 0; i < Figs.Count; i++)
                {
                    SuperShredderClone f = Figs[i];
                    if (!Hittable(f)) continue;
                    if (wantHelp && Low(f)) continue;
                    if (pass == 0)
                    {
                        int o; float ct;
                        if (_claim.TryGetValue(f, out o) && o != bot && _claimT.TryGetValue(f, out ct) && now - ct < 0.7f) continue;
                    }
                    Vector2 d = f.Position2D - me;
                    float c = Math.Abs(d.X) + 2.5f * Math.Abs(d.Y);
                    if (c < bestC) { bestC = c; best = f; }
                }
                if (wantHelp) break;
            }
            if (best != null) Claim(best, bot, now);
            return best;
        }
        private static void Claim(SuperShredderClone f, int bot, float now)
        {
            if (_claim.Count > 24) { _claim.Clear(); _claimT.Clear(); }
            _claim[f] = bot; _claimT[f] = now;
        }

        /// <summary>Hold the swing: sync is on, this figure is already low and the others are not all low yet (nobody has died yet).</summary>
        internal static bool HoldFire(SuperShredderClone f)
        {
            return f != null && ShadowLink && DecideHold(SyncOk, Finishing, Pending, Low(f));
        }

        internal static bool AnyNotLow()
        {
            for (int i = 0; i < Figs.Count; i++) if (Hittable(Figs[i]) && !Low(Figs[i])) return true;
            return false;
        }
    }
}
