using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Paris.Engine.Scene;
using Paris.Game;
using Paris.Game.Actor;
using Paris.Game.Damage;
using Paris.Game.System;

namespace ShreddedBots
{
    /// <summary>
    /// 0.4.14: Survival-only knowledge. Everything in here is reached only behind "GameMode == Survival" (BotController._surv); in every other mode this file is never called.
    /// </summary>
    internal static class SurvScene
    {
        internal struct Zone { public float X, Y, Rx, Ry; public byte Kind; }   // 1 burn, 2 electric, 3 grenade

        private static readonly List<SurvivalPortal> _ports = new List<SurvivalPortal>(4);
        private static readonly List<Zone> _zones = new List<Zone>(8);
        private static object _psc, _zsc; private static float _pt = -9f, _zt = -9f;

        internal static bool On()
        {
            try { return GameInfo.Singleton != null && GameInfo.Singleton.GameMode == GameMode.Survival; } catch { return false; }
        }

        /// <summary>Open (votable) portals; the spawn portal is never open for voting. Refreshed every 0.2 s.</summary>
        internal static List<SurvivalPortal> Portals(Scene2d sc, float now)
        {
            if (ReferenceEquals(sc, _psc) && now - _pt < 0.2f && now >= _pt) return _ports;
            _psc = sc; _pt = now; _ports.Clear();
            try
            {
                foreach (object o in sc.GetGameObjectsOfType<SurvivalPortal>())
                {
                    SurvivalPortal sp = o as SurvivalPortal;
                    if (sp != null && sp.Active && sp.IsOpen && sp.PortalType != SurvivalPortalType.Spawn) _ports.Add(sp);
                }
            }
            catch (Exception ex) { Mod.LogOnce("surv-portals", ex); }
            return _ports;
        }

        /// <summary>Stationary enemy-owned fire / electric / grenade projectiles (the floor zones). Refreshed every 0.15 s. The radii are estimates (no zone class exists in the game).</summary>
        internal static List<Zone> Zones(Scene2d sc, float now)
        {
            if (ReferenceEquals(sc, _zsc) && now - _zt < 0.15f && now >= _zt) return _zones;
            _zsc = sc; _zt = now; _zones.Clear();
            try
            {
                foreach (object o in sc.GetGameObjectsOfType<Projectile>())
                {
                    Projectile pr = o as Projectile;
                    if (pr == null || !pr.Active) continue;
                    object owner = null; try { owner = pr.Owner; } catch { }
                    if (!(owner is Enemy)) continue;
                    byte kind = 0;
                    try { var a = pr.CurrentAttack; if (a != null) { if (a.DamageType == DamageType.Burn) kind = 1; else if (a.DamageType == DamageType.Electric) kind = 2; } } catch { }
                    if (kind == 0 && pr.GetType().Name.IndexOf("Grenade", StringComparison.Ordinal) >= 0) kind = 3;
                    if (kind == 0) continue;
                    float vx = 0f, vy = 0f; try { Vector3 sp = pr.BodyComponent.Displacement.Speed; vx = sp.X; vy = sp.Y; } catch { }
                    if (vx * vx + vy * vy >= 100f) continue;   // still in flight: it is a projectile, not a zone yet
                    Vector2 pp = pr.Position2D;
                    _zones.Add(new Zone { X = pp.X, Y = pp.Y, Rx = kind == 3 ? 56f : (kind == 2 ? 40f : 44f), Ry = 18f, Kind = kind });
                    if (_zones.Count >= 12) break;
                }
            }
            catch (Exception ex) { Mod.LogOnce("surv-zones", ex); }
            return _zones;
        }

        internal static bool ZoneNear(Scene2d sc, float now, Vector2 pos, float r)
        {
            List<Zone> z = Zones(sc, now);
            for (int i = 0; i < z.Count; i++) if (Math.Abs(z[i].X - pos.X) <= r + z[i].Rx && Math.Abs(z[i].Y - pos.Y) <= r * 0.5f + z[i].Ry) return true;
            return false;
        }
    }

    /// <summary>Pure Survival rules (unit tested).</summary>
    internal static class SurvRules
    {
        internal const float ReviveRadius = 100f, PortalHoldSecs = 4.5f, PortalSeekMax = 14f, UnreachableSecs = 6f;

        /// <summary>Do not start a revive: 3+ enemies within ~100 px of the downed player, a ranged attacker aiming at the spot, or (right after a hit interrupted us) any enemy there that is attacking.</summary>
        internal static bool ReviveUnsafe(int nearDown, int aimed, bool recentlyInterrupted, int attackingNear)
        {
            return nearDown >= 3 || aimed > 0 || (recentlyInterrupted && attackingNear >= 1);
        }

