using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace ShreddedBots
{
    /// <summary>
    /// 0.4.17-staged: leftover health-pizza pickup (with leave-behind / wall abort).
    /// Tenderizer dodge reverted to 0.4.15 (peel rules stay in BotBrain.Scan).
    /// </summary>
    public partial class BotController
    {
        // ---- Health pizza pickup ----
        private Pickup _pizzaTarget;
        private static readonly Dictionary<object, float> PizzaSeen = new Dictionary<object, float>(16);
        private static int _pizzaClaimBot = -1;
        private static object _pizzaClaimObj;
        private float _pizzaAbortAt = -1f;
        private float _pizzaClaimAt = -1f;
        // 0.4.17: a pizza claimed far ahead of the human could keep a bot sliding along a wall; claims are limited by distance and time.
        private const float PizzaLeaderMaxDist = 300f;   // claim only if pizza is near the human
        private const float PizzaLeaveBehind = 240f;     // abort if bot drifts this far from human
        private const float PizzaClaimTimeout = 10f;     // max seconds on a claim
        private const float PizzaAbortCooldown = 8f;     // chill before re-claim after abort

        private static float PizzaWaitSec()
        {
            int d = Config.BotDifficulty;
            if (d <= Config.DiffEasy) return 12f;
            if (d >= Config.DiffHard) return 8f;
            return 9f;
        }

        /// <summary>0.4.17: drop pizza claim (wall / leave-behind / timeout). Sets cooldown so we do not re-grab immediately.</summary>
        private void AbortPizza(float now, string why)
        {
            if (_pizzaTarget == null && _pizzaClaimBot != BotIndex) return;
            BotLog.Evt(BotIndex, "pizza-abort", why, 0f);
            ClearPizzaClaim();
            _pizzaAbortAt = now + PizzaAbortCooldown;
            _pizzaClaimAt = -1f;
            _goalSet = false;
        }

        private bool TryPizzaPickup(Scene2d scene, Player p, Vector2 me, float now, bool comboActive)
        {
            // Drop claim if done
            if (_pizzaTarget != null && (!_pizzaTarget.Active || p.HP >= p.MaxHP))
            {
                BotLog.Evt(BotIndex, "pizza-got", _pizzaTarget.Active ? "full HP, drop claim" : "pizza gone", 0f);
                ClearPizzaClaim();
                _pizzaClaimAt = -1f;
            }
            if (_pizzaClaimBot == BotIndex && (_pizzaClaimObj == null || (_pizzaClaimObj is Pickup pk && !pk.Active)))
            {
                ClearPizzaClaim();
                _pizzaClaimAt = -1f;
            }

            if (comboActive || IsHolding(A_REVIVE) || now < _pizzaAbortAt) return false;
            if (_incomingEnemy != null && _incomingDist < 140f) return false;
            if (BossScene.Any) return false;   // Shredder/Super scene: don't wander for pizza mid-boss-fix tools
            // 0.4.17: never override an active wall Y-slide (pizza MoveTo was cancelling the slide every tick → desk thrash)
            if (_wallSlideUntil > now) return false;
            // human low: never take food they need
            if (_leader != null && _leader.Active && !_leader.IsDead && _leader.MaxHP > 0 && _leader.HP * 2 <= _leader.MaxHP) return false;

            // Sticky claim: still enforce leave-behind / timeout / wall path (0.4.16 stuck forever on desk)
            if (_pizzaClaimBot == BotIndex && _pizzaTarget != null && _pizzaTarget.Active)
            {
                float leave = _leader != null && _leader.Active ? Vector2.Distance(_leader.Position2D, me) : 0f;
                if (leave > PizzaLeaveBehind) { AbortPizza(now, "leave-behind " + (int)leave + " from human"); return false; }
                if (_pizzaClaimAt > 0f && now - _pizzaClaimAt > PizzaClaimTimeout) { AbortPizza(now, "timeout " + (int)(now - _pizzaClaimAt) + "s"); return false; }
                if (_leader != null && _leader.Active && Vector2.Distance(_leader.Position2D, _pizzaTarget.Position2D) > PizzaLeaderMaxDist)
                { AbortPizza(now, "pizza too far from human " + (int)Vector2.Distance(_leader.Position2D, _pizzaTarget.Position2D)); return false; }
                Vector2 dir0 = _pizzaTarget.Position2D - me; if (dir0.LengthSquared() > 1f) dir0.Normalize();
                if (!AheadWalkable(me, dir0)) { AbortPizza(now, "wall toward pizza at " + V(me)); return false; }
                MoveTo(_pizzaTarget.Position2D, 8f, 8f);
                SetIntent("pizza");
                if (Vector2.Distance(me, _pizzaTarget.Position2D) < 18f) { _move = Vector2.Zero; _ctlOk = false; }
                return true;
            }

            List<Pickup> list = BotScene.HealthPizzas(scene, now);
            if (list.Count == 0) return false;

            Pickup best = null; float bestNeed = float.MaxValue;
            foreach (Pickup pizza in list)
            {
                if (pizza == null || !pizza.Active) continue;
                if (pizza.PickupDisabled || pizza.DisablePickupUntilLanding) continue;
                object key = pizza;
                float seen;
                if (!PizzaSeen.TryGetValue(key, out seen)) { PizzaSeen[key] = now; BotLog.Evt(BotIndex, "pizza-seen", NamePizza(pizza) + " at " + V(pizza.Position2D), 2f); seen = now; }
                if (now - seen < PizzaWaitSec()) continue;
                // human near / moving toward it
                if (_leader != null && _leader.Active)
                {
                    float hd = Vector2.Distance(_leader.Position2D, pizza.Position2D);
                    if (hd < 70f) continue;
                    // 0.4.17: pizza must stay near the human (no cross-map desk slam)
                    if (hd > PizzaLeaderMaxDist) continue;
                    Vector2 toP = pizza.Position2D - _leader.Position2D;
                    Vector2 hv = new Vector2(_leader.BodyComponent != null ? _leader.BodyComponent.Displacement.Speed.X : 0f,
                                            _leader.BodyComponent != null ? _leader.BodyComponent.Displacement.Speed.Y : 0f);
                    if (hd < 160f && hv.LengthSquared() > 4f && Vector2.Dot(Vector2.Normalize(toP), Vector2.Normalize(hv)) > 0.5f) continue;
                }
                // enemies near pizza or me
                bool hot = false;
                foreach (Vector2 ep in _enemyPos) if (Vector2.Distance(ep, pizza.Position2D) < 100f || Vector2.Distance(ep, me) < 80f) { hot = true; break; }
                if (hot) continue;
                if (_nearestEnemyDist < 110f) continue;
                // pit/wall: reuse AheadWalkable toward pizza
                Vector2 dir = pizza.Position2D - me; if (dir.LengthSquared() > 1f) dir.Normalize();
                if (!AheadWalkable(me, dir)) continue;

                if (p.HP >= p.MaxHP) continue;
                float need = p.HP; // lowest HP wins
                float dist = Vector2.Distance(me, pizza.Position2D);
                float score = need * 1000f + dist;
                // only one claim globally
                if (_pizzaClaimBot >= 0 && _pizzaClaimBot != BotIndex && ReferenceEquals(_pizzaClaimObj, pizza)) continue;
                if (score < bestNeed) { bestNeed = score; best = pizza; }
            }
            if (best != null)
            {
                if (!AmLowestHpHurtBot(p, best.Position2D)) best = null;
            }
            if (best == null) return false;

            if (_pizzaClaimBot != BotIndex || !ReferenceEquals(_pizzaClaimObj, best))
            {
                _pizzaClaimBot = BotIndex; _pizzaClaimObj = best; _pizzaTarget = best;
                _pizzaClaimAt = now;
                BotLog.Evt(BotIndex, "pizza-claim", NamePizza(best) + " HP " + p.HP + "/" + p.MaxHP + " at " + V(best.Position2D), 0f);
            }
            MoveTo(best.Position2D, 8f, 8f);
            SetIntent("pizza");
            // arrived: stay briefly; walk-over collects
            if (Vector2.Distance(me, best.Position2D) < 18f) { _move = Vector2.Zero; _ctlOk = false; }
            return true;
        }

        private bool AmLowestHpHurtBot(Player meP, Vector2 pizzaPos)
        {
            int myHp = meP.HP;
            float myDist = Vector2.Distance(meP.Position2D, pizzaPos);
            foreach (BotController o in All)
            {
                if (o == null || !o.Live || o == this) continue;
                Player op = o.ActivePlayer; if (op == null || !op.Active || op.IsDead || op.MaxHP <= 0) continue;
                if (op.HP >= op.MaxHP) continue;
                if (op.HP < myHp) return false;
                if (op.HP == myHp && Vector2.Distance(op.Position2D, pizzaPos) + o.BotIndex * 0.01f < myDist + BotIndex * 0.01f) return false;
            }
            return true;
        }

        private void ClearPizzaClaim()
        {
            if (_pizzaClaimBot == BotIndex) { _pizzaClaimBot = -1; _pizzaClaimObj = null; }
            _pizzaTarget = null;
        }

        private static string NamePizza(Pickup p)
        {
            if (p == null) return "pizza";
            try { return p.GetType().Name + (p.Name != null ? " '" + p.Name + "'" : ""); } catch { return "pizza"; }
        }
    }
}
