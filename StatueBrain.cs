using System;
using Microsoft.Xna.Framework;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace ShreddedBots
{
    /// <summary>
    /// M4.11: Statue of Tyranny fixes (episode 16, the fight before Super Shredder). Every entry point is gated on StatueScene.On / a
    /// StatueOfTyranny target, so every other scene runs the 0.4.10 code paths unchanged.
    /// </summary>
    public partial class BotController
    {
        private float _srSince = -1f;   // when the current "Statue is busy" spell began (revive pause is capped per spell)

        /// <summary>Pure: should a pending revive wait because the Statue is busy? busySince = how long the spell has lasted, scream = the Ballistic Scream state (no cap).</summary>
        internal static bool RevivePauseFor(bool busy, bool scream, float busySince, float cap)
        {
            if (!busy) return false;
            if (scream) return true;
            return busySince < cap;
        }

        /// <summary>Revives wait while the Statue attacks / slides off-screen (at most 3.5 s per spell, except the scream, which always finishes first).</summary>
        private bool StatueRevivePause(float now)
        {
            if (!StatueScene.On || !StatueScene.Busy) { _srSince = -1f; return false; }
            if (_srSince < 0f) _srSince = now;
            return RevivePauseFor(true, StatueScene.Scream, now - _srSince, 3.5f);
        }

        /// <summary>Is the Statue's real hurtbox within this bot's reach right now (so it is worth fighting it ahead of Foot Clan)?</summary>
        private bool StatueReachable(NonPlayerCharacter n, Vector2 me)
        {
            float gx, gy;
            if (!BossReach(n, me, out gx, out gy)) return false;
            return gx <= _reachX + 24f && gy <= 44f;
        }

        // ------------------------------------------------------------ dodge: Pound Wave waves, Tiara Laser lane, Ballistic Scream beam, its attack boxes and shots
        private void StatueGuard(float now)
        {
            if (!StatueScene.On || StatueScene.Dangers.Count == 0) return;
            Player p = ActivePlayer;
            if (p == null || !p.Active || !p.OnGround) return;
            if (IsHolding(A_REVIVE)) return;
            bool sprinting = _sn == "StateSprint";
            if (!(InMoveState || sprinting)) return;
            Vector2 me = p.Position2D; byte kind;
            if (StatueScene.Risk(me.X, me.Y, 0f, 0.3f, out kind))
            {
                if (!DangerReact(now)) return;   // M4.12: Easy / Normal take a moment to react (never on Hard)
                Vector2 esc = StatueEscape(me);
                if (_sp != Sp.None) EndSprint(true, "danger");
                _move = esc; _ctlOk = false; SetIntent("sidestep " + StatueDangerName(kind));
                BotLog.Evt(BotIndex, "statue-danger-step", StatueDangerName(kind) + " on me at " + V(me) + " -> " + V(esc * 10f) + " | " + StatueScene.DescribeNear(me), 1.0f);
                return;
            }
            if (_move.LengthSquared() > 0.01f)
            {
                byte k2;
                Vector2 dir = _move; dir.Normalize();
                if (StatueScene.Risk(me.X + dir.X * 26f, me.Y + dir.Y * 26f, 0.1f, 0.35f, out k2))
                {
                    bool xOk = _move.X != 0f && !StatueScene.Risk(me.X + Math.Sign(_move.X) * 26f, me.Y, 0.1f, 0.35f, out k2);
                    bool yOk = _move.Y != 0f && !StatueScene.Risk(me.X, me.Y + Math.Sign(_move.Y) * 26f, 0.1f, 0.35f, out k2);
                    Vector2 was = _move;
                    if (xOk) _move = new Vector2(_move.X, 0f); else if (yOk) _move = new Vector2(0f, _move.Y); else _move = Vector2.Zero;
                    _ctlOk = false;
                    BotLog.Evt(BotIndex, "statue-danger-step", "held back from walking into " + StatueDangerName(k2) + " (wanted " + V(was * 10f) + ", now " + V(_move * 10f) + ")", 2.0f);
                }
            }
        }

        private static string StatueDangerName(byte k)
        {
            return "danger";
        }

        private Vector2 StatueEscape(Vector2 me)
        {
            Vector2 lead = _leader != null ? _leader.Position2D : me;
            float sy = StatueScene.Statue != null ? StatueScene.Statue.Position2D.Y : me.Y;
            float bestScore = float.MaxValue; Vector2 best = new Vector2(0f, me.Y >= sy ? 1f : -1f);
            for (int i = 0; i < EscDirs.Length; i++)
            {
                Vector2 dir = EscDirs[i]; byte k; float score = 0f;
                Vector2 q1 = me + dir * 22f, q2 = me + dir * 44f;
                if (StatueScene.Risk(me.X + dir.X * 11f, me.Y + dir.Y * 11f, 0f, 0.1f, out k)) score += 30f;
                if (StatueScene.Risk(q1.X, q1.Y, 0.1f, 0.25f, out k)) score += 50f;
                if (StatueScene.Risk(q2.X, q2.Y, 0.25f, 0.6f, out k)) score += 100f;
                score += 0.01f * Vector2.Distance(q2, lead) + ((BotIndex * 7 + i * 3) % 5) * 0.1f;
                if (score < bestScore) { bestScore = score; best = dir; }
            }
            return best;
        }
    }
}