        /// <summary>A roll (revive-roll, escape roll, wakeup roll) is cut when a pit edge is close on either side of the roll axis or in the lane direction.</summary>
        internal static bool RollBlocked(bool pitAhead, bool pitBehind, bool pitUp, bool pitDown) { return pitAhead || pitBehind || pitUp || pitDown; }

        /// <summary>The portal every bot walks into: the one the human voted for, else (sticky) the one already chosen, else the nearest to the anchor (the human, or a fixed bot).</summary>
        internal static int PickPortal(float[] px, float[] py, bool[] humanVoted, float ax, float ay, int sticky)
        {
            int n = px == null ? 0 : px.Length; if (n == 0) return -1;
            for (int i = 0; i < n; i++) if (humanVoted != null && i < humanVoted.Length && humanVoted[i]) return i;
            if (sticky >= 0 && sticky < n) return sticky;
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < n; i++) { float dx = px[i] - ax, dy = py[i] - ay, d = dx * dx + dy * dy; if (d < bd) { bd = d; best = i; } }
            return best;
        }

        /// <summary>Stage next to the portal (do not enter) while the human can still vote and has not: 5 bots must not outvote the human. Ends after PortalHoldSecs.</summary>
        internal static bool PortalHold(bool humanCanVote, bool humanVoted, float elapsed) { return humanCanVote && !humanVoted && elapsed < PortalHoldSecs; }

        /// <summary>At most 2 bots go after a ranged attacker: the third (others already on a ranged target >= 2) does not.</summary>
        internal static bool RangedCrowded(int othersOnRanged) { return othersOnRanged >= 2; }

        /// <summary>Drop a target we are not getting closer to (and not hitting) after UnreachableSecs.</summary>
        internal static bool DropUnreachable(float secsNoProgress, float dist) { return secsNoProgress >= UnreachableSecs && dist > 70f; }

        internal static bool InZone(float x, float y, float zx, float zy, float rx, float ry, float pad)
        {
            float a = (x - zx) / (rx + pad), b = (y - zy) / (ry + pad);
            return a * a + b * b <= 1f;
        }

