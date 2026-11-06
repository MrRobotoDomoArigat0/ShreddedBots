using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace ShreddedBots
{
    /// <summary>
    /// M4.10: the Shredder (episode 15) and Super Shredder (episode 16) fixes. Every entry point is gated on the boss being in the scene
    /// (BossScene.*), so in any other fight / stage the old code paths run unchanged.
    /// </summary>
    public partial class BotController
    {
        // ------------------------------------------------------------ Shredder: dodge recognition (real Shredder AND his shadow clones)
        /// <summary>His ThunderAttack, LegendarySavate (+ Rebound / Landing) and SummonShadowClones are attacks the generic "Attack in the state name" check never saw (the log: 0 dodges against the real boss, 0.28 chance).</summary>
        private void ObserveShredder(Enemy n, Player p, Vector2 d, float face, float now)
        {
            string sn = StateNameOf(n);
            float rx, ry; bool zFree = false;
            if (sn == "StateThunderAttack") { rx = 140f; ry = 36f; }
            else if (sn == "StateLegendarySavateLanding") { rx = 70f; ry = 26f; }
            else if (sn == "StateLegendarySavate" || sn == "StateLegendarySavateRebound") { rx = 240f; ry = 40f; zFree = true; }
            else if (sn == "StateSummonShadowClones") { rx = 110f; ry = 30f; }
            else return;
            float adx = Math.Abs(d.X), ady = Math.Abs(d.Y);
            if (adx > rx || ady > ry) return;
            if (!zFree && Math.Abs(n.Position.Z - p.Position.Z) > 30f) return;
            if (ReferenceEquals(_incomingEnemy, n) && _incomingDist <= adx) return;   // the generic check already has this one
            float side = d.X >= 0f ? 1f : -1f;
            _incomingN++;
            if (adx < _incomingDist || !(_incomingEnemy is Shredder))
            {
                _incomingDist = adx; _incomingSide = side; _incomingEnemy = n; _incomingBoss = true;
                _incomingWhy = Name(n) + (((Shredder)n).IsCloneVersion ? " (clone)" : "") + " in '" + sn + "'";
            }
        }

        /// <summary>The real Shredder (or a clone) is in his counter stance and one of us is in reach: do not hit him (that triggers the armored thunder attack), step back out of reach until it ends.</summary>
        private bool CounterStepBack(Player p, Vector2 me, float now)
        {
            if (!p.OnGround || !InMoveState) return false;
            Shredder c = BossScene.CounterNear(me, 150f, 46f);
            if (c == null) return false;
            float away = me.X >= c.Position2D.X ? 1f : -1f;
            _ctlOk = false;
            if (PitNear(p, away, 70f)) _move = Vector2.Zero; else _move = new Vector2(away, 0f);
            SetIntent("back off: Shredder counter stance");
            BotLog.Evt(BotIndex, "boss-counter-backoff", Name(c) + (c.IsCloneVersion ? " (clone)" : " (real)") + " in StateCounter at dx " + (int)(c.Position2D.X - me.X) + " dy " + (int)(c.Position2D.Y - me.Y) + ", stepping back instead of hitting him", 1.5f);
            return true;
        }

        // ------------------------------------------------------------ Super Shredder: shadow link figures
        /// <summary>Shadow Link mode: every bot goes for its own nearest living corner figure. No down / enemiesAround / alone gates, no blacklist; a bot whose figure is already low helps another figure that still needs lowering.</summary>
        private bool ShadowScan(Player p, Vector2 me, float now)
        {
            SuperShredderClone cur = _target as SuperShredderClone;
            bool hold = cur != null && BossScene.HoldFire(cur);
            bool help = hold && BossScene.AnyNotLow();
            SuperShredderClone f = BossScene.PickFigure(BotIndex, me, now, cur, help);
            if (f == null && help) f = cur;   // nobody left to help: stay with this one
            if (f == null) return false;
            if (!ReferenceEquals(f, _target))
            {
                BotLog.Evt(BotIndex, "boss-figure", "figure #" + f.CloneIndex + " hp " + SafeHp(f) + "/" + f.MaxHP + " dist " + (int)(f.Position2D - me).Length() + (help ? " (helping: my figure is low)" : "") + " (was " + Name(_target) + ")", 1.0f);
                _wantGrab = false; _grabTimeout = 0f; _tgtSwings = 0; _tgtHp = SafeHp(f); _propSince = now; _climbTries = 0; _spPlan = false;
            }
            _target = f; _targetUntil = now + 1.2f;
            return true;
        }

        // ------------------------------------------------------------ Super Shredder: waiting for his stun window
        /// <summary>He is mutagen-invulnerable (the game's ShouldBotAvoid) and nothing else is left to hit: wait just outside his avoid zone, on our current side, ready to go in the moment his shield drops.</summary>
        private bool SuperStandby(Player p, Vector2 me, float now)
        {
            SuperShredder s = BossScene.Super;
            if (s == null || !BossScene.Invulnerable || BossScene.ShadowLink || !BossScene.ZoneOn) return false;
            if (!p.OnGround || !InMoveState) return false;
            Vector2 bp = s.Position2D;
            float side = Math.Abs(me.X - bp.X) < 6f ? ((BotIndex & 1) == 0 ? -1f : 1f) : Math.Sign(me.X - bp.X);
            float gx = side > 0f ? BossScene.ZoneX1 + 22f : BossScene.ZoneX0 - 22f;
            float gy = bp.Y + ((BotIndex % 5) - 2) * 9f;
            Vector2 goal = new Vector2(gx, gy);
            TargetLabel = "wait for the stun";
            if (Math.Abs(goal.X - me.X) <= 14f && Math.Abs(goal.Y - me.Y) <= 6f) { _move = Vector2.Zero; SetIntent("waiting outside his zone"); }
            else { MoveTo(goal, 12f, 5f); SetIntent("to the edge of his zone"); }
            BotLog.Evt(BotIndex, "boss-standby", "shield up (" + StateNameOf(s) + "), waiting at " + V(goal) + " (zone x " + (int)BossScene.ZoneX0 + ".." + (int)BossScene.ZoneX1 + ")", 4f);
            return true;
        }

        // ------------------------------------------------------------ Super Shredder: stay out of his zone, fire and shots
        private static readonly Vector2[] EscDirs =
        {
            new Vector2(0f, 1f), new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(-1f, 0f),
            new Vector2(0.7f, 0.7f), new Vector2(0.7f, -0.7f), new Vector2(-0.7f, 0.7f), new Vector2(-0.7f, -0.7f),
        };

        /// <summary>After the normal decision (like PitClamp): standing in / about to be hit by something he made, step out; never walk a planned step into it.</summary>
        private void BossGuard(float now)
        {
            if (BossScene.Super == null || BossScene.Dangers.Count == 0) return;
            Player p = ActivePlayer;
            if (p == null || !p.Active || !p.OnGround) return;
            bool sprinting = _sn == "StateSprint";
            if (!(InMoveState || sprinting)) return;
            Vector2 me = p.Position2D; byte kind;
            if (BossScene.Risk(me.X, me.Y, 0f, 0.3f, out kind))
            {
                if (!DangerReact(now)) return;   // M4.12: Easy / Normal take a moment to react (never on Hard)
                Vector2 esc = PickEscape(me);
                if (_sp != Sp.None) EndSprint(true, "danger");
                _move = esc; _ctlOk = false; SetIntent("sidestep " + BossScene.KindName(kind));
                BotLog.Evt(BotIndex, "boss-danger-step", BossScene.KindName(kind) + " on me at " + V(me) + " -> " + V(esc * 10f) + " | " + BossScene.DescribeNear(me), 1.0f);
                return;
            }
            if (_move.LengthSquared() > 0.01f)
            {
                byte k2;
                Vector2 dir = _move; dir.Normalize();
                if (BossScene.Risk(me.X + dir.X * 26f, me.Y + dir.Y * 26f, 0.1f, 0.35f, out k2))
                {
                    bool xOk = _move.X != 0f && !BossScene.Risk(me.X + Math.Sign(_move.X) * 26f, me.Y, 0.1f, 0.35f, out k2);
                    bool yOk = _move.Y != 0f && !BossScene.Risk(me.X, me.Y + Math.Sign(_move.Y) * 26f, 0.1f, 0.35f, out k2);
                    Vector2 was = _move;
                    if (xOk) _move = new Vector2(_move.X, 0f); else if (yOk) _move = new Vector2(0f, _move.Y); else _move = Vector2.Zero;
                    _ctlOk = false;
                    BotLog.Evt(BotIndex, "boss-danger-step", "held back from walking into " + BossScene.KindName(k2) + " (wanted " + V(was * 10f) + ", now " + V(_move * 10f) + ")", 2.0f);
                }
            }
        }

        private Vector2 PickEscape(Vector2 me)
        {
            Vector2 lead = _leader != null ? _leader.Position2D : me;
            float bestScore = float.MaxValue; Vector2 best = new Vector2(0f, me.Y >= BossScene.Super.Position2D.Y ? 1f : -1f);
            for (int i = 0; i < EscDirs.Length; i++)
            {
                Vector2 dir = EscDirs[i]; byte k; float score = 0f;
                Vector2 q1 = me + dir * 22f, q2 = me + dir * 44f;
                if (BossScene.Risk(me.X + dir.X * 11f, me.Y + dir.Y * 11f, 0f, 0.1f, out k)) score += 30f;
                if (BossScene.Risk(q1.X, q1.Y, 0.1f, 0.25f, out k)) score += 50f;
                if (BossScene.Risk(q2.X, q2.Y, 0.25f, 0.6f, out k)) score += 100f;
                score += 0.01f * Vector2.Distance(q2, lead) + ((BotIndex * 7 + i * 3) % 5) * 0.1f;
                if (score < bestScore) { bestScore = score; best = dir; }
            }
            return best;
        }

        private static bool ShadowRisk(Vector2 pos) { byte k; return BossScene.Risk(pos.X, pos.Y, 0f, 0.8f, out k); }

        private static bool NoLivesLeft(Player o)
        {
            try { return o.LifeCount <= 0; } catch { return false; }
        }
    }
}