        /// <summary>Which way out of a zone: away from its centre on the cheaper axes, never toward a pit side.</summary>
        internal static void ZoneExit(float x, float y, float zx, float zy, float rx, float ry, float towardX, bool pitPosX, bool pitNegX, bool pitPosY, bool pitNegY, out float mx, out float my)
        {
            float dx = x - zx, dy = y - zy;
            float sx = dx > 1f ? 1f : (dx < -1f ? -1f : (towardX >= 0f ? 1f : -1f));
            float sy = dy > 1f ? 1f : (dy < -1f ? -1f : 1f);
            if ((sx > 0f && pitPosX) || (sx < 0f && pitNegX)) sx = -sx;
            if ((sx > 0f && pitPosX) || (sx < 0f && pitNegX)) sx = 0f;
            if ((sy > 0f && pitPosY) || (sy < 0f && pitNegY)) sy = -sy;
            if ((sy > 0f && pitPosY) || (sy < 0f && pitNegY)) sy = 0f;
            mx = sx; my = sy;
        }
    }

    public partial class BotController
    {
        // ---- 0.4.14 Survival state (only touched when _surv)
        private bool _surv;
        private object _psScene; private SurvivalPortal _psPortal;
        private float _psSince = -1f, _psDoneUntil, _psExitUntil, _psInsideSince = -1f, _psExitDir = 1f;
        private float _srhSince = -1f, _survIntAt = -999f, _geSince = -1f, _zoneSince = -1f;
        private int _survNearDown, _survNearDownAtk, _survAimDown;
        private NonPlayerCharacter _twT; private float _twSince, _twBest;

        // ------------------------------------------------------------ B1: portal seek
        /// <summary>The vote is open and we can vote: everyone walks into the same portal. Returns true when it owns this frame.</summary>
        private bool SurvPortalStep(Scene2d scene, Player p, Vector2 me, float now)
        {
            if (!ReferenceEquals(_psScene, scene)) { _psScene = scene; _psSince = -1f; _psDoneUntil = 0f; _psPortal = null; _psExitUntil = 0f; _psInsideSince = -1f; }
            List<SurvivalPortal> ports = SurvScene.Portals(scene, now);
            if (ports.Count == 0) { _psSince = -1f; _psPortal = null; _psInsideSince = -1f; _psDoneUntil = 0f; return false; }
            GamePlayerInfo gi = p.GamePInfo;
            bool canVote = false;
            for (int i = 0; i < ports.Count; i++) if (ports[i].CanSubmitVote(gi)) { canVote = true; break; }
            if (!canVote)
            {
                if (_psSince >= 0f) BotLog.Evt(BotIndex, "portal-voted", "voted / being pulled in; " + PortalVotes(ports), 0f);
                _psSince = -1f; _psInsideSince = -1f;
                return false;
            }
            if (now < _psDoneUntil) return false;
            if (_psSince < 0f) { _psSince = now; BotLog.Evt(BotIndex, "portal-seen", ports.Count + " open portal(s); " + PortalVotes(ports), 0f); }
            if (now - _psSince > SurvRules.PortalSeekMax) { _psDoneUntil = now + 12f; _psSince = -1f; BotLog.Evt(BotIndex, "portal-timeout", "no vote after " + (int)SurvRules.PortalSeekMax + " s, back to following", 0f); return false; }

            // who voted what, and the anchor everybody agrees on
            Player human = null; float hd = float.MaxValue; bool humanCanVote = false;
            float[] px = new float[ports.Count], py = new float[ports.Count]; bool[] hv = new bool[ports.Count]; bool humanVoted = false;
            foreach (Player o in scene.Players.Values)
            {
                if (o == null || ReferenceEquals(o, p) || !o.Active || o.IsDead || o.PInfo == null || IsBotPlayer(o)) continue;
                float d = Vector2.DistanceSquared(o.Position2D, me);
                if (d < hd) { hd = d; human = o; }
                try { if (o.GamePInfo != null && o.GamePInfo.CanVote) humanCanVote = true; } catch { }
                for (int i = 0; i < ports.Count; i++) { try { if (ports[i].Votes.Contains(o.GamePInfo)) { hv[i] = true; humanVoted = true; } } catch { } }
            }
            for (int i = 0; i < ports.Count; i++) { Vector2 pp0 = ports[i].Position2D; px[i] = pp0.X; py[i] = pp0.Y; }
            Vector2 anchor = human != null ? human.Position2D : AnchorBotPos(me);
            int idx = SurvRules.PickPortal(px, py, hv, anchor.X, anchor.Y, _psPortal == null ? -1 : ports.IndexOf(_psPortal));
            if (idx < 0) return false;
            SurvivalPortal tgt = ports[idx];
            if (!ReferenceEquals(tgt, _psPortal))
            {
                _psPortal = tgt;
                BotLog.Evt(BotIndex, "portal-seek", "going to " + tgt.PortalType + " portal at " + V(tgt.Position2D) + " (" + (humanVoted ? "the human voted it" : (human != null ? "nearest to the human" : "nearest")) + "); " + PortalVotes(ports), 0f);
            }
            bool hold = SurvRules.PortalHold(humanCanVote, humanVoted, now - _psSince);
            Vector2 pp = tgt.Position2D;
            TargetLabel = "portal"; SetIntent(hold ? "portal: waiting for the human's vote" : "walking into the portal");
            bool touching = false; try { touching = tgt.IsTouching(p); } catch { }
            if (touching && !hold)
            {
                _move = Vector2.Zero;   // the vote registers on the portal's own tick
                if (_psInsideSince < 0f) _psInsideSince = now;
                if (now - _psInsideSince > 0.9f) { _psExitUntil = now + 0.9f; _psExitDir = me.X >= pp.X ? 1f : -1f; _psInsideSince = -1f; BotLog.Evt(BotIndex, "portal-reenter", "inside the portal without a vote: stepping out and back in", 0f); }
                return true;
            }
            _psInsideSince = -1f;
            float stageDx = tgt.PortalReach.X + 26f;
            if (now < _psExitUntil) { MoveTo(new Vector2(pp.X + _psExitDir * stageDx, pp.Y), 4f, 4f); return true; }
            if (hold)
            {
                float side = me.X >= pp.X ? 1f : -1f;
                BotLog.Evt(BotIndex, "portal-hold", "staged at the " + tgt.PortalType + " portal, waiting for the human's vote (" + (now - _psSince).ToString("0.0") + " s of " + SurvRules.PortalHoldSecs.ToString("0.0") + ")", 3f);
                MoveTo(new Vector2(pp.X + side * stageDx, pp.Y), 6f, 4f);
                return true;
            }
            float dx = pp.X - me.X, dy = pp.Y - me.Y;
            if (_sp == Sp.None && _sprintCd <= 0f && p.OnGround && InMoveState && Math.Abs(dx) > 220f && Math.Abs(dx) > 1.5f * Math.Abs(dy)) StartSprint(Math.Sign(dx), pp.X, false, "portal seek, " + (int)Math.Abs(dx) + " px away", now);
            MoveTo(pp, 5f, 3f);
            return true;
        }

        private Vector2 AnchorBotPos(Vector2 me)
        {
            BotController first = null;
            foreach (BotController c in All) { if (c.ActivePlayer == null || !c.ActivePlayer.Active) continue; if (first == null || c.BotIndex < first.BotIndex) first = c; }
            return first != null && first.ActivePlayer != null ? first.ActivePlayer.Position2D : me;
        }

        private static string PortalVotes(List<SurvivalPortal> ports)
        {
            var sb = new System.Text.StringBuilder("votes");
            for (int i = 0; i < ports.Count; i++) { int n = 0; try { n = ports[i].Votes.Count; } catch { } sb.Append(' ').Append(ports[i].PortalType).Append('=').Append(n); }
            return sb.ToString();
        }

        /// <summary>While the vote is open nobody leaves the portal run for a revive that is not right there (the vote timer is 7 s).</summary>
        private bool SurvSkipRevive(Scene2d scene, Vector2 me, float now, Player down)
        {
            try
            {
                List<SurvivalPortal> ports = SurvScene.Portals(scene, now);
                if (ports.Count == 0) return false;
                Player p = ActivePlayer; if (p == null) return false;
                bool can = false; for (int i = 0; i < ports.Count; i++) if (ports[i].CanSubmitVote(p.GamePInfo)) { can = true; break; }
                return can && Vector2.Distance(down.Position2D, me) > 110f;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ B2: careful revives
        private void SurvScanReset() { _survNearDown = 0; _survNearDownAtk = 0; _survAimDown = 0; }

        /// <summary>Called per enemy in the scan while a downed ally is ours to revive.</summary>
        private void SurvNearDown(Enemy n, Vector2 downPos, float now)
        {
            Vector2 d = n.Position2D - downPos;
            if (d.Length() <= SurvRules.ReviveRadius) { _survNearDown++; if (AttackingNow(n)) _survNearDownAtk++; }
            if (IsRangedAttacker(n, now) && AimsAt(n, downPos)) _survAimDown++;
        }

        /// <summary>A ranged enemy in an attack / aim state, facing the spot, roughly in its lane.</summary>
        private static bool AimsAt(Enemy n, Vector2 spot)
        {
            try
            {
                string sn = StateNameOf(n);
                bool acting = sn.IndexOf("Aim", StringComparison.Ordinal) >= 0 || sn.IndexOf("Shoot", StringComparison.Ordinal) >= 0 || sn.IndexOf("Attack", StringComparison.Ordinal) >= 0 || (sn.IndexOf("Throw", StringComparison.Ordinal) >= 0 && sn.IndexOf("Thrown", StringComparison.Ordinal) < 0);
                if (!acting) return false;
                Vector2 d = spot - n.Position2D;
                if (Math.Abs(d.Y) > 28f || Math.Abs(d.X) > 520f) return false;
                float face = n.HorizontalValue >= 0 ? 1f : -1f;
                return d.X * face > 0f;
            }
            catch { return false; }
        }

        /// <summary>True = do not start the revive now (fight to clear instead). Never aborts one that is already running.</summary>
        private bool SurvReviveHold(float now)
        {
            if (IsHolding(A_REVIVE)) return false;
            bool unsafe0 = SurvRules.ReviveUnsafe(_survNearDown, _survAimDown, now - _survIntAt < 6f, _survNearDownAtk);
            if (!unsafe0) { _srhSince = -1f; return false; }
            if (_srhSince < 0f) _srhSince = now;
            if (now - _srhSince > 12f) { BotLog.Evt(BotIndex, "revive-hold-timeout", "still not clear after 12 s: going in anyway", 4f); return false; }
            BotLog.Evt(BotIndex, "revive-hold-off", "ally down; " + _survNearDown + " enemies within 100 px, " + _survAimDown + " aiming at the spot, " + _survNearDownAtk + " attacking: fighting to clear first", 2f);
            return true;
        }

        // ------------------------------------------------------------ B3: no rolls toward a pit
        private bool SurvRollBlocked(Player p, float now)
        {
            try
            {
                float face = p.HorizontalValue >= 0 ? 1f : -1f;
                bool b = SurvRules.RollBlocked(PitNear(p, face, 80f), PitNear(p, -face, 80f), PitNearY(p, -1f, 40f), PitNearY(p, 1f, 40f));
                if (b) BotLog.Evt(BotIndex, "roll-pit-cut", "pit edge close: no roll / backflip here (a quick stand / stay instead)", 2f);
                return b;
            }
            catch { return false; }
        }

        /// <summary>Gap crossing for something other than following the human: wait at the edge while enemies are on the near side and the human has not crossed.</summary>
        private bool SurvHoldEdge(bool leaderAcross, float now)
        {
            if (leaderAcross || _nearestEnemyDist >= 200f) { _geSince = -1f; return false; }
            if (_geSince < 0f) _geSince = now;
            if (now - _geSince > 12f) return false;
            BotLog.Evt(BotIndex, "gap-hold", "pit ahead, target across it, enemies on this side (" + (int)_nearestEnemyDist + " px) and the human has not crossed: waiting at the edge", 4f);
            return true;
        }

        // ------------------------------------------------------------ B4: ranged attackers in pairs
        private bool SurvRangedCrowded(Enemy n, float now)
        {
            int c = 0;
            foreach (BotController o in All)
            {
                if (ReferenceEquals(o, this) || !o.Live) continue;
                Enemy te = o._target as Enemy;
                if (te != null && o.IsRangedAttacker(te, now)) c++;
            }
            bool crowded = SurvRules.RangedCrowded(c);
            if (crowded && !ReferenceEquals(_target, n)) BotLog.Evt(BotIndex, "ranged-cap", "2 bots are already on ranged attackers: staying with the human", 4f);
            return crowded;
        }

        private bool SurvFarFromHuman(Enemy n)
        {
            return _leader != null && Vector2.Distance(n.Position2D, _leader.Position2D) > Config.BotEngageRange * 1.5f;
        }

        // ------------------------------------------------------------ B5: unreachable target
        private void SurvTargetWatch(Player p, Vector2 me, float now)
        {
            NonPlayerCharacter t = _target;
            if (t == null || t is Boss || t is Breakable || !t.Active) { _twT = null; return; }
            float dist = Vector2.Distance(t.Position2D, me);
            if (!ReferenceEquals(_twT, t)) { _twT = t; _twSince = now; _twBest = dist; return; }
            if (dist < _twBest - 12f || now - _lastHitAt < 2f) { _twBest = Math.Min(dist, _twBest); _twSince = now; return; }
            if (!SurvRules.DropUnreachable(now - _twSince, dist)) return;
            BotLog.Evt(BotIndex, "unreachable-target", Name(t) + " at " + (int)dist + " px: no progress for " + (now - _twSince).ToString("0.0") + " s, dropping it and returning to the human", 0f);
            _black[t] = now + 10f; _target = null; _scanT = 0f; _tgtSwings = 0; _giveUpUntil = 0f; _twT = null;
        }

        // ------------------------------------------------------------ B6: burn / grenade / electric zones
        private bool SurvZoneStep(Scene2d scene, Player p, Vector2 me, float now)
        {
            if (!p.OnGround) return false;
            if (_comboHits > 0 && now - _lastHitAt < 0.5f) return false;
            List<SurvScene.Zone> zs = SurvScene.Zones(scene, now);
            for (int i = 0; i < zs.Count; i++)
            {
                SurvScene.Zone z = zs[i];
                if (!SurvRules.InZone(me.X, me.Y, z.X, z.Y, z.Rx, z.Ry, 6f)) continue;
                if (_zoneSince < 0f) _zoneSince = now;
                if (now - _zoneSince > 3f) return false;   // do not dance forever
                float mx, my;
                SurvRules.ZoneExit(me.X, me.Y, z.X, z.Y, z.Rx, z.Ry, _leader != null ? _leader.Position2D.X - me.X : 1f, PitNear(p, 1f, 50f), PitNear(p, -1f, 50f), PitNearY(p, 1f, 24f), PitNearY(p, -1f, 24f), out mx, out my);
                _move = new Vector2(mx, my);
                SetIntent("stepping out of a " + (z.Kind == 1 ? "fire" : (z.Kind == 2 ? "electric" : "grenade")) + " zone");
                BotLog.Evt(BotIndex, "zone-step", (z.Kind == 1 ? "burn" : (z.Kind == 2 ? "electric" : "grenade")) + " zone at " + V(new Vector2(z.X, z.Y)) + ", me " + V(me), 2f);
                return true;
            }
            _zoneSince = -1f;
            return false;
        }

        // ------------------------------------------------------------ B7: dev-only Survival log
        [Conditional("SB_DEBUG")]
        private void SurvLogTick(Scene2d scene, Player p, float now)
        {
        }
    }
}
