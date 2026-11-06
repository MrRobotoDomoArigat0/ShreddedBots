using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.System;
using Paris.Engine.Scene;
using Paris.Engine.Graphics.Playfield;
using Paris.Engine.Physic;
using Paris.Game;
using Paris.Game.Actor;
using Paris.Game.Actor.Camera;
using Paris.Game.Damage;
using Paris.Game.System;
using Paris.System.FSM;

namespace ShreddedBots
{
    /// <summary>
    /// The bot's decisions (0.4.0). Priority: mash out of grabs / burn / freeze holds > wakeup roll / quick stand > escape roll when hurt > revive (only when no
    /// threat near the ally / us) > throw a grabbed enemy > flee from a lit barrel > airborne follow-up (double jump, super-air, backflip kick) > sprint sequence >
    /// bikers (timed jump kick / normal attack as the bike arrives) > super / radical mode > fight (ranged and attacking enemies first, then the committed combo
    /// target, then the nearest; props only when no enemy is near, barrels only with nobody next to them) > follow the human (give up and wait when stuck).
    /// Everything is done by "pressing buttons" through the normal controller path; nothing here teleports or cheats.
    /// </summary>
    public partial class BotController
    {
        private static readonly Random Rng = new Random();

        // ---- cooldowns / timers
        private float _scanT, _tapT, _comboPause, _jumpCd, _tauntCd, _superCd, _grabSince = -1f, _airTapAt = -1f, _holdSwingCd;
        private float _ledgeCd, _bfKickAt = -1f, _bfKickCd, _riseCd, _sprintCd, _radicalCd, _cheerCd;
        private int _missTaps, _lastHits;
        private NonPlayerCharacter _target; private float _targetUntil;
        private bool _wasHurt, _wantGrab;
        private float _reachX = -1f, _tolY = -1f;
        private int _nearCount, _threatNearMe, _threatNearAlly, _skippedNoHurt; private float _nearestEnemyDist = 9999f;
        private int _superRange, _superFacing; private float _superSide = 1f; private bool _rangedThreat;
        private Player _leader;
        private string _airWhy = "";

        // ---- M4: situational dodges, multi-bot coordination
        private float _evadeCd, _surroundCd, _backKickCd, _sandCd, _sideUntil, _sideDir = 1f, _evadeRollAt = -9f;
        private object _evadeRollFor; private bool _evadeDo;
        private int _leftNear, _rightNear, _frontClose, _behindClose, _incomingN;
        private float _incomingSide, _incomingDist; private bool _incomingBoss; private Enemy _incomingEnemy; private string _incomingWhy = "";
        private Enemy _behindEnemy, _behindCand, _projEnemy; private object _sandRolledFor; private float _evadeAirUntil, _projAt, _projDy, _projSide;

        // ---- combat awareness
        private float _hurtAt = -999f, _lastHitAt = -999f; private int _comboHits, _lastMyHp = -1;
        private string _prevSn = "";

        // ---- state read from an actor's FSM (state method names, e.g. "StateIdle"); cached per actor type because the ids differ between types
        private static readonly AccessTools.FieldRef<Actor2d, FSM> FsmOf = MakeFsmRef();
        private static readonly Dictionary<Type, Dictionary<byte, string>> StateNames = new Dictionary<Type, Dictionary<byte, string>>();
        private string _sn = ""; private byte _sid = 255;
        private static AccessTools.FieldRef<Actor2d, FSM> MakeFsmRef()
        {
            try { return AccessTools.FieldRefAccess<Actor2d, FSM>("_fsm"); } catch { return null; }
        }
        /// <summary>Exposed for tests: the state method name for an actor, "" when unreadable.</summary>
        internal static string StateNameOf(Actor2d p)
        {
            if (FsmOf == null || p == null) return "";
            try
            {
                FSM f = FsmOf(p); byte id = f.CurrentState; string n;
                Dictionary<byte, string> map;
                Type t = p.GetType();
                if (!StateNames.TryGetValue(t, out map)) StateNames[t] = map = new Dictionary<byte, string>();
                if (!map.TryGetValue(id, out n))
                {
                    n = f.CurrentStateName ?? "";
                    if (n != "Invalid" && n != "None") map[id] = n;
                }
                return n;
            }
            catch { return ""; }
        }
        private void ReadState(Player p)
        {
            _prevSn = _sn;
            _sn = StateNameOf(p); _sid = FsmOf == null ? (byte)255 : SafeId(p);
        }
        private static byte SafeId(Actor2d p) { try { return FsmOf(p).CurrentState; } catch { return 255; } }
        private bool InMoveState => _sn == "StateIdle" || _sn == "StateWalk";
        private bool InAttackName => _sn.StartsWith("StateStationaryAttack", StringComparison.Ordinal) || _sn.StartsWith("StateMovingAttack", StringComparison.Ordinal);
        /// <summary>Held in place and mashing helps: grab reactions (ids 40-50), frozen / burnt / electrocuted (75-77), or any state name saying so.</summary>
        private bool InGrabReaction
        {
            get
            {
                if (_sn.EndsWith("End", StringComparison.Ordinal)) return false;
                if ((_sid >= 40 && _sid <= 50) || (_sid >= 75 && _sid <= 77)) return true;
                return _sn.IndexOf("GrabReaction", StringComparison.Ordinal) >= 0 || _sn == "StateFrozenReaction" || _sn == "StateBurntReaction" || _sn == "StateElectrocutedReaction";
            }
        }

        // ---- reflection helpers for protected / private game fields (all optional: a failure only disables the feature)
        private static AccessTools.FieldRef<Character, T> CharRef<T>(string name) { try { return AccessTools.FieldRefAccess<Character, T>(name); } catch { return null; } }
        private static AccessTools.FieldRef<Player, bool> PlayerBoolRef(string name) { try { return AccessTools.FieldRefAccess<Player, bool>(name); } catch { return null; } }
        private static readonly AccessTools.FieldRef<Character, DamageInfoEx> DmgRef = CharRef<DamageInfoEx>("_currentAttackDamageInfo");
        private static readonly AccessTools.FieldRef<Character, Character> GrabInstRef = CharRef<Character>("_grabInstigator");
        private static readonly AccessTools.FieldRef<Player, bool> UnlockAirRef = PlayerBoolRef("_hasUnlockedSpecialAttackAir");
        private static readonly AccessTools.FieldRef<Player, bool> UnlockDodgeRef = PlayerBoolRef("_hasUnlockedSpecialAttackDodge");
        private static readonly AccessTools.FieldRef<Player, bool> UnlockRadRef = PlayerBoolRef("_hasUnlockedRadicalMode");

        /// <summary>The attack hit something and we are in the part of the swing where the game accepts the next press (0.3.0 pressed blindly every 0.2 s and half the presses were wasted).</summary>
        private bool ChainOpen(Player p)
        {
            try
            {
                if (!InAttackName) return false;
                var st = p.CurrentAttackState;
                if (st != Character.AttackStates.Delivery && st != Character.AttackStates.Recovery) return false;
                DamageInfoEx d = DmgRef == null ? null : DmgRef(p);
                return d == null || d.HasHit;
            }
            catch { return false; }
        }
        private static Character GrabHolder(Player p)
        {
            try { Character c = GrabInstRef == null ? null : GrabInstRef(p); return c != null && c.Active && !c.IsKO ? c : null; } catch { return null; }
        }

        // ---- unlock gating (power-level abilities of the bot's hero, read from the game; everything stays in the moveset and switches on by itself)
        private bool _unlockAir = true, _unlockDodge = true, _unlockRad = true; private float _unlockT; private string _lockedKey = "";
        private void UpdateUnlocks(Player p, float now)
        {
            if (now < _unlockT) return;
            _unlockT = now + 1f;
            try
            {
                if (UnlockAirRef != null) _unlockAir = UnlockAirRef(p);
                if (UnlockDodgeRef != null) _unlockDodge = UnlockDodgeRef(p);
                if (UnlockRadRef != null) _unlockRad = UnlockRadRef(p);
            }
            catch { _unlockAir = _unlockDodge = _unlockRad = true; }
            var l = new List<string>();
            if (!_unlockAir) l.Add("super-air");
            if (!_unlockDodge) l.Add("super-dodge");
            if (!_unlockRad) l.Add("radical-mode");
            string key = string.Join(",", l.ToArray());
            if (key != _lockedKey) { _lockedKey = key; BotLog.Evt(BotIndex, "unlock-change", "locked now: " + (key.Length == 0 ? "nothing" : key), 0f); }
            BotLog.SetLocked(BotIndex, l.ToArray());
        }

        // ---- expectation tracking: did the game enter the state we expected after the press?
        private string _expMove; private float _expUntil; private string[] _expNames;
        private readonly Dictionary<string, int> _noted = new Dictionary<string, int>();
        private void Expect(string move, float window, params string[] names)
        {
            if (!BotLog.On) return;
            _expMove = move; _expUntil = BotManager.Time + window; _expNames = names;
        }
        private void CheckExpect(float now)
        {
            if (_expMove == null) return;
            foreach (string n in _expNames)
                if (_sn.IndexOf(n, StringComparison.Ordinal) >= 0)
                {
                    BotLog.Result(BotIndex, _expMove, true);
                    NoteState(_expMove, "ok", _sn);
                    _expMove = null; return;
                }
            if (now > _expUntil)
            {
                BotLog.Result(BotIndex, _expMove, false);
                NoteState(_expMove, "FAIL", _sn);
                _expMove = null;
            }
        }
        private void NoteState(string move, string kind, string state)
        {
            string k = move + ":" + kind + ":" + state; int n; _noted.TryGetValue(k, out n);
            if (n >= 2) return; _noted[k] = n + 1;
            BotLog.Note(BotIndex, "state-" + move + "-" + kind, move + " -> " + kind + " (player state '" + state + "')", 0f);
        }

        // ---- sprint sequence
        private enum Sp { None, Stop, Press1, Release, Press2, Running }
        private Sp _sp = Sp.None; private float _spT, _spStarted, _spGoalX; private bool _spAttack, _spPlan, _spSawSprint; private string _spWhy = "";
        private bool _sprintJp; private float _sprintDirX;

        // ---- stuck / terrain
        private Vector2 _wantMove, _stuckRefPos; private float _stuckRefT, _stuckClearAt, _jumpDirUntil;
        private int _stuckLevel; private Vector2 _jumpDir;
        private bool _goalSet, _higherGoal; private Vector2 _goal; private float _goalTolX, _goalDz;
        private bool _ctlOk;
        private float _stuckSince = -1f, _detourUntil, _detourDir = 1f, _giveUpUntil, _lastGiveUp; private int _giveUps, _climbTries;
        // 0.4.15: solid-wall (height sentinel) slide — do not hop into short.MaxValue ahead
        private float _wallSlideUntil; private float _wallSlideDir = 1f; private Vector2 _wallSpot; private int _wallSpotGives; private bool _wallForceSlide, _lastWallAhead;
        private float _dblAt = -1f, _dblStartZ, _dblMaxZ, _lastGroundZ, _airStartZ; private bool _dblWatch, _wasAir;

        // ---- grab
        private bool _grabbed; private float _grabAt, _mashT; private int _mashTaps, _mashStep; private string _grabNote = "";

        // ---- knockdown / rolls
        private bool _kdActive, _kdLogged; private float _kdStart, _kdTapT; private int _kdMode;
        private bool _escRollWant, _escRollLogged; private float _escTapT;

        // ---- explosive barrels
        private float _fleeArmAt = -1f, _fleeUntil; private Vector2 _fleePos; private float _fleeR; private int _barrelTaps; private Breakable _barrelObj;
        private readonly Dictionary<object, bool> _seenProps = new Dictionary<object, bool>();

        // ---- super
        private float _superPendUntil;

        // ---- revive
        private float _reviveBlockedUntil, _reviveWaitSince = -1f, _reviveHoldAt = -999f; private bool _reviveEvt;

        // ---- M4.1: surf / autoscroll stages, hazards, bosses, revive, super watch
        private bool _surfNow, _wasSurf; private float _lineSince = -1f, _surfWatchT = -99f;
        private int _attackAtAlly;
        private Hazard _hzCur; private float _hzSeenAt; private bool _hzMissed; private float _hzGuardUntil, _hzLo, _hzHi, _hzDir = 1f, _hzLogAt = -9f;
        private bool _superWatch; private float _superWatchUntil; private int _superFails, _superStacksAt;
        private NonPlayerCharacter _bossLogged;

        // ---- bikers
        private bool _bikeJumpPlanV; private bool _bikeJumpPlan { get { return _bikeJumpPlanV; } set { _bikeJumpPlanV = value && AdvOk(0.5); } }   /* M4.12: Hard keeps the plan as rolled; Normal drops half of the jump kick plans, Easy all of them (RunBiker itself is unchanged) */ private object _bikeCur; private float _bikeActedAt;
        // M4.7: the bike kick's air tap waits for the jump to be high enough for the game to turn it into a dive kick (> 50 px); the watch below logs what each bike pass did (no-op in this build)
        private float _bikeKickWaitUntil = -1f;

        // ---- target bookkeeping
        private readonly Dictionary<NonPlayerCharacter, float> _black = new Dictionary<NonPlayerCharacter, float>();
        private int _tgtHp, _tgtSwings; private float _propSince, _blackPrune;
        private object _lastScene;
        private readonly Dictionary<Type, bool> _rangedTypes = new Dictionary<Type, bool>();

        private void SetIntent(string s) { Intent = s; }
        private static string Name(object o) { return o == null ? "none" : o.GetType().Name; }
        private static string V(Vector2 v) { return (int)v.X + "," + (int)v.Y; }

        private void Think(float dt)
        {
            _ctlOk = false; _goalSet = false; _higherGoal = false; _sprintJp = false; _goalDz = 0f;
            float now = BotManager.Time;
            Scene2d sc0 = Scene2d.Active;
            if (!ReferenceEquals(sc0, _lastScene))
            {
                if (_lastScene != null) { BotLog.StageEnd(BotIndex, "scene change"); }
                _lastScene = sc0; _black.Clear(); _target = null; _seenProps.Clear(); _fleeUntil = 0f; _fleeArmAt = -1f; _giveUpUntil = 0f;
                _stuckLevel = 0; _stuckSince = -1f; _giveUps = 0; _climbTries = 0; _wallSpotGives = 0; _wallForceSlide = false; _wallSlideUntil = 0f; _bikeCur = null; _comboHits = 0;
                if (_pair != null && (_pair.A == this || _pair.B == this)) EndPair(_pair, "scene change", false, now);
                _evadeRollFor = null; _behindEnemy = null; _incomingEnemy = null; _projEnemy = null;
            }
            ThinkCore(dt, now);
            try { LaneGuard(now); } catch { }
            try { BossGuard(now); } catch (Exception ex) { Mod.LogOnce("bot-bossguard", ex); }   // M4.10: Super Shredder fight only (BossScene.Super != null)
            try { StatueGuard(now); } catch (Exception ex) { Mod.LogOnce("bot-statueguard", ex); }   // M4.11: Statue of Tyranny fight only (StatueScene.On)
            try { PitClamp(now); } catch (Exception ex) { Mod.LogOnce("bot-pitclamp", ex); }   // M4.6: whatever decided the move, a ground step into a pit tile is cut
            if (_ctlOk) { try { AfterThink(dt, now); } catch (Exception ex) { Mod.LogOnce("bot-after", ex); } }
            _wantMove = _move;
        }

        private void ThinkCore(float dt, float now)
        {
            Info = FindMyInfo();
            if (Info == null) { Status = "no info"; return; }
            Scene2d scene = Scene2d.Active;
            Player p = scene == null ? null : scene.GetPlayer(Info) as Player;
            ActivePlayer = p;
            if (p == null || !p.Active || p.Scene == null) { Status = "waiting"; Intent = ""; TargetLabel = ""; _target = null; _sp = Sp.None; _grabbed = false; return; }
            if (Owner == null) Owner = p;
            if (_reachX < 0f) { _reachX = Config.BotReachX; _tolY = Config.BotReachY; }
            // config can change at runtime (F7 menu / ini): re-clamp the adaptive values to the configured maxima
            _reachX = Math.Min(_reachX, Config.BotReachX); _tolY = Math.Min(_tolY, Config.BotReachY);

            ReadState(p);
            CheckExpect(now);
            _surv = SurvScene.On(); if (_surv) SurvLogTick(scene, p, now);   // 0.4.14: Survival-only rules (portal seek, careful revives, pit-safe rolls, ranged pairs, zones); dev log
            BossScene.Update(scene, now);   // M4.10: Shredder / Super Shredder knowledge (empty in every other scene)
            StatueScene.Update(scene, now);   // M4.11: Statue of Tyranny knowledge (empty / off in every other scene)
            if (_cheerTapAt >= 0f && _sn.IndexOf("CheerUp", StringComparison.Ordinal) >= 0) { _cheerTapAt = -1f; _cheerTries = 0; _cheerSince = -1f; _hcSince = -1f; _cheerCd = 10f; }   // the press took
            UpdateUnlocks(p, now);
            BotLog.Observe(BotIndex, false, _sn, Info.TotalHitCount, p.Position.Z, dt, now, p.OnGround);
            try { PitWatch(p, now); } catch (Exception ex) { Mod.LogOnce("bot-pitwatch", ex); }
            if (_sn == "StateSandwichAttack" && _prevSn != _sn) BotLog.Evt(BotIndex, "sandwich-ok", "in a sandwich attack (" + (_pair != null && (_pair.A == this || _pair.B == this) ? "planned with b" + ((_pair.A == this ? _pair.B : _pair.A).BotIndex + 1) : _hs != null && _hs.Bot == this ? "planned with the human" : "not planned by me") + ")", 0f);   // M4.7 (no-op in this build)
            try { BikeWatch(p, now); } catch (Exception ex) { Mod.LogOnce("bot-bikewatch", ex); }   // M4.7 (no-op in this build)

            if (p.IsInStateKO) { Status = "down"; SetIntent("waiting for revive"); TargetLabel = ""; _sp = Sp.None; _grabbed = false; _kdActive = false; _escRollWant = false; return; }
            if (!p.HasControl) { Status = "no control"; SetIntent(""); return; }
            Status = "active";

            _jumpCd -= dt; _tauntCd -= dt; _superCd -= dt; _holdSwingCd -= dt; _comboPause -= dt; _tapT -= dt;
            _ledgeCd -= dt; _bfKickCd -= dt; _riseCd -= dt; _sprintCd -= dt; _radicalCd -= dt; _cheerCd -= dt;
            _evadeCd -= dt; _surroundCd -= dt; _backKickCd -= dt; _sandCd -= dt;
            Vector2 me = p.Position2D;
            _surfNow = BotScene.Surf(scene, now);
            if (_surfNow != _wasSurf)
            {
                _wasSurf = _surfNow;
                BotLog.Evt(BotIndex, _surfNow ? "surf-mode-on" : "surf-mode-off", BotScene.SurfWhy + ", my vehicle " + p.VehicleType + (_surfNow ? "; stuck detector, give-up and ledge hops are off" : ""), 0f);
            }
            if (_surfNow) { _giveUpUntil = 0f; _stuckLevel = 0; _stuckSince = -1f; }
            SuperWatch(p, now);

            // damage / hit accounting from the game's own counters
            int myHp = SafeHpP(p);
            if (_lastMyHp >= 0 && myHp < _lastMyHp) _hurtAt = now;
            _lastMyHp = myHp;
            int hits0 = Info.TotalHitCount;
            if (hits0 != _lastHits) { _comboHits++; _lastHitAt = now; }
            if (now - _lastHitAt > 1.0f) _comboHits = 0;
            bool comboActive = _comboHits > 0 && now - _lastHitAt < 1.0f;
            bool prevAtk = _prevSn.StartsWith("StateStationaryAttack", StringComparison.Ordinal) || _prevSn.StartsWith("StateMovingAttack", StringComparison.Ordinal);
            if (prevAtk && !InAttackName && _comboHits >= 3)
            { _comboPause = Math.Max(_comboPause, 0.35f + DiffComboPause); _wantGrab = Rng.NextDouble() < DiffGrabP(0.25); _grabTimeout = 0f; _spPlan = Rng.NextDouble() < 0.3; }   // M4.12: the two Diff* helpers return the old values on Hard

            // 0. grabbed / wrapped / burning / frozen (mash-to-escape): nothing else matters until we are free
            Character holder = GrabHolder(p);
            if (InGrabReaction || (holder != null && !p.IsInStateGrab && !p.IsInStateHurt && !p.IsInStateKnockdown && _sn != "StateGrabAbort" && !InMoveState && !InAttackName))
            {
                if (!_grabbed)
                {
                    _grabbed = true; _grabAt = now; _mashT = 0f; _mashTaps = 0; _mashStep = 0; _sp = Sp.None; Release(A_REVIVE);
                    _grabNote = _sn + " by " + Name(holder);
                    BotLog.Evt(BotIndex, "grab-start", "state " + _sn + " (id " + _sid + "), holder " + Name(holder) + ", enemies near " + _nearCount, 0f);
                    BotLog.Move(BotIndex, "mash-escape", "held (" + _sn + ")");
                    if (BossScene.Any && (_sn == "StateBurntReaction" || _sn == "StateElectrocutedReaction")) BotLog.Evt(BotIndex, "boss-burn-src", _sn + " at " + V(me) + " | " + BossScene.DescribeNear(me), 0.5f);   // M4.10 (no-op in this build)
                }
                SetIntent("mash out of hold"); _ctlOk = false;
                _mashT -= dt;
                if (_mashT <= 0f)
                {
                    // the game ends a plain grab on a fresh Backflip press and shortens the other holds with any of attack / jump / special / backflip
                    _mashT = 0.05f + (float)Rng.NextDouble() * 0.04f; _mashTaps++;
                    int k = _mashStep++ % 4;
                    Tap(k == 0 ? A_BACKFLIP : (k == 2 ? A_JUMP : A_ATTACK));
                }
                return;
            }
            if (_grabbed)
            {
                _grabbed = false;
                BotLog.Evt(BotIndex, "grab-escaped", "after " + (now - _grabAt).ToString("0.0") + "s, " + _mashTaps + " taps, now " + _sn + " (was " + _grabNote + ")", 0f);
                _comboPause = 0.3f;
            }
            _ctlOk = true;

            _leader = FindLeader(scene, p);
            Player down = FindDownedAlly(scene, p, me);
            if (down != null && BossScene.ShadowLink && !(Vector2.Distance(down.Position2D, me) < 70f && !ShadowRisk(down.Position2D))) down = null;   // M4.10: Shadow Link: the figures come first; only a downed player right next to us with no turret line on him
            if (down != null && !AmReviver(down, me)) down = null;   // M4: one bot (the nearest able one) goes for a downed player; the others keep fighting / following
            if (_surv && down != null && SurvSkipRevive(scene, me, now, down)) down = null;   // 0.4.14 Survival: the portal vote is open: no revive trip beyond 110 px
            if (_scanT <= 0f) { Scan(scene, p, me, down, now, comboActive); _scanT = 0.09f + 0.03f * (float)Rng.NextDouble(); } else _scanT -= dt;
            if (now > _blackPrune) { _blackPrune = now + 5f; PruneBlack(now); }
            if (_surv) SurvTargetWatch(p, me, now);   // 0.4.14 Survival: drop a target we cannot reach after ~6 s

            // hit accounting (reach self-calibration + damage checks)
            if (hits0 != _lastHits) { _lastHits = hits0; _missTaps = 0; _reachX = Math.Min(Config.BotReachX, _reachX + 1.5f); _tolY = Math.Min(Config.BotReachY, _tolY + 0.25f); }
            if (_target != null)
            {
                int hp = SafeHp(_target);
                if (hp < _tgtHp) { _tgtHp = hp; _tgtSwings = 0; } else if (hp > _tgtHp) _tgtHp = hp;
            }

            bool hurt = p.IsInStateHurt;
            if (hurt && !_wasHurt) BotLog.Evt(BotIndex, "hurt", "enemies near " + _nearCount + ", nearest " + (int)_nearestEnemyDist + ", target " + Name(_target), 0.8f);
            if (hurt && !_wasHurt && StatueScene.On) BotLog.Evt(BotIndex, "statue-hurt-src", "hit at " + V(me) + " from " + _prevSn + " | " + StatueScene.DescribeNear(me), 0.5f);   // M4.11
            if (hurt && !_wasHurt && BossScene.Any) BotLog.Evt(BotIndex, "boss-hurt-src", "hit at " + V(me) + " from " + _prevSn + " | " + BossScene.DescribeNear(me), 0.5f);   // M4.10 (no-op in this build)

            // 1. knocked down: roll / quick-stand once grounded (the game only accepts them after the body has landed; 0.3.0 tapped in the air and failed 17 of 27)
            if (p.IsInStateKnockdown)
            {
                _sp = Sp.None; _ctlOk = false;
                if (!_kdActive)
                {
                    _kdActive = true; _kdStart = now; _kdTapT = 0f; _kdLogged = false;
                    if (StatueScene.On) BotLog.Evt(BotIndex, "statue-knock-src", "knocked down at " + V(me) + " from " + _prevSn + " | " + StatueScene.DescribeNear(me), 0.5f);   // M4.11
                    if (BossScene.Any) BotLog.Evt(BotIndex, "boss-knock-src", "knocked down at " + V(me) + " from " + _prevSn + " | " + BossScene.DescribeNear(me), 0.5f);   // M4.10 (no-op in this build)
                    bool enemies = _nearCount > 0 || _nearestEnemyDist < 160f;
                    double r = Rng.NextDouble();
                    _kdMode = enemies ? (r < 0.7 ? 1 : 2) : (r < 0.35 ? 1 : (r < 0.7 ? 2 : 0));
                    if (_escRollWant) _kdMode = 1;
                }
                SetIntent(_kdMode == 1 ? "wakeup roll" : (_kdMode == 2 ? "quick stand" : "lie there"));
                _kdTapT -= dt;
                bool early = now - _kdStart < 0.12f;
                if (_kdMode != 0 && _kdTapT <= 0f && (p.OnGround || (_kdMode == 1 && early)) && now - _kdStart < 3.5f)
                {
                    _kdTapT = 0.10f;
                    if (_surv && _kdMode == 1 && SurvRollBlocked(p, now)) _kdMode = 2;   // 0.4.14 Survival: no roll with a pit edge close: quick stand
                    Tap(_kdMode == 1 ? A_BACKFLIP : A_JUMP);
                    if (_kdMode == 1) _move = new Vector2(0f, MeanEnemyY(scene, me) > me.Y ? -1f : 1f);
                    if (!_kdLogged)
                    {
                        _kdLogged = true;
                        string mv = _kdMode == 1 ? (_escRollWant ? "escape-roll" : "wakeup-roll") : "quick-stand";
                        BotLog.Move(BotIndex, mv, "knocked down, " + _nearCount + " enemies near");
                        Expect(mv, 3.0f, _kdMode == 1 ? new[] { "Roll", "Backflip", "RoundOff" } : new[] { "GetUp" });
                    }
                }
                return;
            }
            _kdActive = false;

            // 2. got hit: escape roll / super when swarmed, always roll when it interrupted a revive. The roll window is tiny, so keep tapping while hurt.
            if (hurt && !_wasHurt)
            {
                bool wasReviving = _reviveEvt && now - _reviveHoldAt < 4f;
                if (wasReviving)
                {
                    _reviveEvt = false; _reviveBlockedUntil = now + 0.3f; Release(A_REVIVE);   // M4.1: hit while reviving: roll out, retry right away
                    BotLog.Evt(BotIndex, "revive-interrupted", "got hit while reviving", 0f);
                    if (_surv) _survIntAt = now;   // 0.4.14 Survival: stricter restart rule for a few seconds
                }
                _escRollWant = false; _escRollLogged = false; _escTapT = 0f;
                if ((wasReviving || _nearCount >= 2) && (wasReviving || Rng.NextDouble() < 0.6))
                {
                    if (!wasReviving && p.NinjaPowerStackCount >= 1 && _superCd <= 0f && _nearCount >= 3 && Rng.NextDouble() < 0.4)
                        FireSuper(now, "break out of a swarm (" + _nearCount + " near)", false);
                    else
                    {
                        _escRollWant = true;
                        if (wasReviving) BotLog.Evt(BotIndex, "revive-roll", "dodging out, retrying the revive right away", 0f);
                    }
                }
            }
            _wasHurt = hurt;
            if (hurt)
            {
                _sp = Sp.None;
                if (_surv && _escRollWant && p.OnGround && SurvRollBlocked(p, now)) _escRollWant = false;   // 0.4.14 Survival: never roll toward a pit edge
                if (_escRollWant && p.OnGround)
                {
                    _escTapT -= dt; _ctlOk = false; SetIntent("escape roll");
                    if (_escTapT <= 0f)
                    {
                        _escTapT = 0.06f; Tap(A_BACKFLIP); _move = new Vector2(0f, MeanEnemyY(scene, me) > me.Y ? -1f : 1f);
                        if (!_escRollLogged)
                        {
                            _escRollLogged = true;
                            BotLog.Move(BotIndex, "escape-roll", "hit with " + _nearCount + " enemies near");
                            Expect("escape-roll", 1.2f, "Roll", "Backflip", "RoundOff");
                        }
                    }
                }
                return;
            }
            if (!hurt && _escRollWant && InMoveState) _escRollWant = false;

            // 3. revive a downed ally. M4.1: the nearest able bot goes straight in (no waiting for the area to be clear: with a boss around it never is).
            //    It bails only when it is actually hit or grabbed (the hurt / grab branches above) and retries right away. The one exception is a short wait (0.5 s at most)
            //    when an enemy attack is mid-swing right at the spot. Every other bot keeps fighting (the boss first); it is not drawn off its target.
            if (down != null)
            {
                bool wait = now < _reviveBlockedUntil;
                if (!wait && _attackAtAlly > 0)
                {
                    if (_reviveWaitSince < 0f) _reviveWaitSince = now;
                    wait = now - _reviveWaitSince < 0.5f && !IsHolding(A_REVIVE);
                }
                else if (_attackAtAlly == 0) _reviveWaitSince = -1f;
                if (!wait && _surv && SurvReviveHold(now)) wait = true;   // 0.4.14 Survival: 3+ enemies at the spot / a ranged attacker aiming there: fight to clear first
                if (!wait && StatueScene.On && !IsHolding(A_REVIVE) && StatueRevivePause(now)) { wait = true; BotLog.Evt(BotIndex, "statue-revive-wait", "ally down, Statue in " + StatueScene.State + ": reviving between attacks", 2.0f); }   // M4.11: never interrupts a revive already in progress
                else if (!StatueScene.On) _srSince = -1f;
                if (!wait)
                {
                    Vector2 d2 = down.Position2D - me;
                    float reach = Math.Max(10f, p.ReviveReachRadius * 0.6f);
                    TargetLabel = "revive P" + (down.PInfo != null ? down.PInfo.PlayerID + 1 : 0);
                    if (Math.Abs(d2.X) <= reach && Math.Abs(d2.Y) <= Math.Min(6f, reach))
                    {
                        SetIntent("reviving");
                        if (!IsHolding(A_REVIVE))
                        {
                            Hold(A_REVIVE, 3.0f); _reviveHoldAt = now; _reviveEvt = true;
                            BotLog.Move(BotIndex, "revive-hold", "ally down, going straight in (" + _attackAtAlly + " attacks mid-swing at the spot, " + _threatNearAlly + " enemies near them)");
                            BotLog.Evt(BotIndex, "revive-start", "ally P" + (down.PInfo != null ? down.PInfo.PlayerID + 1 : 0), 0f);
                            Expect("revive-hold", 1.0f, "StateRevive");
                        }
                        return;
                    }
                    SetIntent("to downed ally");
                    MoveTo(down.Position2D, reach * 0.8f, 4f);
                    return;
                }
                if (IsHolding(A_REVIVE)) { Release(A_REVIVE); _reviveEvt = false; }
                BotLog.Evt(BotIndex, "revive-wait-swing", "ally down; an attack is mid-swing at the spot, waiting up to 0.5 s (" + (now - _reviveWaitSince).ToString("0.0") + " s so far)", 3f);
                // fall through: keep fighting while we wait
            }
            else
            {
                if (IsHolding(A_REVIVE)) Release(A_REVIVE);
                if (_reviveEvt && now - _reviveHoldAt < 5f && !hurt) { _reviveEvt = false; BotLog.Evt(BotIndex, "revive-done", "ally is up", 0f); }
                _reviveWaitSince = -1f;
            }

            // 4. grabbing an enemy: throw it
            if (p.IsInStateGrab)
            {
                _sp = Sp.None;
                if (_grabSince < 0f) _grabSince = now;
                SetIntent("throwing");
                if (now - _grabSince > 0.35f && _tapT <= 0f)
                {
                    string mv;
                    if (!ThrowDecide(scene, p, me, now, out mv)) return;   // 0.4.14: situational throw (fwd / back / slam / toss); a held enemy that is too strong is not thrown (the game would abort the grab with a pushback)
                    Tap(A_ATTACK); _tapT = 0.6f; _grabSince = now + 5f;
                    BotLog.Move(BotIndex, mv, "holding an enemy");
                    Expect(mv, 0.8f, "Throw", "Slam", "Toss", "Suplex");
                }
                return;
            }
            _grabSince = -1f;

            // 4b. a barrel we lit is about to blow: get out of its radius (and never stand next to one that is already going off)
            if (_fleeArmAt >= 0f && now >= _fleeArmAt) { _fleeArmAt = -1f; _fleeUntil = now + 2.8f; }
            if (_barrelObj != null && p.OnGround)
            {
                try { if (_barrelObj.Active && _barrelObj.IsDead && Vector2.Distance(_barrelObj.Position2D, me) < _fleeR + 24f && _fleeUntil < now + 0.5f) { _fleePos = _barrelObj.Position2D; _fleeUntil = now + 1.8f; } } catch { }
            }
            if (p.OnGround && _fleeUntil < now + 0.3f)
            {
                // a barrel lit by another bot: step out of its blast too
                for (int li = 0; li < LitList.Count; li++)
                {
                    Lit l = LitList[li];
                    if (now < l.Until && Vector2.Distance(l.Pos, me) < l.R + 30f) { _fleePos = l.Pos; _fleeR = l.R; _fleeUntil = now + 0.8f; break; }
                }
            }
            if (now < _fleeUntil && p.OnGround && (InMoveState || _sn == "StateSprint"))
            {
                Vector2 away = me - _fleePos; float ad = away.Length();
                if (ad > _fleeR + 36f) _fleeUntil = 0f;
                else
                {
                    _sp = Sp.None;
                    _move = new Vector2(Math.Abs(away.X) < 2f ? (Rng.Next(2) == 0 ? 1f : -1f) : Math.Sign(away.X), Math.Abs(away.Y) < 14f ? (away.Y >= 0f ? 0.6f : -0.6f) : 0f);
                    SetIntent("away from the barrel"); _ctlOk = false;
                    return;
                }
            }

            // 5. airborne (jump-in / backflip / double jump): finish with the planned follow-up, keep drifting at the target
            bool airborne = !p.OnGround || p.IsJumping;
            if (airborne)
            {
                if (!_wasAir) { _wasAir = true; _airStartZ = _lastGroundZ; _dblMaxZ = p.Position.Z; }
                if (p.Position.Z > _dblMaxZ) _dblMaxZ = p.Position.Z;
                float air = p.Position.Z - _airStartZ;
                if (_dblAt >= 0f && now >= _dblAt)
                {
                    _dblAt = -1f; Tap(A_JUMP); _dblStartZ = p.Position.Z; _dblWatch = true;
                    BotLog.Move(BotIndex, "jump-double", "second jump near the apex (+" + (int)air + " so far), goal dz " + (int)_goalDz);
                    Expect("jump-double", 0.5f, "StateJump", "Fall");
                }
                if (_airTapAt >= 0f && now >= _airTapAt && BikeKickTooLow(air, now)) { }   // M4.7: the bike kick's attack press would be lost below 50 px: wait a frame or two (max 0.15 s)
                else if (_airTapAt >= 0f && now >= _airTapAt)
                {
                    _airTapAt = -1f; _bikeKickWaitUntil = -1f;
                    if (_unlockAir && air > 52f && p.NinjaPowerStackCount >= 1 && _superCd <= 0f && _superRange >= 1 && SuperOk(p) && BotManager.SuperGateOpen && Rng.NextDouble() < 0.5)
                        FireSuper(now, "from the air, " + _superRange + " in range", true);
                    else
                    {
                        Tap(A_ATTACK); SetIntent("air attack");
                        BotLog.Move(BotIndex, "air-attack", _airWhy); Expect("air-attack", 0.7f, "Aerial", "DiveKick", "FlyingKick");
                    }
                }
                if (_bfKickAt >= 0f && now >= _bfKickAt)
                {
                    _bfKickAt = -1f;
                    if (_unlockDodge && p.NinjaPowerStackCount >= 1 && _superCd <= 0f && _superRange >= 1 && SuperOk(p) && BotManager.SuperGateOpen)
                    {
                        _superCd = 3f; BotManager.SuperFired(now, (float)Rng.NextDouble()); Tap(A_SPECIAL); BotLog.Move(BotIndex, "super-dodge", "special out of the backflip, " + _superRange + " in range, " + p.NinjaPowerStackCount + " stacks");
                        Expect("super-dodge", 0.7f, "SuperFlyingKick", "Super", "SpecialAttack");
                    }
                    else { Tap(A_ATTACK); BotLog.Move(BotIndex, "backflip-kick", "kick out of the backflip"); Expect("backflip-kick", 0.7f, "FlyingKick", "DiveKick"); }
                    SetIntent("backflip kick");
                }
                if (_target != null && now >= _evadeAirUntil) { Vector2 dt2 = _target.Position2D - me; _move.X = Math.Abs(dt2.X) > 12f ? Math.Sign(dt2.X) : 0f; }
                return;
            }
            if (_wasAir)
            {
                _wasAir = false;
                if (_dblWatch)
                {
                    _dblWatch = false; float gain = _dblMaxZ - _dblStartZ;
                    BotLog.Note(BotIndex, "double-jump-height", "double jump peak +" + (int)(_dblMaxZ - _airStartZ) + " above the take-off (second press at +" + (int)(_dblStartZ - _airStartZ) + ", gained " + (int)gain + ")", 2f);
                }
            }
            _lastGroundZ = p.Position.Z;
            _airTapAt = -1f; _dblAt = -1f;
            if (!(_bfKickAt >= 0f && now < _bfKickAt + 0.15f)) _bfKickAt = -1f;   // keep a just-planned backflip kick through the take-off frames

            // 6. sprint sequence in progress?
            if (_sp != Sp.None && RunSprint(p, me, now, dt)) return;

            // 6b. a biker is charging along our lane: meet it with a timed jump kick or a normal hit
            if (TryHazard(p, me, now)) return;   // M4.1: a hazard with a warning (car in a lane): get out of its lane first
            if (TryPizzaPickup(scene, p, me, now, comboActive)) return;   // 0.4.16 pizza; 0.4.17 leave-behind/wall abort
            if (_surv && SurvZoneStep(scene, p, me, now)) return;   // 0.4.14 Survival: step out of a burn / electric / grenade zone
            if (_surv && SurvPortalStep(scene, p, me, now)) return;   // 0.4.14 Survival: the portal vote is open: everybody walks into the same portal
            if (_surfNow ? RunBikerSurf(p, me, now, scene) : RunBiker(p, me, now, scene)) return;   // normal stages: the 0.3.1 biker logic, untouched

            // 6b2. (M4.10) Shredder in his counter stance and within reach: do not hit him, step back out of reach until it ends
            if (BossScene.Counter != null && CounterStepBack(p, me, now)) return;

            // 6c. (M4) situational dodges: an attack coming at us, surrounded, an enemy behind us. Never spammed (cooldowns + a roll per incoming attack).
            if (TryEvade(p, me, now)) return;

            // 7. super / radical mode
            if (HandleSuper(p, me, now)) return;

            // 7b. (M4) two-player sandwich attack with another bot, when the geometry allows it
            if (RunSandwich(p, me, now)) return;

            // 8. fight
            bool leaderFar = _leader != null && Vector2.Distance(_leader.Position2D, me) > Config.BotFollowFar * 1.6f;
            bool figT = _target is SuperShredderClone;   // M4.10: a Shadow Link figure: no give-up rules (swing drop, prop timeout, blacklist, "not engaged")
            bool engaged = _nearestEnemyDist < 220f || now - _hurtAt < 6f || comboActive || _rangedThreat || _target is Boss || figT;
            bool pursue = _target != null && comboActive && _target.Active && !_target.IsInStateKO;
            bool tgtOk = _target != null && _target.Active && (_target.CanBeTargeted || pursue);
            if (tgtOk && (!leaderFar || engaged))
            {
                if (_tgtSwings >= (_target is Breakable ? 4 : 7)) { if (_target is Boss) { _tgtSwings = 0; BotLog.Evt(BotIndex, "boss-no-damage", "7 swings without damage on the boss; staying on it", 5f); } else { if (figT) { _tgtSwings = 0; } else { DropTarget("no damage after " + _tgtSwings + " swings", now); return; } } }
                if (_target is Breakable && !figT && now - _propSince > 10f) { DropTarget("prop took too long", now); return; }
                FightTarget(p, me, now, dt, comboActive);
                return;
            }
            if (!tgtOk) { _target = null; }

            // 8b. (M4.3) the game's high-five between two bots: a low bot gets +2 HP from a healthy one (nothing is in a fight)
            if (_sp == Sp.None && InMoveState && (TryCheerAlly(scene, p, me, now) || TryCheerHuman(scene, p, me, now))) return;

            // 8c. (M4.10) Super Shredder is shielded and nothing else is left: wait just outside his zone for the stun window
            if (BossScene.Super != null && SuperStandby(p, me, now)) return;

            // 9. follow the human
            TargetLabel = _leader == null ? "" : "follow P" + (_leader.PInfo != null ? _leader.PInfo.PlayerID + 1 : 0);
            if (_leader != null)
            {
                Vector2 slot = _leader.Position2D + new Vector2(-((BotIndex % 2 == 0) ? 1 : -1) * 36f * (1 + BotIndex / 2) * (_leader.HorizontalValue >= 0 ? 1 : -1), SlotY[BotIndex % SlotY.Length]);
                Vector2 d = slot - me;
                float far = leaderFar ? Config.BotFollowNear * 0.6f : Config.BotFollowFar, near = Config.BotFollowNear;
                float dist = d.Length();
                _goalDz = _leader.Position.Z - p.Position.Z;
                _higherGoal = _leader.OnGround && _goalDz >= 14f;
                if (now < _giveUpUntil)
                {
                    _following = false; SetIntent("waiting (cannot reach P" + (_leader.PInfo != null ? _leader.PInfo.PlayerID + 1 : 0) + ")");
                    BotLog.Add(BotIndex, "wait-sec", dt);
                }
                else if (dist > far || (_following && dist > near))
                {
                    _following = true; MoveTo(slot, 10f, 8f); SetIntent(leaderFar ? "catch up" : "follow");
                    float catchUp = Math.Max(150f, Config.BotFollowFar * 2.2f);
                    if (_sprintCd <= 0f && dist > catchUp && Math.Abs(d.X) > 130f && Math.Abs(d.X) > 1.4f * Math.Abs(d.Y) && _nearestEnemyDist > 100f && InMoveState && !_higherGoal && Rng.NextDouble() < 0.08)
                        StartSprint(Math.Sign(d.X), slot.X, false, "far from human: dx " + (int)d.X + " dy " + (int)d.Y, now);
                }
                else
                {
                    _following = false; SetIntent("idle");
                    Vector2 sep = Separation(p, me);
                    if (sep != Vector2.Zero) { _move = sep; _ctlOk = false; SetIntent("spread out"); }   // do not stand on top of the others
                }
                if (!_following && !StatueScene.On && _nearestEnemyDist > 220f && _tauntCd <= 0f && p.NinjaPowerStackCount < Math.Max(1, p.MaxNPStacks) && p.NP < p.MaxNP && Rng.NextDouble() < 0.01)
                {
                    _tauntCd = 12f; Tap(A_TAUNT); SetIntent("taunt");
                    BotLog.Move(BotIndex, "taunt", "safe, no enemies within 220, NP not full"); Expect("taunt", 0.8f, "StateTaunt");
                }
            }
            else SetIntent("alone");
        }

        private static readonly float[] SlotY = { -8f, 8f, 0f, -12f, 12f };
        private static bool SafeRadical(Player p) { try { return p.CanUseRadicalMode; } catch { return false; } }
        private static bool Hurtable(NonPlayerCharacter n) { try { return n.CanBeHurt && !n.IsInvulnerable; } catch { return true; } }
        private static int SafeHp(NonPlayerCharacter t) { try { return t.HP; } catch { return 0; } }
        private static int SafeHpP(Player t) { try { return t.HP; } catch { return 0; } }

        private bool _following; private float _grabTimeout;

        // ------------------------------------------------------------ super / radical
        private bool SuperOk(Player p)
        {
            // air supers need height; ground ones need nothing but an enemy in front. Never while we hold the meter for radical mode unless a crowd is on us.
            // M4.1: the game refuses the special while frozen, in radical mode, mutated or without control (the 0.4.0 bots kept pressing it: Usagi 6 of 23)
            try { return !p.IsFrozen && !p.IsInRadicalMode && !p.IsMutationApplied && p.HasControl; } catch { return true; }
        }

        private void FireSuper(float now, string why, bool air)
        {
            _superCd = 2.2f; _superPendUntil = now + 0.9f;
            { Player pp = ActivePlayer; _superWatch = true; _superWatchUntil = now + 1.0f; _superStacksAt = pp != null ? pp.NinjaPowerStackCount : 0; }
            BotManager.SuperFired(now, (float)Rng.NextDouble());
            Tap(A_SPECIAL); SetIntent("super");
            string mv = air ? "super-air" : "super";
            BotLog.Move(BotIndex, mv, why);
            Expect(mv, 0.8f, air ? new[] { "SuperDiveKick", "Super", "SpecialAttack" } : new[] { "SpecialAttack", "Super" });
        }

        /// <summary>Super only with an enemy in range and in front (0.3.0 fired from attack states with no hit and from far away: 8 of 17). Fire as soon as the meter allows unless radical mode is unlocked and the meter is not full yet.</summary>
        private bool HandleSuper(Player p, Vector2 me, float now)
        {
            if (_sn == "StateSpecialAttack") { _superCd = Math.Max(_superCd, 4f); _superPendUntil = 0f; return false; }
            if (_superPendUntil > now && _sn != "StateSpecialAttack") { SetIntent("super"); _move = Vector2.Zero; return true; }
            int stacks = p.NinjaPowerStackCount; int max = Math.Max(1, p.MaxNPStacks);
            bool full = stacks >= max;
            // radical mode: full meter, enemies around
            if (_unlockRad && full && _nearCount >= 2 && _radicalCd <= 0f && _tauntCd <= 0f && InMoveState && SafeRadical(p))
            {
                _radicalCd = 20f; _tauntCd = 5f; Tap(A_TAUNT); SetIntent("radical mode");
                BotLog.Move(BotIndex, "radical-mode", _nearCount + " enemies near, full meter (" + stacks + "/" + max + ")"); Expect("radical-mode", 0.9f, "Radical");
                return true;
            }
            if (stacks < 1 || _superCd > 0f || _superRange < 1 || !SuperOk(p) || !p.OnGround) return false;
            if (!BotManager.SuperGateOpen && !(_nearCount >= 3 && now - _hurtAt < 2f)) return false;   // M4: another bot just used its super; stagger (unless we are swarmed and hurt)
            bool saveForRadical = _unlockRad && !full;
            if (saveForRadical && !(_superRange >= 3 || (_superRange >= 2 && now - _hurtAt < 4f))) return false;
            bool ready = InMoveState || (InAttackName && ChainOpen(p));
            if (!ready) return false;
            if (_superFacing < 1)
            {
                // enemies are in range but behind us: turn first (a press of the direction turns the sprite), then fire next frame
                _move.X = _superSide; SetIntent("turn for super");
                return false;
            }
            FireSuper(now, _superFacing + " in front (" + _superRange + " in range), " + stacks + "/" + max + " stacks" + (saveForRadical ? ", crowd" : ""), false);
            return true;
        }

        // ------------------------------------------------------------ fighting
        private static bool IsExplosive(Breakable b, out float r)
        {
            r = 0f;
            try
            {
                if (b == null) return false;
                bool has = !string.IsNullOrEmpty(b.AttackOnDeath) || !string.IsNullOrEmpty(b.AttackOnDeath2);
                string nm = b.Name ?? "";
                bool named = nm.IndexOf("Explosive", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!has && !named) return false;
                r = Math.Max(b.AttackRadius, b.AttackRadiusPlayer);
                if (r < 60f) r = 90f; if (r > 200f) r = 200f;
                return true;
            }
            catch { return false; }
        }

        private bool IsRangedAttacker(Enemy n, float now)
        {
            string sn = StateNameOf(n);
            if ((sn.IndexOf("Throw", StringComparison.Ordinal) >= 0 && sn.IndexOf("Thrown", StringComparison.Ordinal) < 0) || sn.IndexOf("Shoot", StringComparison.Ordinal) >= 0 || sn.IndexOf("Aim", StringComparison.Ordinal) >= 0 || sn.IndexOf("Projectile", StringComparison.Ordinal) >= 0) return true;
            bool t; Type ty = n.GetType();
            if (!_rangedTypes.TryGetValue(ty, out t))
            {
                string tn = ty.Name;
                t = tn.IndexOf("Arbalest", StringComparison.Ordinal) >= 0 || tn.IndexOf("Boomerang", StringComparison.Ordinal) >= 0 || tn.IndexOf("Bow", StringComparison.Ordinal) >= 0 || tn.IndexOf("Dynamite", StringComparison.Ordinal) >= 0
                    || tn.IndexOf("Kunai", StringComparison.Ordinal) >= 0 || tn.IndexOf("Thrower", StringComparison.Ordinal) >= 0 || tn.IndexOf("Jetpack", StringComparison.Ordinal) >= 0 || tn.IndexOf("Blimp", StringComparison.Ordinal) >= 0;
                _rangedTypes[ty] = t;
            }
            return t && (now - _hurtAt < 8f || sn.IndexOf("Attack", StringComparison.Ordinal) >= 0);
        }

        private void FightTarget(Player p, Vector2 me, float now, float dt, bool comboActive)
        {
            if (_target is Shredder && BossScene.InCounter(_target))
            {
                // M4.10: he is in his counter stance: keep off him (the scan drops him from the target list within ~0.1 s)
                _ctlOk = false; _move = Vector2.Zero; SetIntent("hold: Shredder counter stance"); _scanT = 0f;
                return;
            }
            Vector2 tp = _target.Position2D;
            Vector2 d = tp - me; float ax = Math.Abs(d.X), ay = Math.Abs(d.Y);
            float dz = _target.Position.Z - p.Position.Z;
            TargetLabel = Name(_target) + " dx" + (int)d.X + " dy" + (int)d.Y;
            string why = Name(_target) + " dx" + (int)d.X + " dy" + (int)d.Y + (Math.Abs(dz) > 10f ? " dz" + (int)dz : "") + (_tgtSwings > 0 ? " swings " + _tgtSwings : "");
            bool knocked = _target.IsInStateKnockdown || !_target.CanBeTargeted;
            bool boss = _target is Boss; float bgx = 0f, bgy = 0f; bool bossGeom = false;
            if (boss)
            {
                // M4.1: bosses (incl. vehicle bosses like the Tenderizer) are wide: reach / lane come from the real hurtbox edge, not the centre
                bossGeom = BossReach(_target, me, out bgx, out bgy);
                if (bossGeom) { ax = Math.Min(ax, bgx + 20f); ay = Math.Min(ay, bgy); if (_bossLogged != _target) { _bossLogged = _target; BotLog.Evt(BotIndex, "boss-geom", Name(_target) + " hurtbox gap x " + (int)bgx + " y " + (int)bgy + ", centre dx " + (int)d.X + " dy " + (int)d.Y + ", ReachX " + (int)_reachX, 0f); } }
            }
            bool airTarget = !_target.OnGround && dz > 18f;
            bool surfFlier = _surfNow && airTarget && !boss;   // M4.1: fliers on surf stages: wider lane / reach tolerance (the board and the flier both drift)
            bool inY = ay <= _tolY + (surfFlier ? 10f : 0f);
            if (inY) _lineSince = -1f;
            bool figure = _target is SuperShredderClone;   // M4.10: Shadow Link figure (turret clone)
            bool higher = _target.OnGround && dz >= 16f && !figure;
            _higherGoal = higher; _goalDz = dz;
            _goalSet = true; _goal = tp; _goalTolX = _reachX + 6f;
            Breakable prop = _target as Breakable; float barrelR = 0f;
            bool explosive = prop != null && IsExplosive(prop, out barrelR);

            // keep hitting a knocked-back enemy: follow it down instead of walking off (the combo counter keeps running while we are on it)
            if (knocked && _target is StatueOfTyranny && !_target.IsInStateKnockdown)
            {
                // M4.11: the Statue slid off-screen / went untargetable (not knocked down): wait at the edge, do not chase the combo into the pit
                _ctlOk = false; _move = Vector2.Zero; SetIntent("hold: Statue away");
                BotLog.Evt(BotIndex, "statue-hold", "Statue untargetable in " + StatueScene.State + ", holding at " + V(me), 3f);
                return;
            }
            if (knocked)
            {
                SetIntent("follow knocked-back enemy");
                BotLog.Evt(BotIndex, "combo-follow", Name(_target) + " knocked back, following (combo " + _comboHits + ")", 2f);
                _move.X = ax > _reachX * 0.5f ? Math.Sign(d.X) : 0f; _move.Y = ay > 4f ? Math.Sign(d.Y) : 0f;
                return;
            }
            if (!inY) _move.Y = Math.Sign(d.Y);

            // standing on something higher than us (container, car roof): get next to it and climb; give up after a few tries
            if (higher && ax <= _reachX + 24f)
            {
                if (_climbTries >= 3)
                {
                    BotLog.Evt(BotIndex, "unreachable-target", Name(_target) + " is " + (int)dz + " higher, " + _climbTries + " climb tries failed", 0f);
                    _black[_target] = now + 12f; _target = null; _tgtSwings = 0; _scanT = 0f; _climbTries = 0;
                    return;
                }
                SetIntent("climb to target"); _move.X = ax > 8f ? Math.Sign(d.X) : 0f;
                return;
            }

            if (ax > _reachX + 6f + (surfFlier ? 30f : 0f))
            {
                SetIntent(boss ? "approach boss" : "approach");
                _move.X = Math.Sign(d.X);
                // jump-in dive when a gap is in front of us and nobody is on top of us (never at a boss: it jumped in from dx 127, far outside its reach, and ate the hit)
                if (!boss && inY && ax > 70f * AggChargeHold() && ax < 130f && _jumpCd <= 0f && _nearCount <= 2 && !higher && InMoveState && prop == null && RollAdv(0.02))
                {
                    _jumpCd = 4f; Tap(A_JUMP); _airTapAt = now + 0.28f; _airWhy = "jump-in at " + why; SetIntent("jump-in");
                    BotLog.Move(BotIndex, "jump-in", why); Expect("jump-in", 0.5f, "StateJump");
                    return;
                }
                // sprint attack plan: run in from a distance, then bash / slide
                if (_spPlan && _sprintCd <= 0f && inY && ax > (boss ? 150f : 110f) * AggChargeHold() && ax < 260f && (boss || _nearCount <= 2) && InMoveState && Hurtable(_target) && !higher && !airTarget && prop == null && RollAdv(0.25))
                {
                    _spPlan = false;
                    StartSprint(Math.Sign(d.X), tp.X, true, "sprint attack at " + why, now);
                }
                return;
            }
            // in horizontal reach
            if (!inY)
            {
                SetIntent("line up"); if (ax > _reachX * 0.5f) _move.X = 0f;
                if (_surfNow && !boss)
                {
                    // M4.1: on a surf stage the lane can be unreachable (board lane limits, enemy on another layer): never line up for ever, leave it alone for a bit
                    if (_lineSince < 0f) _lineSince = now;
                    else if (now - _lineSince > 1.6f)
                    {
                        BotLog.Evt(BotIndex, "surf-lineup-timeout", Name(_target) + " dy " + (int)d.Y + " dz " + (int)dz + " could not be lined up for 1.6 s; ignoring it for 3 s", 1f);
                        _black[_target] = now + 3f; _target = null; _scanT = 0f; _lineSince = -1f;
                    }
                }
                return;
            }
            bool facing = (d.X >= 0f) == (p.HorizontalValue >= 0);
            if (!facing && ax > 2f) { _move.X = Math.Sign(d.X); SetIntent("turn"); return; }

            if (airTarget && _jumpCd <= 0f && InMoveState)
            {
                _jumpCd = _surfNow ? 1.2f : 2.5f; Tap(A_JUMP); _airTapAt = now + 0.2f; _airWhy = "flier " + why; SetIntent("jump at flier");
                BotLog.Move(BotIndex, "jump-at-flier", why); Expect("jump-at-flier", 0.5f, "StateJump");
                return;
            }

            // explosive barrel: hit it once, then run (never with the human next to it)
            if (explosive)
            {
                if (OtherPlayerNear(tp, barrelR + 28f, p))
                {
                    BotLog.Evt(BotIndex, "target-drop-nodmg", "barrel with the human or a bot next to it, leaving it", 3f);
                    _black[_target] = now + 6f; _target = null; _scanT = 0f; return;
                }
                if (!ReferenceEquals(_barrelObj, prop)) _barrelTaps = 0;
                if (_tapT <= 0f && InMoveState)
                {
                    _barrelObj = prop; _barrelTaps++; _tapT = 0.5f; _fleePos = tp; _fleeR = barrelR; _fleeArmAt = now + 0.3f; NoteLit(tp, barrelR, now);
                    Tap(A_ATTACK); BotLog.Move(BotIndex, "barrel-hit", why + ", radius " + (int)barrelR + ", " + _nearCount + " enemies near");
                    Expect("barrel-hit", 0.5f, "Attack");
                    if (_barrelTaps >= 3) { _black[_target] = now + 40f; _target = null; _scanT = 0f; }
                }
                SetIntent("light the barrel");
                return;
            }

            if (figure && BossScene.HoldFire((SuperShredderClone)_target))
            {
                // M4.10: this figure is already low: hold off the last hit until every living figure is low, then finish together inside the respawn window
                SetIntent("hold the last hit: finish together");
                BotLog.Evt(BotIndex, "boss-hold-fire", "figure #" + ((SuperShredderClone)_target).CloneIndex + " hp " + SafeHp(_target) + "/" + _target.MaxHP + ", waiting for the others (" + BossScene.Figs.Count + " alive)", 2f);
                return;
            }
            bool chainOpen = ChainOpen(p);
            if (_wantGrab && _tapT <= 0f && _comboHits == 0 && InMoveState && prop == null && Hurtable(_target) && CanGrabIt(_target))
            {
                // walk into the enemy without attacking: the game grabs automatically when close
                SetIntent("grab");
                if (_grabTimeout == 0f) { BotLog.Move(BotIndex, "grab", why); Expect("grab", 1.3f, "StateGrab"); }
                if (ax > 14f) _move.X = Math.Sign(d.X);
                _grabTimeout += dt; if (_grabTimeout > 1.2f) { _wantGrab = false; _grabTimeout = 0f; }
                return;
            }
            // heavy swing: hold attack ~0.95 s. Random spice after a landed hit (4%, skill-gated). Deliberate answer when the target is blocking / shielded (0.4.15).
            // The game starts HeavySwing from a hit string OR from walk while the button is held (HeavyAttackHoldButtonDuration 0.2 s + HeavyAttackTiggerDelay 0.4 s).
            bool guardT = prop == null && TargetGuarding(_target);
            bool wantRndHeavy = chainOpen && _comboHits >= 1 && RollAdv(0.04);
            bool wantGuardHeavy = guardT && GuardHeavyOk() && (chainOpen || InMoveState);
            if (_holdSwingCd <= 0f && prop == null && (wantRndHeavy || wantGuardHeavy) && !HeavySwingBlocked(p, me, now, d))
            {
                _holdSwingCd = wantGuardHeavy ? 4.5f : 7f; Hold(A_ATTACK, 0.95f); _tapT = 1.4f;
                if (!chainOpen && ax > 4f) _move.X = Math.Sign(d.X);   // walk-path heavy needs mostly-horizontal motion
                SetIntent(wantGuardHeavy ? "heavy swing (guard)" : "heavy swing");
                string hwhy = wantGuardHeavy ? ("reason=guard, " + why) : ("held attack after a landed hit, " + why);
                BotLog.Move(BotIndex, "heavy-swing", hwhy); Expect("heavy-swing", 1.4f, "HeavySwing");
                return;
            }
            // occasional air attack, like a player mixing it up (only from the ground, between strings); backflips / dodges are situational now (TryEvade)
            if (_comboHits == 0 && _tapT <= 0f && _comboPause <= 0f && InMoveState)
            {
                if (_jumpCd <= 0f && _nearCount <= 2 && prop == null && !higher && RollAdv(0.006))
                {
                    _jumpCd = 3.5f; Tap(A_JUMP); _airTapAt = now + 0.22f; _airWhy = "mix-up air attack, " + why; SetIntent("jump attack");
                    BotLog.Move(BotIndex, "jump-in", "mix-up, " + why); Expect("jump-in", 0.5f, "StateJump");
                    return;
                }
            }
            // rising attack = jump + attack pressed together while the last swing has hit (the game ignores it otherwise: 3 of 6 in 0.3.0)
            if (chainOpen && _comboHits >= 1 && _riseCd <= 0f && _tapT <= 0f && prop == null && RollAdv(0.08))
            {
                _riseCd = 10f; Tap(A_JUMP); Tap(A_ATTACK); _tapT = 0.35f; _comboPause = 0.5f;
                BotLog.Move(BotIndex, "rising-attack", "jump+attack after a landed hit, " + why); Expect("rising-attack", 0.7f, "RisingKick");
                SetIntent("rising attack");
                return;
            }
            // normal attacks: start a string from idle, then press again exactly when the swing has hit (the game's chain window), never blindly
            if (_tapT <= 0f)
            {
                if (chainOpen) { Tap(A_ATTACK); _tapT = 0.08f; SetIntent("attack (chain)"); return; }
                if (InMoveState && _comboPause <= 0f)
                {
                    Tap(A_ATTACK); _tapT = 0.25f; _missTaps++; _tgtSwings++;
                    if (figure) BossScene.SwingsSeen++;
                    if (prop != null && _tgtSwings == 1) BotLog.Move(BotIndex, "prop-smash", why);
                    if (_missTaps >= 8 && prop == null)
                    {
                        _missTaps = 0;
                        if (_tolY > 3.5f) _tolY -= 1.5f; else _reachX = Math.Max(22f, _reachX - 4f);
                        BotLog.Note(BotIndex, "calib", "no hits for 8 swings: ReachX " + (int)_reachX + ", ReachY " + _tolY.ToString("0.#") + " (" + why + ")", 5f);
                    }
                }
            }
            SetIntent("attack");
        }

        private static bool CanGrabIt(NonPlayerCharacter n)
        {
            try
            {
                if (n is Breakable) return false;
                string tn = n.GetType().Name;
                if (tn.IndexOf("Rodney", StringComparison.Ordinal) >= 0 || tn.IndexOf("Boss", StringComparison.Ordinal) >= 0) return false;
                return n.CanCurrentlyBeGrabbed;
            }
            catch { return false; }
        }

        private void DropTarget(string why, float now)
        {
            BotLog.Evt(BotIndex, "target-drop-nodmg", Name(_target) + ": " + why + "; ignoring it for a while", 0f);
            _black[_target] = now + (_target is Breakable ? 25f : 8f);
            _target = null; _tgtSwings = 0; _scanT = 0f;
        }

        private void PruneBlack(float now)
        {
            List<NonPlayerCharacter> rm = null;
            foreach (var kv in _black) if (kv.Value < now || kv.Key == null || !kv.Key.Active) (rm ?? (rm = new List<NonPlayerCharacter>())).Add(kv.Key);
            if (rm != null) foreach (var k in rm) _black.Remove(k);
        }
        private bool IsBlack(NonPlayerCharacter n, float now) { float t; return _black.TryGetValue(n, out t) && t > now; }

        // ------------------------------------------------------------ bikers
        /// <summary>A charging bike is only hurtable while it rides at us, so there is nothing to "target": stand in its lane, face it, and time the move to its speed.</summary>
        private bool RunBiker(Player p, Vector2 me, float now, Scene2d scene)
        {
            FootMotorcycle best = null; float bestT = 9f, bestDx = 0f, bestSpeed = 0f;
            try
            {
                foreach (object o in scene.GetGameObjectsOfType<FootMotorcycle>())
                {
                    FootMotorcycle b = o as FootMotorcycle;
                    if (b == null || !b.Active || !b.IsVisibleOnScreen || !Hurtable(b)) continue;
                    Vector2 d = b.Position2D - me;
                    if (Math.Abs(d.Y) > _tolY + 16f || Math.Abs(d.X) > 460f || Math.Abs(b.Position.Z - p.Position.Z) > 24f) continue;
                    int dir = b.HorizontalValue;
                    if (d.X * dir >= 0f) continue;                       // not coming toward us
                    float sp = 260f;
                    try { float vx = Math.Abs(b.BodyComponent.Displacement.Speed.X); if (vx > 60f) sp = vx; } catch { }
                    float t = (Math.Abs(d.X) - (_reachX + 10f)) / sp;
                    if (t < bestT) { bestT = t; best = b; bestDx = d.X; bestSpeed = sp; }
                }
            }
            catch (Exception ex) { Mod.LogOnce("bot-biker", ex); return false; }
            if (best == null) { _bikeCur = null; return false; }
            if (!ReferenceEquals(_bikeCur, best))
            {
                _bikeCur = best; _bikeJumpPlan = Rng.NextDouble() < 0.55;
                BotLog.Note(BotIndex, "biker", "bike incoming at dx " + (int)bestDx + ", speed " + (int)bestSpeed + ", plan " + (_bikeJumpPlan ? "jump kick" : "normal hit"), 0.5f);
            }
            if (!InMoveState) return false;
            TargetLabel = "FootMotorcycle dx" + (int)bestDx; SetIntent(_bikeJumpPlan ? "biker: jump kick" : "biker: normal hit");
            bool facing = (bestDx >= 0f) == (p.HorizontalValue >= 0);
            if (!facing) { _move.X = Math.Sign(bestDx); return true; }
            if (_bikeJumpPlan)
            {
                // the kick lands ~0.2 s after takeoff
                if (bestT <= 0.30f && bestT > -0.1f && _jumpCd <= 0f)
                {
                    _jumpCd = 1.6f; Tap(A_JUMP); _airTapAt = now + 0.18f; _airWhy = "kick the bike, ttc " + bestT.ToString("0.00"); _bikeActedAt = now;
                    BotLog.Move(BotIndex, "biker-jumpkick", "bike dx " + (int)bestDx + " speed " + (int)bestSpeed + " ttc " + bestT.ToString("0.00"));
                    Expect("biker-jumpkick", 0.9f, "Aerial", "DiveKick", "FlyingKick");
                }
            }
            else if (bestT <= 0.10f && _tapT <= 0f)
            {
                Tap(A_ATTACK); _tapT = 0.3f; _bikeActedAt = now;
                BotLog.Move(BotIndex, "biker-normal", "bike dx " + (int)bestDx + " speed " + (int)bestSpeed + " ttc " + bestT.ToString("0.00"));
                Expect("biker-normal", 0.5f, "Attack");
            }
            return true;
        }

        // ------------------------------------------------------------ M4.1: surf stages, hazards, bosses, diagnostics
        private static bool AttackingNow(Enemy n)
        {
            string sn = StateNameOf(n);
            return sn.IndexOf("Attack", StringComparison.Ordinal) >= 0 && sn.IndexOf("Hurt", StringComparison.Ordinal) < 0 && sn.IndexOf("Wait", StringComparison.Ordinal) < 0
                && sn.IndexOf("Reaction", StringComparison.Ordinal) < 0 && sn.IndexOf("Stun", StringComparison.Ordinal) < 0 && sn.IndexOf("Block", StringComparison.Ordinal) < 0;
        }

        /// <summary>Gap between the player and the boss's real hurtbox (all boxes merged): 0 when standing inside its x / y range. False if the game gives no box.</summary>
        private static bool BossReach(NonPlayerCharacter n, Vector2 me, out float gapX, out float gapY)
        {
            gapX = gapY = 9999f;
            try
            {
                int c = n.GetVulnerabilityBoxCount();
                if (c <= 0) return false;
                float minX = float.MaxValue, maxX = float.MinValue, lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < c; i++)
                {
                    Rectangle r = n.GetVulnerabilityBox(i, true);
                    if (r.Width <= 0) continue;
                    if (r.Left < minX) minX = r.Left; if (r.Right > maxX) maxX = r.Right;
                    Paris.Engine.Types.Range z = n.Get3DVulnerabilityBoxDepth(i);
                    float zl = Math.Min(z.Min, z.Max), zh = Math.Max(z.Min, z.Max);
                    if (zh - zl < 8f) { float mid = n.Position.Y; zl = Math.Min(zl, mid - 8f); zh = Math.Max(zh, mid + 8f); }
                    if (zl < lo) lo = zl; if (zh > hi) hi = zh;
                }
                if (minX > maxX || lo > hi) return false;
                gapX = me.X < minX ? minX - me.X : (me.X > maxX ? me.X - maxX : 0f);
                gapY = me.Y < lo ? lo - me.Y : (me.Y > hi ? me.Y - hi : 0f);
                return true;
            }
            catch { return false; }
        }

        private static string Diag(Player p)
        {
            try
            {
                string fx = "";
                for (int i = 0; i < (int)Player.StatusEffectTypes.Count; i++) if (p.HasStatusEffect((Player.StatusEffectTypes)i)) fx += (Player.StatusEffectTypes)i + ",";
                return "frozen " + p.IsFrozen + " radical " + p.IsInRadicalMode + " mutation " + p.IsMutationApplied + " control " + p.HasControl + " vehicle " + p.VehicleType + " effects [" + fx + "] stacks " + p.NinjaPowerStackCount + "/" + p.MaxNPStacks;
            }
            catch { return "diag n/a"; }
        }

        private void SurfWatch(Player p, Vector2 me, float now, bool pushing)
        {
            if (!pushing || (me - _stuckRefPos).Length() > 5f) { ClearStuckRef(me, now); return; }
            if (now - _stuckRefT < 3f || now - _surfWatchT < 8f) return;
            _surfWatchT = now; _stuckRefT = now;
            BotLog.Evt(BotIndex, "surf-immobile", "pushing " + V(_move * 10f) + " for 3 s without moving at " + V(me) + " state " + _sn + " intent '" + Intent + "' | " + Diag(p), 0f);
        }

        /// <summary>Learn from a super press without the debug log: the press worked if the special state started or a stack was spent; if not, back off (the 0.4.0 bots re-pressed it every ~2 s).</summary>
        private void SuperWatch(Player p, float now)
        {
            if (!_superWatch) return;
            if (_sn.IndexOf("Special", StringComparison.Ordinal) >= 0 || _sn.IndexOf("Super", StringComparison.Ordinal) >= 0 || p.NinjaPowerStackCount < _superStacksAt) { _superWatch = false; _superFails = 0; return; }
            if (now < _superWatchUntil) return;
            _superWatch = false; _superFails++;
            float cd = Math.Min(24f, 4f + 4f * _superFails);
            _superCd = Math.Max(_superCd, cd);
            BotLog.Evt(BotIndex, "super-failed", "no special state and no stack spent after the press (#" + _superFails + " in a row, state " + _sn + "); backing off " + (int)cd + " s | " + Diag(p), 0f);
        }

        // ---- hazards (cars in a lane etc.): the game shows a warning icon ~2 s before a hazard reaches the screen centre; leave its lane before it arrives
        private static readonly AccessTools.FieldRef<Hazard, WarningIcon> HzWarn = MakeHzWarn();
        private static AccessTools.FieldRef<Hazard, WarningIcon> MakeHzWarn() { try { return AccessTools.FieldRefAccess<Hazard, WarningIcon>("_warning"); } catch { return null; } }

        private static bool HazardLane(Hazard h, out float lo, out float hi, out float halfX)
        {
            lo = hi = halfX = 0f;
            try
            {
                float r = h.AttackRadiusPlayer > 0f ? h.AttackRadiusPlayer : h.AttackRadius;
                if (r > 0f) { float cy = h.Position.Y + h.AttackRadiusOffset.Y; r = Math.Min(r, 70f); lo = cy - r; hi = cy + r; halfX = r; return true; }
                if (h.GetAttackBoxCount() > 0)
                {
                    Paris.Engine.Types.Range z = h.Get3DAttackBoxDepth(0);
                    float zl = Math.Min(z.Min, z.Max), zh = Math.Max(z.Min, z.Max);
                    if (zh - zl < 24f) { zl = Math.Min(zl, h.Position.Y - 36f); zh = Math.Max(zh, h.Position.Y + 36f); }
                    lo = zl; hi = zh; halfX = Math.Max(40f, h.GetAttackBox(0, true).Width * 0.5f); return true;
                }
            }
            catch { }
            return false;
        }

        private bool TryHazard(Player p, Vector2 me, float now)
        {
            if (!p.OnGround || !InMoveState || _sp != Sp.None) return false;
            Scene2d sc = Scene2d.Active; if (sc == null) return false;
            Hazard bestH = null; float bestT = 99f, bLo = 0f, bHi = 0f; bool bestWarn = false;
            AutoScrollController asc = BotScene.Scroll(sc, now);
            foreach (Hazard h in BotScene.Hazards(sc, now))
            {
                if (h == null || !h.Active || !h.IsVisibleOnScreen) continue;
                try { if (h.AttackTargets.ToString() == "Enemies") continue; } catch { }
                bool warn = false;
                try { WarningIcon w = HzWarn != null ? HzWarn(h) : null; warn = w != null && w.Visible; } catch { }
                float vx = 0f;
                try { vx = h.BodyComponent.Displacement.Speed.X; } catch { }
                try { if (asc != null && asc.CanApplyPassiveSpeed) vx += asc.GetEffectiveScrollSpeed(h.AutoScrollSpeedFactor).X; } catch { }
                float dx = h.Position2D.X - me.X;
                bool toward = Math.Abs(vx) > 40f && dx * vx < 0f;
                if (!warn && !toward) continue;
                if (Math.Abs(vx) > 40f && !toward) continue;                // already past us
                if (Math.Abs(dx) > 760f) continue;
                float lo, hi, halfX;
                if (!HazardLane(h, out lo, out hi, out halfX)) continue;
                float t = Math.Abs(vx) > 40f ? Math.Max(0f, Math.Abs(dx) - halfX) / Math.Abs(vx) : (warn ? 1.5f : 99f);
                if (t > 2.6f) continue;
                if (t < bestT) { bestT = t; bestH = h; bLo = lo; bHi = hi; bestWarn = warn; }
            }
            if (bestH == null) { _hzCur = null; return false; }
            bool inLane = me.Y >= bLo - 14f && me.Y <= bHi + 14f;
            _hzGuardUntil = now + 0.6f; _hzLo = bLo; _hzHi = bHi;
            if (!ReferenceEquals(_hzCur, bestH))
            {
                _hzCur = bestH; _hzSeenAt = now; _hzMissed = Diff < Config.DiffHard && Rng.NextDouble() < DiffHazardMiss;   // M4.12: no roll on Hard
                BotLog.Evt(BotIndex, "hazard-seen", bestH.GetType().Name + " warning " + bestWarn + " arrives in " + bestT.ToString("0.0") + " s, lane y " + (int)bLo + ".." + (int)bHi + ", I am at y " + (int)me.Y + (inLane ? " (in the lane)" : " (safe)"), 0.3f);
            }
            if (!inLane) return false;
            if (Diff < Config.DiffHard && !HazardReady(now, bestT)) return false;   // M4.12: Easy / Normal notice it a moment later, sometimes late
            float up = me.Y - (bLo - 18f), down = (bHi + 18f) - me.Y;
            _hzDir = up <= down ? -1f : 1f;
            _move = new Vector2(0f, _hzDir); _ctlOk = false;
            if (now - _hzLogAt > 1.5f) { _hzLogAt = now; BotLog.Move(BotIndex, "hazard-dodge", bestH.GetType().Name + " in " + bestT.ToString("0.0") + " s, stepping " + (_hzDir < 0 ? "up" : "down") + " " + (int)Math.Min(up, down) + " px out of its lane"); }
            SetIntent("out of the hazard lane");
            return true;
        }

        /// <summary>For a moment after a hazard warning, do not let the fight logic walk us back into the lane.</summary>
        private void LaneGuard(float now)
        {
            if (now >= _hzGuardUntil || _hzGuardUntil <= 0f) return;
            Player p = ActivePlayer; if (p == null || !p.OnGround) return;
            float y = p.Position2D.Y;
            if (_move.Y == 0f) return;
            float ny = y + Math.Sign(_move.Y) * 8f;
            bool nowIn = y >= _hzLo - 14f && y <= _hzHi + 14f;
            bool willIn = ny >= _hzLo - 14f && ny <= _hzHi + 14f;
            if (willIn && !nowIn) _move.Y = 0f;
        }

        // ------------------------------------------------------------ M4.7: bike kick height gate + per-pass bike log
        /// <summary>The game turns an attack press in the air into a dive kick only above DiveKickMinHeight (50 px over the take-off). The bike kick presses 0.18 s after the jump button (jump height 384*t - 500*t^2 = ~53 px, with a frame or two of input delay ~46-50 px): a press just below 50 is lost and the bot lands with no kick.
        /// Only for the bike kick: hold the press until the jump is 58 px high (a frame or two later, the apex is at 74 px at 0.38 s); give up waiting after 0.15 s. Every other air attack and every case that was already high enough is unchanged.</summary>
        private bool BikeKickTooLow(float air, float now)
        {
            if (_airWhy == null || !_airWhy.StartsWith("kick the bike", StringComparison.Ordinal)) return false;
            if (air >= 58f) return false;
            if (_bikeKickWaitUntil < 0f) { _bikeKickWaitUntil = now + 0.15f; _bwNote(air); }
            if (now >= _bikeKickWaitUntil) { _bikeKickWaitUntil = -1f; return false; }
            return true;
        }
        [System.Diagnostics.Conditional("SB_DEBUG")]
        private void _bwNote(float air)
        {
        }

        /// <summary>Debug builds only (no-op here). When RunBiker acts (biker-jumpkick / biker-normal) remember the bike and our HP, and 1.4 s later log what happened: did the bike lose HP / die, did we get hurt, what state are we in.</summary>
        [System.Diagnostics.Conditional("SB_DEBUG")]
        private void BikeWatch(Player p, float now)
        {
        }

        // ---- bikers on surf stages (a separate variant: the board is stationary, the bike comes in at the scroll speed + its own, and the lane tolerance of the normal logic is too tight)
        private bool RunBikerSurf(Player p, Vector2 me, float now, Scene2d scene)
        {
            FootMotorcycle best = null; float bestT = 9f, bestDx = 0f, bestSpeed = 0f, bestDy = 0f; bool bestHurt = false;
            try
            {
                AutoScrollController asc = BotScene.Scroll(scene, now);
                foreach (object o in scene.GetGameObjectsOfType<FootMotorcycle>())
                {
                    FootMotorcycle b = o as FootMotorcycle;
                    if (b == null || !b.Active || !b.IsVisibleOnScreen) continue;
                    Vector2 d = b.Position2D - me;
                    if (Math.Abs(d.Y) > 80f || Math.Abs(d.X) > 560f || Math.Abs(b.Position.Z - p.Position.Z) > 30f) continue;
                    float vx = 0f;
                    try { vx = b.BodyComponent.Displacement.Speed.X; } catch { }
                    try { if (asc != null && asc.CanApplyPassiveSpeed) vx += asc.GetEffectiveScrollSpeed(b.AutoScrollSpeedFactor).X; } catch { }
                    if (Math.Abs(vx) < 40f) vx = b.HorizontalValue * 260f;
                    if (d.X * vx >= 0f) continue;                          // moving away from us / already past
                    float sp = Math.Abs(vx);
                    float t = (Math.Abs(d.X) - (_reachX + 10f)) / sp;
                    if (t < bestT) { bestT = t; best = b; bestDx = d.X; bestDy = d.Y; bestSpeed = sp; bestHurt = Hurtable(b); }
                }
            }
            catch (Exception ex) { Mod.LogOnce("bot-biker-surf", ex); return false; }
            if (best == null) { _bikeCur = null; return false; }
            if (!ReferenceEquals(_bikeCur, best))
            {
                _bikeCur = best; _bikeJumpPlan = Rng.NextDouble() < 0.7;
                BotLog.Note(BotIndex, "biker-surf", "bike incoming at dx " + (int)bestDx + " dy " + (int)bestDy + ", closing speed " + (int)bestSpeed + ", hurtable " + bestHurt + ", plan " + (_bikeJumpPlan ? "jump kick" : "normal hit"), 0.5f);
            }
            if (!InMoveState) return false;
            TargetLabel = "FootMotorcycle dx" + (int)bestDx; SetIntent(_bikeJumpPlan ? "surf biker: jump kick" : "surf biker: normal hit");
            // get into its lane (it aims at one player's lane): wider tolerance than the normal logic, and move even while it is still warning
            if (Math.Abs(bestDy) > _tolY * 0.7f) { _move.Y = Math.Sign(bestDy); }
            if (Math.Abs(bestDy) > _tolY + 14f) return true;
            if (!bestHurt) return true;                                    // still in its warning phase: stay lined up, nothing to hit yet
            bool facing = (bestDx >= 0f) == (p.HorizontalValue >= 0);
            if (!facing) { _move.X = Math.Sign(bestDx); return true; }
            if (_bikeJumpPlan)
            {
                if (bestT <= 0.34f && bestT > -0.15f && _jumpCd <= 0f)
                {
                    _jumpCd = 1.4f; Tap(A_JUMP); _airTapAt = now + 0.18f; _airWhy = "kick the bike (surf), ttc " + bestT.ToString("0.00"); _bikeActedAt = now;
                    BotLog.Move(BotIndex, "biker-jumpkick", "surf: bike dx " + (int)bestDx + " dy " + (int)bestDy + " speed " + (int)bestSpeed + " ttc " + bestT.ToString("0.00"));
                    Expect("biker-jumpkick", 0.9f, "Aerial", "DiveKick", "FlyingKick");
                }
            }
            else if (bestT <= 0.14f && bestT > -0.2f && _tapT <= 0f)
            {
                Tap(A_ATTACK); _tapT = 0.3f; _bikeActedAt = now;
                BotLog.Move(BotIndex, "biker-normal", "surf: bike dx " + (int)bestDx + " dy " + (int)bestDy + " speed " + (int)bestSpeed + " ttc " + bestT.ToString("0.00"));
                Expect("biker-normal", 0.5f, "Attack");
            }
            return true;
        }

        // ------------------------------------------------------------ M4.3: cheer-up between bots
        // The game's high-five (CheerUp): both players stand facing each other in the same lane, within about 96 px (their 48x12 reach ellipses touch), both walking / idle on the ground;
        // the one who presses the cheer-up button gives min(missing HP, 2) HP to the other (the giver is billed 2 HP for a moment and gets it back when the animation ends).
        // Healthy bot (>= 3/4 HP, > 3 HP) walks to a bot that is at or under a third HP, only while nothing is fighting either of them; the low bot only turns to face the giver.
        private float _cheerSince = -1f;
        private static bool IsBotPlayer(Player o) { try { return o != null && o.PInfo != null && o.PInfo.Controller is BotController; } catch { return false; } }
        private bool TryCheerAlly(Scene2d scene, Player p, Vector2 me, float now)
        {
            try
            {
                if (p.MaxHP <= 0 || _nearestEnemyDist < 200f || now - _hurtAt < 3f || _rangedThreat) { _cheerSince = -1f; return false; }
                bool iAmLow = p.HP * 3 <= p.MaxHP;
                bool iAmFit = p.HP * 4 >= p.MaxHP * 3 && p.HP > 3;
                if (!iAmLow && !iAmFit) { _cheerSince = -1f; return false; }
                // the low ally the nearest fit bot looks after
                Player low = null; float bd = 300f * 300f;
                foreach (Player o in scene.Players.Values)
                {
                    if (o == null || !o.Active || o.IsDead || o.IsInStateKO || !o.IsVisibleOnScreen || o.MaxHP <= 0 || o.HP <= 0 || !IsBotPlayer(o)) continue;
                    if (o.HP * 3 > o.MaxHP) continue;
                    if (iAmFit && !ReferenceEquals(o, p)) { float d2 = Vector2.DistanceSquared(o.Position2D, me); if (d2 < bd) { bd = d2; low = o; } }
                    else if (iAmLow && ReferenceEquals(o, p)) low = o;
                }
                if (iAmFit)
                {
                    if (low == null) { _cheerSince = -1f; return false; }
                    // am I the nearest fit bot to it?
                    float myD = Vector2.DistanceSquared(low.Position2D, me);
                    foreach (Player o in scene.Players.Values)
                    {
                        if (o == null || ReferenceEquals(o, p) || ReferenceEquals(o, low) || !o.Active || o.IsDead || o.IsInStateKO || !IsBotPlayer(o) || o.MaxHP <= 0) continue;
                        if (o.HP * 4 < o.MaxHP * 3 || o.HP <= 3) continue;
                        if (Vector2.DistanceSquared(low.Position2D, o.Position2D) < myD - 1f) return false;
                    }
                    if (_cheerCd > 0f) return false;
                    TargetLabel = "high-five P" + (low.PInfo != null ? low.PInfo.PlayerID + 1 : 0);
                    return CheerApproach(p, me, low, now, false, ref _cheerSince);
                }
                // I am the low one: when a fit bot is walking up to me, turn to face it (the game needs both to face each other)
                Player nearFit = null; float nd = 110f * 110f;
                foreach (Player o in scene.Players.Values)
                {
                    if (o == null || ReferenceEquals(o, p) || !o.Active || o.IsDead || o.IsInStateKO || !IsBotPlayer(o) || o.MaxHP <= 0) continue;
                    if (o.HP * 4 < o.MaxHP * 3 || o.HP <= 3) continue;
                    float d2 = Vector2.DistanceSquared(o.Position2D, me); if (d2 < nd) { nd = d2; nearFit = o; }
                }
                if (nearFit == null || p.IsFacing(nearFit)) return false;
                if (!p.OnGround || Math.Abs(nearFit.Position.Z - p.Position.Z) > 4f) return false;
                float sd = nearFit.Position2D.X >= me.X ? 1f : -1f;
                _move.X = sd; SetIntent("high-five: turning to the bot that is coming to cheer me up");
                return true;
            }
            catch { _cheerCd = 30f; _cheerSince = -1f; return false; }
        }

        // ------------------------------------------------------------ M4.5: the high-five itself (human and bot targets use the same alignment)
        // The game's high-five check (Player.ShouldCheerUp) needs, in our reading, all of: giver not KO; target alive, standing / walking (state 0 or 1, no mutation), on screen, on the ground; giver on the ground;
        // giver faces target AND target faces giver (sprite flip vs relative X); exactly the same Z; their 48x12 CheerUp ellipses touch (about 96 px in X, 24 px in Y).
        // The giver is then eased to 56 px in front of the target. So: walk to the side of the target it FACES, about 52 px away, turn to it, and only press when all of that is true.
        private float _cheerTapAt = -1f, _hcSince = -1f, _cheerLogAt = -9f; private int _cheerTries;
        private string CheerWhyNot(Player p, Player t)
        {
            if (!p.OnGround) return "I am in the air";
            if (!t.OnGround) return "it is in the air";
            if (Math.Abs(t.Position.Z - p.Position.Z) > 0.01f) return "different height (dz " + (int)(t.Position.Z - p.Position.Z) + ")";
            if (!t.IsVisibleOnScreen) return "it is off screen";
            if (!t.IsReadyForCheeredUp) return "it is busy (" + StateNameOf(t) + ")";
            if (!p.IsFacing(t)) return "I do not face it";
            if (!t.IsFacing(p)) return "it does not face me";
            float dx = t.Position2D.X - p.Position2D.X, dy = t.Position2D.Y - p.Position2D.Y;
            if (dx * dx + dy * dy >= 96f * 96f || (dx * dx) / (96f * 96f) + (dy * dy) / (24f * 24f) >= 1f) return "too far apart (dx " + (int)dx + ", dy " + (int)dy + ")";
            return null;
        }
        private string CheerSnapshot(Player p, Player t)
        {
            return "dx " + (int)(t.Position2D.X - p.Position2D.X) + " dy " + (int)(t.Position2D.Y - p.Position2D.Y) + " dz " + (int)(t.Position.Z - p.Position.Z)
                 + ", I face it " + p.IsFacing(t) + ", it faces me " + t.IsFacing(p) + ", its state " + StateNameOf(t) + ", mine " + _sn + ", on ground " + p.OnGround + "/" + t.OnGround;
        }
        private bool CheerApproach(Player p, Vector2 me, Player t, float now, bool human, ref float since)
        {
            string who = human ? "human" : "bot ally";
            if (_cheerTapAt >= 0f)
            {
                if (now - _cheerTapAt < 0.4f) { _move = Vector2.Zero; SetIntent("high-five: pressed, waiting to see it take"); return true; }
                _cheerTapAt = -1f; _cheerTries++;
                BotLog.Evt(BotIndex, "cheer-up-rejected", "the game ignored the press at the " + who + " (try " + _cheerTries + "): " + CheerSnapshot(p, t), 0f);
                if (_cheerTries >= 3) { _cheerTries = 0; since = -1f; _cheerCd = 15f; BotLog.Evt(BotIndex, "cheer-give-up", "3 presses ignored by the game, resting 15 s", 0f); return false; }
            }
            if (since < 0f) since = now;
            float window = human ? 10f : 8f;
            if (now - since > window)
            {
                string why = CheerWhyNot(p, t);
                since = -1f; _cheerTries = 0; _cheerCd = 15f;
                BotLog.Evt(BotIndex, "cheer-give-up", "could not line up a high-five with the " + who + " in " + (int)window + " s: " + (why ?? "all conditions held at the end") + " (" + CheerSnapshot(p, t) + ")", 0f);
                return false;
            }
            Vector2 d = t.Position2D - me;
            float tFace = t.HorizontalValue >= 0 ? 1f : -1f;                       // the way the target looks
            float stand = human ? tFace : (d.X >= 0f ? -1f : 1f);                   // side of the target I stand on: in front of a human, my own side of a bot (it turns to me)
            float off = (me.X - t.Position2D.X) * stand;                            // how far out on that side I am
            if (off < 30f || off > 80f || Math.Abs(d.Y) > 6f)
            {
                MoveTo(t.Position2D + new Vector2(stand * 52f, 0f), 6f, 3f);
                SetIntent("high-five: walking to the " + who + (human ? " (to the side it faces)" : ""));
                return true;
            }
            _move = Vector2.Zero;
            if (!p.IsFacing(t)) { _move.X = -stand; SetIntent("high-five: turning to the " + who); return true; }
            string reason = CheerWhyNot(p, t);
            if (reason != null)
            {
                SetIntent("high-five: waiting (" + reason + ")");
                if (now - _cheerLogAt > 2.5f) { _cheerLogAt = now; BotLog.Evt(BotIndex, "cheer-wait", who + ": " + reason, 0f); }
                return true;
            }
            Tap(A_REVIVE); _cheerTapAt = now;
            BotLog.Move(BotIndex, "cheer-up", who + " HP " + t.HP + "/" + t.MaxHP + ", mine " + p.HP + "/" + p.MaxHP); Expect("cheer-up", 0.7f, "CheerUp");
            return true;
        }

        /// <summary>A human at or under a third health gets a high-five from the nearest healthy bot (same rules as between bots; nothing fighting, 10 s window, 3 presses at most).</summary>
        private bool TryCheerHuman(Scene2d scene, Player p, Vector2 me, float now)
        {
            try
            {
                Player h = _leader;
                if (h == null || h.PInfo == null || h.PInfo.Controller is BotController || p.MaxHP <= 0 || h.MaxHP <= 0) { _hcSince = -1f; return false; }
                if (_cheerCd > 0f || !h.Active || h.IsDead || h.IsInStateKO || !h.IsVisibleOnScreen || h.HP <= 0 || h.HP * 3 > h.MaxHP) { _hcSince = -1f; return false; }
                if (p.HP * 4 < p.MaxHP * 3 || p.HP <= 3 || _nearestEnemyDist < 200f || now - _hurtAt < 3f || _rangedThreat) { _hcSince = -1f; return false; }
                float myD = Vector2.DistanceSquared(h.Position2D, me);
                if (myD > 300f * 300f) { _hcSince = -1f; return false; }
                foreach (Player o in scene.Players.Values)
                {
                    if (o == null || ReferenceEquals(o, p) || ReferenceEquals(o, h) || !o.Active || o.IsDead || o.IsInStateKO || !IsBotPlayer(o) || o.MaxHP <= 0) continue;
                    if (o.HP * 4 < o.MaxHP * 3 || o.HP <= 3) continue;
                    if (Vector2.DistanceSquared(h.Position2D, o.Position2D) < myD - 1f) { _hcSince = -1f; return false; }   // a fitter bot is closer: it goes
                }
                TargetLabel = "high-five P" + (h.PInfo.PlayerID + 1);
                return CheerApproach(p, me, h, now, true, ref _hcSince);
            }
            catch { _cheerCd = 30f; _hcSince = -1f; return false; }
        }

        // ------------------------------------------------------------ sprinting
        private void StartSprint(float dir, float goalX, bool attack, string why, float now)
        {
            if (dir == 0f) return;
            if (PitNear(ActivePlayer, Math.Sign(dir), 360f)) { _sprintCd = 2f; return; }   // M4.6: no sprint toward a pit (a sprint cannot jump, the slide button is the only one)
            _sp = Sp.Stop; _spT = 0f; _spStarted = now; _spGoalX = goalX; _spAttack = attack; _spWhy = why; _sprintDirX = dir; _spSawSprint = false;
            _sprintCd = 6f;
        }

        /// <summary>Drive the double-tap protocol the game's own sprint detector expects: stop, tap a direction, release briefly, push again. Returns true when it owns this frame.</summary>
        private bool RunSprint(Player p, Vector2 me, float now, float dt)
        {
            _spT += dt;
            bool sprinting = _sn == "StateSprint";
            if (now - _spStarted > 6f || (_sp != Sp.Running && !InMoveState && !sprinting && _spT > 0.3f)) { EndSprint(false, "interrupted in " + _sn); return false; }
            if (_nearestEnemyDist < 60f && !_spAttack) { EndSprint(false, "enemy close"); return false; }
            switch (_sp)
            {
                case Sp.Stop:
                    SetIntent("sprint: ready");
                    if (_sn == "StateIdle" || _spT > 0.45f) { _sp = Sp.Press1; _spT = 0f; }
                    return true;
                case Sp.Press1:
                    SetIntent("sprint: tap 1"); _sprintJp = true; _move = new Vector2(_sprintDirX, 0f);
                    if (sprinting) { _sp = Sp.Running; _spT = 0f; return true; }   // "sprint on flick" setting: already running
                    if (_spT >= 0.10f) { _sp = Sp.Release; _spT = 0f; }
                    return true;
                case Sp.Release:
                    SetIntent("sprint: release"); _sprintJp = true;
                    if (_spT >= 0.08f) { _sp = Sp.Press2; _spT = 0f; }
                    return true;
                case Sp.Press2:
                    SetIntent("sprint: tap 2"); _sprintJp = true; _move = new Vector2(_sprintDirX, 0f);
                    if (sprinting) { _sp = Sp.Running; _spT = 0f; return true; }
                    if (_spT > 0.4f) { EndSprint(false, "no sprint after double tap (state " + _sn + ")"); return false; }
                    return true;
                case Sp.Running:
                    if (!sprinting) { if (_spT > 0.25f) { EndSprint(true, "sprint ended in " + _sn); return false; } return true; }
                    if (!_spSawSprint)
                    {
                        _spSawSprint = true; BotLog.Move(BotIndex, "sprint-start", _spWhy); BotLog.Result(BotIndex, "sprint-start", true);
                        NoteState("sprint-start", "ok", _sn);
                    }
                    float dx = _spGoalX - me.X;
                    float dy = _target != null ? _target.Position2D.Y - me.Y : (_leader != null ? _leader.Position2D.Y - me.Y : 0f);
                    float y = Math.Abs(dy) > 6f ? Math.Sign(dy) * 0.4f : 0f;
                    _move = new Vector2(_sprintDirX, y);
                    SetIntent(_spAttack ? "sprint attack" : "sprint: catch up");
                    bool passed = Math.Sign(dx) != Math.Sign(_sprintDirX);
                    if (_spAttack)
                    {
                        if (_target == null || !_target.Active) { EndSprint(true, "target gone"); return false; }
                        float ax = Math.Abs(_target.Position2D.X - me.X);
                        if (_target is Boss) { float bgx, bgy; if (BossReach(_target, me, out bgx, out bgy)) { ax = Math.Min(ax, bgx + 20f); dy = Math.Min(Math.Abs(dy), bgy); } }
                        if (ax < 75f && Math.Abs(dy) <= _tolY + 4f)
                        {
                            bool slide = RollAdv(0.35);
                            Tap(slide ? A_JUMP : A_ATTACK);
                            string mv = slide ? "sprint-slide" : "sprint-bash";
                            BotLog.Move(BotIndex, mv, "ax " + (int)ax + " dy " + (int)dy + " " + Name(_target));
                            Expect(mv, 0.6f, slide ? "SlidingKick" : "ShoulderBash");
                            EndSprint(true, "attacked"); _tapT = 0.4f; _comboPause = 0.4f;
                            return true;
                        }
                    }
                    else if (Math.Abs(dx) < 60f || passed) { EndSprint(true, "caught up"); BotLog.Evt(BotIndex, "sprint-caught-up", "dx " + (int)dx, 0f); _move = Vector2.Zero; return true; }
                    return true;
            }
            return false;
        }

        private void EndSprint(bool ok, string why)
        {
            if (!ok && _spSawSprint) ok = true;   // it did sprint: running into something / enemy close is not a failed start
            if (!ok) { BotLog.Evt(BotIndex, "sprint-fail", why + " (" + _spWhy + ")", 0f); BotLog.Result(BotIndex, "sprint-start", false); NoteState("sprint-start", "FAIL", _sn); _sprintCd = 10f; }
            _sp = Sp.None;
        }



        // ------------------------------------------------------------ M4.6: pits and gaps
        // How the game does it: a collision tile of kind PIT has no floor (height -128). A body whose centre is over one falls; at Z -128 the player takes 1 damage, plays the 2.5 s drop-in-hole
        // animation and is put back on the last floor spot (killed when already KO). Only attack-type states (and flips) refuse to step onto a pit tile; walking, jumping and falling do not.
        // So: the bot reads the same tiles (Playfield.GetHeightAtPos().Flag has PIT), never steps onto one, jumps a gap it can clear when its goal is on the far side, and otherwise waits at the edge.
        private enum Gp { None, Air }
        private Gp _gp; private float _gpAt, _gpDir, _gpEdgeX, _gpFarX, _gpW; private int _gpType, _gpTries; private Vector2 _gpFrom; private string _gpWhy = "";
        private float _gpNextTry, _gpTriesAt, _gpLaneUntil, _gpLaneY, _gpLaneEvalAt, _pitLogAt, _gpWaitLogAt;
        private float _gpLaneX, _gpWaitSince, _gpWaitSeen; private bool _gpLaneBack;   // M4.7: lane point with a retreat, wait timer
        // M4.8 (Episode 13): the clamp ran before the guard, so inside 8 px of the edge the guard never saw the move and never jumped; stall watchdog; double after a miss
        private float _clampSx, _clampAt = -1f;
        private float _pbSince = -1f, _pbSeen = -9f, _pbSx, _wdUntil, _wdBackX, _wdSx, _wdNext, _wdForceUntil; private Vector2 _pbPos; private bool _gpForceDbl;
        private bool _inHole; private Vector2 _lastSafe; private bool _hasSafe; private int _pitFalls; private float _holeAt;
        private Playfield _pitPf;
        private Func<float, float, bool> _pitFn;
        private Func<float, float, bool> PitFn
        {
            get
            {
                if (_pitFn == null) _pitFn = (x, y) => PitMap.IsPit(_pitPf, x, y);
                return _pitFn;
            }
        }

        private Func<float, float, float> _hgtFn;
        private Func<float, float, float> HgtFn
        {
            get
            {
                if (_hgtFn == null) _hgtFn = (x, y) => { try { return _pitPf.GetHeightAtPos(new Vector2(x, y)).Height; } catch { return 0f; } };
                return _hgtFn;
            }
        }

        private Playfield PitField()
        {
            Playfield pf = null;
            try { Scene2d sc = Scene2d.Active; pf = sc == null ? null : sc.Playfield; } catch { }
            if (PitMap.NewField(pf))
            {
                Playfield f = PitMap.Field(pf);   // indexes the new field
                BotLog.Note(BotIndex, "pit-map", f == null ? "this scene has no pit tiles" : "pit tiles: " + PitMap.Summary, 0f);
            }
            _pitPf = PitMap.Field(pf);
            return _pitPf;
        }

        /// <summary>Is there a pit tile starting within dist px of the player along dirX (ground level probe)?</summary>
        private bool PitNear(Player p, float dirX, float dist)
        {
            if (p == null || dirX == 0f) return false;
            try
            {
                if (PitField() == null) return false;
                float st, w; Vector2 me = p.Position2D;
                return PitMap.Scan(PitFn, me.X, me.Y, dirX, 0f, dist, 4f, 40f, out st, out w);
            }
            catch { return false; }
        }

        private void PitAirClamp(Vector2 me)
        {
            if (_move.X == 0f) return;
            if (PitField() == null) return;
            float st, w;
            if (PitMap.Scan(PitFn, me.X, me.Y, Math.Sign(_move.X), 0f, 44f, 4f, 40f, out st, out w)) _move.X = 0f;
        }

        /// <summary>Last line of defence: whatever the decision code wanted, a ground step toward a pit tile that is 8 px away or closer is cut (the walk goes 1.5 px per frame).</summary>
        private void PitClamp(float now)
        {
            Player p = ActivePlayer; if (p == null || !p.OnGround || _gp == Gp.Air || _surfNow) return;
            if (_move.LengthSquared() < 0.01f) return;
            if (!(InMoveState || _sn == "StateSprint")) return;
            if (PitField() == null) return;
            Vector2 me = p.Position2D; float st, w;
            if (_move.X != 0f && PitMap.Scan(PitFn, me.X, me.Y, Math.Sign(_move.X), 0f, 8f, 4f, 20f, out st, out w))
            {
                // M4.8: remember where it wanted to go; PitGuard (which runs later, in AfterThink) decides jump / wait from this spot instead of never seeing the move
                if (_sn != "StateSprint") { _clampSx = Math.Sign(_move.X); _clampAt = now; }
                MarkPitBlock(now, me, Math.Sign(_move.X));
                _move.X = 0f; PitNote(now, "clamped at " + V(me) + ", pit " + (int)st + " px ahead (x)");
            }
            if (_move.Y != 0f && PitMap.Scan(PitFn, me.X, me.Y, 0f, Math.Sign(_move.Y), 8f, 4f, 20f, out st, out w)) { _move.Y = 0f; PitNote(now, "clamped at " + V(me) + ", pit " + (int)st + " px ahead (y)"); }
        }

        private void PitNote(float now, string text)
        {
            if (now - _pitLogAt < 4f) return;
            _pitLogAt = now;
            BotLog.Evt(BotIndex, "pit-block", text + ", intent '" + Intent + "'", 0f);
        }

        /// <summary>Per frame bookkeeping: remember the last safe floor spot, notice falls into a pit, and settle a gap jump (landed on the far side or not).</summary>
        private void PitWatch(Player p, float now)
        {
            Vector2 pos = p.Position2D;
            bool inHole = _sn == "StateFallInHole" || _sid == 65 || (p.Position.Z < -40f && !p.OnGround);
            if (inHole && !_inHole)
            {
                _inHole = true; _holeAt = now; _pitFalls++;
                string flags = "";
                try
                {
                    Playfield pf = Scene2d.Active == null ? null : Scene2d.Active.Playfield;
                    if (pf != null && pf.Is3D) { HeightmapInfo hi = pf.GetHeightAtPos(_hasSafe && _gp != Gp.Air ? pos : pos); flags = ", tile here " + hi.Flag + " h " + hi.Height; }
                }
                catch { }
                bool gj = _gp == Gp.Air;
                BotLog.Evt(BotIndex, "pit-fall", "fell at " + V(pos) + " (last floor " + (_hasSafe ? V(_lastSafe) : "?") + "), z " + (int)p.Position.Z + ", state " + _sn + " (was " + _prevSn + "), intent '" + Intent + "', target " + Name(_target)
                    + ", leader " + (_leader != null ? V(_leader.Position2D) : "none") + ", move " + V(_move * 10f) + (gj ? ", DURING a gap jump (" + _gpWhy + ")" : "") + ", hp " + SafeHpP(p) + flags, 0f);
                if (gj) { BotLog.Evt(BotIndex, "gap-missed", "fell into the pit: took off at " + V(_gpFrom) + ", gap " + (int)_gpW + " px, " + (_gpType == 2 ? "double" : "single") + " jump, try " + _gpTries, 0f); _gpNextTry = now + 4f; if (_gpType == 1) _gpForceDbl = true; }
                _gp = Gp.None; _sp = Sp.None;
            }
            else if (!inHole && _inHole && now - _holeAt > 0.2f && _sn != "StateFallInHole")
            {
                _inHole = false;
                BotLog.Evt(BotIndex, "pit-respawn", "back on the floor at " + V(pos) + " after " + (now - _holeAt).ToString("0.0") + "s, hp " + SafeHpP(p) + (p.IsInStateKO ? ", KO" : ""), 0f);
                _gpNextTry = Math.Max(_gpNextTry, now + 1f);
            }
            if (_gp == Gp.Air)
            {
                float t = now - _gpAt;
                if (t > 0.25f && p.OnGround && !inHole)
                {
                    bool bad = false; try { Playfield pf = _pitPf; bad = pf != null && PitMap.IsPit(pf, pos.X, pos.Y); } catch { }
                    bool far = (pos.X - _gpFarX) * _gpDir >= -2f && !bad;
                    if (far) { BotLog.Evt(BotIndex, "gap-landed", "crossed the " + (int)_gpW + " px gap (" + (_gpType == 2 ? "double" : "single") + " jump from " + V(_gpFrom) + ", landed at " + V(pos) + ") in " + t.ToString("0.00") + "s", 0f); _gpTries = 0; _gpForceDbl = false; }
                    else { if (_gpType == 1) _gpForceDbl = true; BotLog.Evt(BotIndex, "gap-missed", "landed " + (((pos.X - _gpEdgeX) * _gpDir) < 0f ? "back on the near side" : "short of the far edge") + " at " + V(pos) + " (gap " + (int)_gpW + " px, far edge x " + (int)_gpFarX + ", " + (_gpType == 2 ? "double" : "single") + " jump, try " + _gpTries + ")", 0f); _gpNextTry = now + 1.5f; }
                    _gp = Gp.None;
                }
                else if (t > 4f) _gp = Gp.None;
            }
            if (p.OnGround && !inHole && _sn != "StateFallInHole" && (InMoveState || _sn == "StateSprint"))
            {
                bool over = false; try { Playfield pf = _pitPf; over = pf != null && PitMap.IsPit(pf, pos.X, pos.Y); } catch { }
                if (!over) { _lastSafe = pos; _hasSafe = true; }
            }
        }

        private void MarkPitBlock(float now, Vector2 me, float sx)
        {
            if (_pbSince < 0f || now - _pbSeen > 1.0f || Vector2.Distance(me, _pbPos) > 6f) { _pbSince = now; _pbPos = me; }
            _pbSeen = now; _pbSx = sx;
        }

        /// <summary>M4.8 wrapper. (1) The clamp (runs first) zeroes a step toward a pit that is 8 px away or closer; the guard used to see no move and never jumped from there (Episode 13: Splinter / April stood at 0-2 px from the edge for ever).
        /// The clamp now leaves the intended direction and the guard decides again: jump from where it stands, or wait. (3) Watchdog: blocked at the same spot for over 6 s with the human across: back off 24-40 px, re-pick the lane, double jump.</summary>
        private bool PitGuard(Player p, Vector2 me, float now)
        {
            if (p == null || !p.OnGround || _gp == Gp.Air || !InMoveState || PitField() == null) return PitGuardCore(p, me, now);
            // a re-plan is backing away from the edge
            if (_wdUntil > now)
            {
                if ((me.X - _wdBackX) * _wdSx > 3f && !PitMap.IsPit(_pitPf, me.X - _wdSx * 6f, me.Y)) { _move = new Vector2(-_wdSx, 0f); SetIntent("backing off from a pit edge to re-plan"); return true; }
                _wdUntil = 0f;
            }
            else if (PitMap.StallDue(now, _pbSince, _pbSeen, _wdNext) && _leader != null && _pbSx != 0f && (_leader.Position2D.X - me.X) * _pbSx > 60f)
            {
                float back = 24f + (float)Rng.NextDouble() * 16f;
                _wdSx = _pbSx; _wdBackX = me.X - _wdSx * back; _wdUntil = now + 2.5f; _wdNext = now + 12f; _wdForceUntil = now + 14f;
                _gpForceDbl = true; _gpLaneEvalAt = 0f; _gpNextTry = Math.Min(_gpNextTry, now); _gpLaneBack = false; _gpLaneUntil = 0f;
                BotLog.Evt(BotIndex, "pit-replan", "blocked at the edge near " + V(_pbPos) + " for " + (now - _pbSince).ToString("0.0") + "s with the human across (" + V(_leader.Position2D) + "): backing off " + (int)back + " px, new lane, double jump", 0f);
                _pbSince = -1f;
                _move = new Vector2(-_wdSx, 0f); SetIntent("backing off from a pit edge to re-plan");
                return true;
            }
            bool restored = false;
            if (_clampAt == now && Math.Abs(_move.X) < 0.01f && _clampSx != 0f) { _move.X = _clampSx; restored = true; }
            bool r = PitGuardCore(p, me, now);
            if (restored && _gp != Gp.Air && _move.X * _clampSx > 0f)
            {
                float st, w;   // the guard did not act on it: the step into the pit is still cut
                if (PitMap.Scan(PitFn, me.X, me.Y, _clampSx, 0f, 8f, 4f, 20f, out st, out w)) { _move.X = 0f; r = true; }
            }
            return r;
        }

        /// <summary>Runs after the decisions (ground, move states). Returns true when it changed the move.</summary>
        private bool PitGuardCore(Player p, Vector2 me, float now)
        {
            if (_gp == Gp.Air || p == null || !p.OnGround) return false;
            bool sprinting = _sn == "StateSprint";
            if (!(InMoveState || sprinting)) return false;
            if (_move.LengthSquared() < 0.01f) return false;
            if (PitField() == null) return false;
            float sx = _move.X > 0.2f ? 1f : (_move.X < -0.2f ? -1f : 0f);
            float sy = _move.Y > 0.2f ? 1f : (_move.Y < -0.2f ? -1f : 0f);
            bool changed = false; float st, w;

            if (sprinting)
            {
                // a sprint cannot stop on a coin: ends well before the edge (and was not started toward one, see StartSprint)
                if (sx != 0f && PitMap.Scan(PitFn, me.X, me.Y, sx, 0f, 110f, 4f, 40f, out st, out w))
                {
                    EndSprint(true, "pit ahead"); _sp = Sp.None; _move = Vector2.Zero;
                    PitNote(now, "sprint cut at " + V(me) + ", pit " + (int)st + " px ahead");
                    return true;
                }
                return false;
            }

            // M4.7: a committed lane change (Episode 13: slanted edge, the narrow part of the gap is on another row and further back): back off along this row, then walk the column to that row
            if (_gpLaneBack && now < _gpLaneUntil && _target == null)
            {
                float ddx = _gpLaneX - me.X, ddy = _gpLaneY - me.Y;
                if (Math.Abs(ddx) > 4f) { _move = new Vector2(Math.Sign(ddx), 0f); SetIntent("moving to a crossable part of the gap"); return true; }
                if (Math.Abs(ddy) > 5f) { _move = new Vector2(0f, Math.Sign(ddy)); SetIntent("moving to a crossable part of the gap"); return true; }
                _gpLaneBack = false; _gpLaneUntil = 0f; _gpLaneEvalAt = now + 3f;
            }
            else if (_gpLaneBack) _gpLaneBack = false;

            if (sy != 0f && PitMap.Scan(PitFn, me.X, me.Y, 0f, sy, 24f, 4f, 40f, out st, out w) && st <= 10f) { _move.Y = 0f; changed = true; PitNote(now, "side step blocked at " + V(me) + ", pit " + (int)st + " px away (y)"); }
            if (sx != 0f && sy != 0f && _move.Y != 0f && PitMap.IsPit(_pitPf, me.X + sx * 10f, me.Y + sy * 10f)) { _move.Y = 0f; changed = true; }
            if (sx == 0f) return changed;
            if (!PitMap.Scan(PitFn, me.X, me.Y, sx, 0f, 34f, 4f, 260f, out st, out w)) return changed;

            // a pit starts within 34 px of the way we are heading. Where is the goal?
            Vector2 dest = Vector2.Zero; bool hasDest = false;
            if (_goalSet) { dest = _goal; hasDest = true; } else if (_leader != null) { dest = _leader.Position2D; hasDest = true; }
            bool finite = !float.IsInfinity(w);
            bool across = hasDest && finite && (dest.X - me.X) * sx > st + w - 4f;
            bool leaderAcross = _leader != null && finite && (_leader.Position2D.X - me.X) * sx > st + w - 4f;
            float ws = Math.Max(40f, p.WalkSpeed);
            float cap1 = ws * 0.81f - 18f, cap2 = ws * (p.CanDoubleJump ? 1.5f : 1.28f) - 18f;
            bool chasing = _target != null;

            // M4.7: at this row the gap cannot be crossed (open ended, or wider than a double jump clears) but the goal is on the far side: look for a crossable row (Episode 13: the edge is slanted, 92 px wide
            // on one row, 260+ px on the rows above it; 0.4.6 only looked for lanes when the width at its own row was finite, and then picked a pit-free strip behind the playfield)
            bool notCrossable = !finite || w > cap2 + 14f;
            bool openFar = hasDest && !finite && (dest.X - me.X) * sx > st + 40f;
            if ((across || openFar) && !chasing && notCrossable && now >= _gpLaneEvalAt)
            {
                _gpLaneEvalAt = now + 0.5f;
                float lx, ly, lreach = Math.Min(260f, Math.Abs(dest.X - me.X));
                float lbw = PitMap.BestLane2(PitFn, HgtFn, me.X, me.Y, sx, lreach, finite ? w : 1e9f, 24f, out lx, out ly);
                if (lbw <= cap2 + 14f && (Math.Abs(lx - me.X) > 6f || Math.Abs(ly - me.Y) > 6f))
                {
                    _gpLaneX = lx; _gpLaneY = ly; _gpLaneUntil = now + 6f; _gpLaneBack = true;
                    BotLog.Evt(BotIndex, "gap-lane", "pit " + (finite ? (int)w + " px wide" : "open ended") + " at " + V(me) + ", " + (lbw <= 0.5f ? "no pit" : (int)lbw + " px") + " at " + V(new Vector2(lx, ly)) + " (crossable, backing off " + (int)Math.Abs(lx - me.X) + " px, sideways " + (int)Math.Abs(ly - me.Y) + " px): taking that lane", 0f);
                    float ddx0 = lx - me.X;
                    _move = Math.Abs(ddx0) > 4f ? new Vector2(Math.Sign(ddx0), 0f) : new Vector2(0f, Math.Sign(ly - me.Y)); SetIntent("moving to a crossable part of the gap");
                    return true;
                }
                if (!finite && now - _gpWaitLogAt > 10f)
                {
                    _gpWaitLogAt = now;
                    BotLog.Evt(BotIndex, "gap-wait", "pit is open ended at " + V(me) + " and no crossable row within reach (best " + (lbw >= 1e8f ? "none" : (int)lbw + " px") + ", I clear " + (int)cap2 + "): waiting for the human / the next try", 0f);
                }
            }

            if (across && !chasing)
            {
                // a lane with a narrower gap (or a floor all the way) next to us?  (0.4.6 behaviour, unchanged for gaps this bot can already clear)
                if (w > cap1 * 0.6f && !notCrossable && now >= _gpLaneEvalAt)
                {
                    float laneY, reach = Math.Min(260f, Math.Abs(dest.X - me.X));
                    if (now < _gpLaneUntil && Math.Abs(_gpLaneY - me.Y) > 6f) { _move = new Vector2(0f, Math.Sign(_gpLaneY - me.Y)); SetIntent("moving to a narrower part of the gap"); return true; }
                    _gpLaneEvalAt = now + 0.5f;
                    float bw = PitMap.BestLane(PitFn, me.X, me.Y, sx, reach, w, out laneY);
                    if (bw < w - 16f && Math.Abs(laneY - me.Y) > 6f)
                    {
                        _gpLaneY = laneY; _gpLaneUntil = now + 2.5f;
                        BotLog.Evt(BotIndex, "gap-lane", "pit " + (int)w + " px wide at y " + (int)me.Y + ", " + (bw <= 0.5f ? "no pit" : (int)bw + " px") + " at y " + (int)laneY + ": taking that lane", 0f);
                        _move = new Vector2(0f, Math.Sign(laneY - me.Y)); SetIntent("moving to a narrower part of the gap");
                        return true;
                    }
                }
                // M4.7: a bot that has waited 8+ s at the edge with the human across, and a gap within 30 px of its estimate (the estimate is conservative: Episode 13 Don / Splinter cleared 92 px with
                // ~36 px to spare), makes the same bounded tries (3, never at 1 HP). A miss only costs the pit-fall (1 HP, put back on the near side).
                if (leaderAcross && w > cap2 && _gpTries < 3 && w <= cap2 + 30f) { if (now - _gpWaitSeen > 3f) _gpWaitSince = now; _gpWaitSeen = now; }
                bool marginal = w > cap2 && leaderAcross && _gpTries < 3 && (w <= cap2 + 14f || (w <= cap2 + 30f && now - _gpWaitSince >= 8f && SafeHpP(p) >= 2));
                if (!marginal && w > cap2 && leaderAcross && now < _wdForceUntil && w <= cap2 + 30f && SafeHpP(p) >= 2) marginal = true;   // M4.8: the stall watchdog asked for a real try
                if ((w <= cap2 || marginal) && now >= _gpNextTry && !(_surv && SurvHoldEdge(leaderAcross, now)))
                {
                    if (st > 12f) { return changed; }   // walk up to the edge first
                    bool dbl = PitMap.UseDouble(w, cap1, p.CanDoubleJump, _gpForceDbl);   // M4.8: the single-jump estimate is optimistic: double from 75 % of it, and always after a miss
                    string why = "pit starts " + (int)st + " px ahead, " + (int)w + " px wide (single reaches " + (int)cap1 + ", double " + (int)cap2 + "), goal " + V(dest) + " is across" + (marginal ? ", marginal: the human got over it" : "");
                    DoJump(p, now, new Vector2(sx, 0f), false, "gap-jump", why, 0.9f);
                    _jumpDirUntil = now + 1.9f;
                    if (dbl) { _dblAt = now + (p.CanDoubleJump ? 0.55f : 0.30f); _dblStartZ = p.Position.Z; _dblWatch = false; }
                    if (now - _gpTriesAt > 25f) _gpTries = 0;
                    _gpTries++; _gpTriesAt = now;
                    _gp = Gp.Air; _gpAt = now; _gpDir = sx; _gpFrom = me; _gpEdgeX = me.X + sx * st; _gpFarX = _gpEdgeX + sx * w; _gpW = w; _gpType = dbl ? 2 : 1; _gpWhy = why;
                    _ctlOk = true;
                    return true;
                }
                if (w > cap2 || _gpTries >= 3)
                {
                    if (now - _gpWaitLogAt > 10f)
                    {
                        _gpWaitLogAt = now;
                        BotLog.Evt(BotIndex, "gap-wait", "pit " + (int)w + " px wide is more than I clear (" + (int)cap2 + " with a double jump); waiting at the edge at " + V(me) + (leaderAcross ? " (the human is across, " + _gpTries + " tries used)" : " for the human to cross"), 0f);
                    }
                }
            }

            // not crossing: stop at the edge (12 px before the first pit tile)
            if (st <= 12f)
            {
                _move.X = 0f; changed = true;
                MarkPitBlock(now, me, sx);
                SetIntent("waiting at a pit edge");
                PitNote(now, "stopped at " + V(me) + ", pit " + (int)st + " px ahead, " + (finite ? (int)w + " px wide" : "open ended") + ", " + (chasing ? "chasing " + Name(_target) : (hasDest ? "goal " + V(dest) : "no goal")));
                if (chasing && hasDest && !(_target is StatueOfTyranny) && (_target.Position2D.X - me.X) * sx > st - 4f)   // M4.11: never give up on the Statue because of a pit
                {
                    BotLog.Evt(BotIndex, "pit-skip-target", Name(_target) + " is across the pit at " + V(_target.Position2D) + ": not going after it", 0f);
                    _black[_target] = now + (_surv ? 3f : 8f); _target = null; _tgtSwings = 0; _scanT = 0f;
                }
            }
            return changed;
        }

        // ------------------------------------------------------------ terrain + stuck handling (runs after the decisions, may add a hop)
        private void ClearStuckRef(Vector2 me, float now) { _stuckRefPos = me; _stuckRefT = now; _stuckSince = -1f; }

        private void DoJump(Player p, float now, Vector2 dir, bool dbl, string move, string why, float cd)
        {
            Tap(A_JUMP); _jumpDir = dir; _jumpDirUntil = now + (dbl ? 1.6f : 0.8f); _ledgeCd = cd; _jumpCd = Math.Max(_jumpCd, 0.5f);
            if (dbl) { _dblAt = now + 0.27f; _dblStartZ = p.Position.Z; _dblWatch = false; }
            BotLog.Move(BotIndex, move, why + (dbl ? " (will double jump)" : ""));
            Expect(move, 0.5f, "StateJump");
        }

        private void GiveUp(Vector2 me, float now, string why)
        {
            if (now - _lastGiveUp > 40f) _giveUps = 0;
            _giveUps++; _lastGiveUp = now;
            float wait = Math.Min(20f, 4f + 4f * _giveUps);
            if (_surv) wait = Math.Min(wait, 4f);   // 0.4.14 Survival: no 8-20 s waits, back to the human
            // M4.5: standing right next to the human on the same level: a long park gains nothing (the 0.4.4 Don / Splinter waits of 20 s with the human 15 px away)
            if (_leader != null && _leader.Active && Vector2.Distance(_leader.Position2D, me) < 90f && Math.Abs(_leader.Position.Z - ActivePlayer.Position.Z) < 8f) wait = Math.Min(wait, 4f);
            // 0.4.15: same-spot parks against a wall: count them and force a Y-slide when the wait ends (ends the 8-20 s loop)
            if ((me - _wallSpot).LengthSquared() < 144f) _wallSpotGives++; else { _wallSpot = me; _wallSpotGives = 1; }
            if (_lastWallAhead || _wallSpotGives >= 2) _wallForceSlide = true;
            _giveUpUntil = now + wait; _stuckLevel = 0; _stuckSince = -1f; _climbTries = 0;
            BotLog.Evt(BotIndex, "stuck-give-up", why + "; waiting " + (int)wait + "s and fighting what comes (give-up #" + _giveUps + ")", 0f);
            if (_pizzaTarget != null || _pizzaClaimBot == BotIndex) AbortPizza(now, "give-up: " + why);
            if (_target != null && !(_target is Breakable) && _target.Active) { _black[_target] = now + 12f; _target = null; _scanT = 0f; }
            _move = Vector2.Zero;
        }

        private void AfterThink(float dt, float now)
        {
            Player p = ActivePlayer; if (p == null) return;
            Vector2 me = p.Position2D;
            if (_stuckSince >= 0f) BotLog.Add(BotIndex, "stuck-sec", dt);
            if (_detourUntil > now && _move.LengthSquared() > 0.01f) _move.Y = _detourDir;
            // 0.4.15: after a wall give-up (or 2+ parks at one spot), force a pure Y-slide before catch-up resumes
            if (_wallForceSlide && now >= _giveUpUntil && _wallSlideUntil <= now && p.OnGround)
            {
                BeginWallSlide(me, now, "after give-up at " + V(_wallSpot));
                _wallForceSlide = false;
            }
            if (_wallSlideUntil > now && p.OnGround)
            {
                _move = new Vector2(0f, _wallSlideDir);
                float wantX = 0f;
                if (_leader != null && _leader.Active) wantX = Math.Sign(_leader.Position2D.X - me.X);
                else if (_goalSet) wantX = Math.Sign(_goal.X - me.X);
                if (wantX != 0f && AheadWalkable(me, new Vector2(wantX, 0f)))
                {
                    _wallSlideUntil = 0f; ClearStuckRef(me, now); _stuckLevel = 0;
                }
                else
                {
                    ClearStuckRef(me, now);
                    return;
                }
            }
            if (!p.OnGround)
            {
                if (_jumpDirUntil > now) _move = _jumpDir;   // keep drifting toward the ledge / goal in the air
                if (_gp != Gp.Air) PitAirClamp(me);           // M4.6: an ordinary jump does not drift out over a pit (the air control stops the drift within a few px)
                else { _airTapAt = -1f; _bfKickAt = -1f; }    // a gap jump is only about the landing: no air attack on the way
                return;
            }
            bool sprinting = _sn == "StateSprint";
            bool moving = _move.LengthSquared() > 0.01f;
            // M4.1: surf / autoscroll stages: no stuck detector, no ledge hops (the world scrolls past a stationary board; the 0.4.0 detector fired falsely there and the give-up parked bots for 30-57 s). Diagnostics only.
            if (_surfNow) { SurfWatch(p, me, now, moving && (InMoveState || sprinting)); return; }
            if (PitGuard(p, me, now)) { if (_gp == Gp.Air) return; moving = _move.LengthSquared() > 0.01f; if (!moving) { ClearStuckRef(me, now); return; } }   // M4.6: never walk into a pit; jump the gap or wait at the edge
            if (!moving || !(InMoveState || sprinting)) { ClearStuckRef(me, now); return; }
            if (sprinting || _sp != Sp.None) { ClearStuckRef(me, now); return; }   // a jump would turn into a sliding kick; the sprint ends by itself at walls

            // a) ledge in front of us (loading dock, car roof, container): hop up on our own, double jump when it is tall
            Vector2 dir = _move; dir.Normalize();
            float dh;
            if (_ledgeCd <= 0f && HigherAhead(me, dir, out dh))
            {
                bool dbl = dh > 58f;
                DoJump(p, now, _move, dbl, "jump-ledge", "raised terrain ahead +" + (int)dh + " at " + V(me) + " heading " + V(dir * 10f), dbl ? 1.6f : 0.9f);
                return;
            }
            // b) the thing we are chasing / following stands on something higher and we are right next to it
            if (_higherGoal && _ledgeCd <= 0f && (_goalSet || _leader != null))
            {
                Vector2 g = _goalSet ? _goal : _leader.Position2D;
                Vector2 gd = g - me;
                if (Math.Abs(gd.X) < 70f && Math.Abs(gd.Y) < 30f)
                {
                    bool dbl = _goalDz > 58f;
                    _climbTries++;
                    DoJump(p, now, new Vector2(Math.Abs(gd.X) > 4f ? Math.Sign(gd.X) : 0f, Math.Abs(gd.Y) > 6f ? Math.Sign(gd.Y) * 0.5f : 0f), dbl, "jump-higher",
                        "goal is " + (int)_goalDz + " higher, dx " + (int)gd.X + " dy " + (int)gd.Y + ", try " + _climbTries, dbl ? 1.8f : 1.2f);
                    if (_climbTries >= 6 && _target == null) GiveUp(me, now, "climbed " + _climbTries + " times without getting up");
                    return;
                }
            }
            // c) stuck detector: we are pushing but not getting anywhere. Escalate: hop, go around, double hop, other way around, then give up and wait.
            if (_goalSet && Math.Abs(_goal.X - me.X) <= _goalTolX && Math.Abs(_goal.Y - me.Y) <= 8f) { ClearStuckRef(me, now); return; }   // arrived
            if ((me - _stuckRefPos).Length() > 5f) { ClearStuckRef(me, now); if (now > _stuckClearAt) _stuckLevel = 0; return; }
            if (now - _stuckRefT < 0.6f) return;
            if (_stuckSince < 0f) _stuckSince = now - 0.6f;
            _stuckRefT = now; _stuckClearAt = now + 3f; _stuckLevel++;
            float gz = 0f, hz = 0f;
            try { var pf = Scene2d.Active?.Playfield; if (pf != null) { gz = pf.GetHeightAtPos(me).Height; hz = pf.GetHeightAtPos(me + dir * 14f).Height - gz; } } catch { }
            BotLog.Evt(BotIndex, "stuck", "level " + _stuckLevel + " at " + V(me) + " z " + (int)p.Position.Z + " ground " + (int)gz + " ahead +" + (int)hz + " pushing " + V(_move * 10f) + " state " + _sn + " intent '" + Intent + "' goal dz " + (int)_goalDz + " human " + (_leader == null ? "none" : ((int)Vector2.Distance(_leader.Position2D, me)).ToString()) + " | " + Diag(p), 1.0f);
            // 0.4.15: |hz| >= 200 (incl. short.MaxValue 32767) = solid / no tile ahead — not a climbable ledge (HigherAhead only accepts 10..125)
            bool wallAhead = Math.Abs(hz) >= 200f;
            _lastWallAhead = wallAhead;
            if (wallAhead) BotLog.Evt(BotIndex, "wall-ahead", "solid ahead +" + (int)hz + " at " + V(me) + " pushing " + V(_move * 10f) + ", stuck " + _stuckLevel, 1.0f);
            // M4.5: blocked on the way to a prop (crate, gate): the prop is simply out of reach from here (seen in testing: Don and Splinter pushed at the same spot for 50 s, hop / detour / give-up / wait, then re-picked the same crate). Drop it for a good while, no give-up wait.
            // 0.4.15: against a wall, blacklist the Breakable at stuck level 1 (was >= 2) so we do not keep approaching a door behind solid collision.
            if (_target is Breakable && (_stuckLevel >= 2 || (wallAhead && _stuckLevel >= 1)))
            {
                BotLog.Evt(BotIndex, "prop-unreachable", Name(_target) + " blocked at " + V(me) + ", " + (int)Vector2.Distance(_target.Position2D, me) + " px away; ignoring it for 90 s", 0f);
                _black[_target] = now + 90f; _target = null; _tgtSwings = 0; _scanT = 0f;
                _stuckLevel = 0; ClearStuckRef(me, now); _stuckClearAt = now;
                if (wallAhead) BeginWallSlide(me, now, "prop behind wall");
                return;
            }
            if (wallAhead)
            {
                // 0.4.17: pizza MoveTo into a desk/stanchion was the "up/down walls" thrash — drop claim before sliding
                if (_pizzaTarget != null || _pizzaClaimBot == BotIndex)
                    AbortPizza(now, "wall-ahead at " + V(me));
                // no hop into the wall — long pure Y-slide, try the other side next time
                BeginWallSlide(me, now, "stuck level " + _stuckLevel);
                if (_stuckLevel >= 5) GiveUp(me, now, "stuck " + _stuckLevel + " times at wall " + V(me) + " heading " + V(dir * 10f));
                return;
            }
            switch (_stuckLevel)
            {
                case 1:
                    DoJump(p, now, _move, _goalDz > 58f || hz > 58f, "jump-stuck", "blocked, hopping toward " + V(_move * 10f), 0.8f);
                    break;
                case 2:
                    _detourDir = (Rng.Next(2) == 0 ? 1f : -1f); _detourUntil = now + 1.3f; _move.Y = _detourDir;
                    BotLog.Move(BotIndex, "detour-stuck", "blocked, going around (" + (_detourDir > 0 ? "down" : "up") + ")");
                    break;
                case 3:
                    DoJump(p, now, new Vector2(_move.X, _move.Y == 0f ? (Rng.Next(2) == 0 ? 0.6f : -0.6f) : _move.Y), true, "jump-stuck", "still blocked, diagonal double hop", 1.4f);
                    break;
                case 4:
                    _detourDir = -_detourDir; _detourUntil = now + 1.6f; _move.Y = _detourDir;
                    BotLog.Move(BotIndex, "detour-stuck", "still blocked, going around the other way");
                    break;
                default:
                    GiveUp(me, now, "stuck " + _stuckLevel + " times at " + V(me) + " heading " + V(dir * 10f));
                    break;
            }
        }

        // ---- 0.4.15 wall-ahead helpers (solid / short.MaxValue height) + heavy-vs-guard ----
        private static bool AheadWalkable(Vector2 me, Vector2 dir)
        {
            try
            {
                var pf = Scene2d.Active?.Playfield; if (pf == null) return true;
                float cur = pf.GetHeightAtPos(me).Height;
                float ahead = pf.GetHeightAtPos(me + dir * 14f).Height - cur;
                return Math.Abs(ahead) < 200f;
            }
            catch { return true; }
        }
        private void BeginWallSlide(Vector2 me, float now, string why)
        {
            _wallSlideDir = -_wallSlideDir;   // alternate up / down each call
            if (_leader != null && _leader.Active)
            {
                float dy = _leader.Position2D.Y - me.Y;
                if (Math.Abs(dy) > 6f) _wallSlideDir = Math.Sign(dy);
            }
            _wallSlideUntil = now + 1.8f;
            _detourDir = _wallSlideDir; _detourUntil = _wallSlideUntil;
            _move = new Vector2(0f, _wallSlideDir);
            ClearStuckRef(me, now);
            BotLog.Move(BotIndex, "sidestep-stuck", "wall-ahead slide " + (_wallSlideDir > 0 ? "down" : "up") + " 1.8s (" + why + ")");
            SetIntent("wall slide");
        }
        /// <summary>Target is blocking / guarding (state name has Block) or holding an Enforcer / Zorax shield.</summary>
        private static bool TargetGuarding(NonPlayerCharacter n)
        {
            if (n == null || !n.Active) return false;
            try
            {
                string sn = StateNameOf(n);
                if (sn.IndexOf("Block", StringComparison.Ordinal) >= 0) return true;
                if (n is TriceratonEnforcer te && te.IsShieldActive) return true;
                if (n is Zorax zx && zx.IsShieldActive) return true;
            }
            catch { }
            return false;
        }
        /// <summary>Gnarly always; Okay half the time; Chill always (a basic answer to shields, not an advanced trick).</summary>
        private static bool GuardHeavyOk()
        {
            int d = Diff;
            if (d >= Config.DiffHard) return true;
            if (d <= Config.DiffEasy) return true;
            return AdvOk(0.5);
        }
        /// <summary>No heavy near pits, while reviving, or when a boss attack is about to land on us.</summary>
        private bool HeavySwingBlocked(Player p, Vector2 me, float now, Vector2 d)
        {
            if (IsHolding(A_REVIVE)) return true;
            float sx = Math.Abs(d.X) > 4f ? Math.Sign(d.X) : (p.HorizontalValue >= 0 ? 1f : -1f);
            if (PitNear(p, sx, 90f)) return true;
            if (_incomingEnemy != null && _incomingDist < 140f) return true;   // any attack aimed at us (boss or not): dodge first
            return false;
        }

        /// <summary>Is the ground just ahead (12 and 24 px) clearly higher than where we stand, but low enough to jump (or double jump) onto?</summary>
        private bool HigherAhead(Vector2 me, Vector2 dir, out float dh)
        {
            dh = 0f;
            try
            {
                var pf = Scene2d.Active?.Playfield; if (pf == null) return false;
                float cur = pf.GetHeightAtPos(me).Height;
                float best = 0f;
                for (int i = 1; i <= 2; i++) best = Math.Max(best, pf.GetHeightAtPos(me + dir * (12f * i)).Height - cur);
                dh = best;
                return best >= 10f && best <= 125f;
            }
            catch { return false; }
        }

        private void MoveTo(Vector2 goal, float tolX, float tolY)
        {
            Vector2 d = goal - ActivePlayer.Position2D;
            _move.X = Math.Abs(d.X) > tolX ? Math.Sign(d.X) : 0f;
            _move.Y = Math.Abs(d.Y) > tolY ? Math.Sign(d.Y) : 0f;
            _goalSet = true; _goal = goal; _goalTolX = tolX + 2f;
        }

        // ------------------------------------------------------------ scanning
        private readonly List<Vector2> _enemyPos = new List<Vector2>();
        private void Scan(Scene2d scene, Player p, Vector2 me, Player down, float now, bool comboActive)
        {
            _nearCount = 0; _nearestEnemyDist = 9999f; _threatNearMe = 0; _threatNearAlly = 0; _skippedNoHurt = 0; _superRange = 0; _superFacing = 0; _rangedThreat = false; _attackAtAlly = 0;
            float face = p.HorizontalValue >= 0 ? 1f : -1f; _superSide = face;
            NonPlayerCharacter best = null; float bestCost = float.MaxValue, stickyCost = float.MaxValue; bool bestRanged = false;
            Vector2 lead = _leader != null ? _leader.Position2D : me;
            Vector2 downPos = down != null ? down.Position2D : Vector2.Zero;
            bool tenLive = BotScene.LiveTenderizer(scene, now) != null;   // 0.4.16: Tenderizer-only targeting rules
            int jetClaims = tenLive ? JetpackClaims() : 0;
            if (_surv) SurvScanReset();
            _enemyPos.Clear();
            _leftNear = _rightNear = _frontClose = _behindClose = _incomingN = 0; _incomingEnemy = null; _behindEnemy = null; _behindCand = null; _projEnemy = null; _incomingDist = 9999f; _incomingBoss = false;
            float behindBest = 9999f;
            // real enemies only (never props / neutral NPCs); bikes are handled by RunBiker; the nearest-enemy / threat numbers include ones we cannot hurt right now
            foreach (Enemy n in BotScene.Enemies(scene, now))
            {
                if (n == null || !n.Active || !n.CanBeTargeted || n is FootMotorcycle) continue;
                Vector2 d = n.Position2D - me;
                float dist = d.Length();
                _enemyPos.Add(n.Position2D);
                ObserveClose(n, p, d, face, now, ref behindBest);
                if (n is Shredder) ObserveShredder(n, p, d, face, now);   // M4.10: Thunder / Legendary Savate / Summon (Shredder fight only)
                if (dist < _nearestEnemyDist) _nearestEnemyDist = dist;
                if (dist < 90f) _nearCount++;
                if (Math.Sqrt(d.X * d.X + 4f * d.Y * d.Y) < 90f) _threatNearMe++;
                bool nearAlly = false;
                if (down != null)
                {
                    Vector2 da = n.Position2D - downPos;
                    if (Math.Sqrt(da.X * da.X + 4f * da.Y * da.Y) < 130f) { _threatNearAlly++; nearAlly = true; }
                    if (Math.Abs(da.X) <= 95f && Math.Abs(da.Y) <= 24f && n.OnGround && AttackingNow(n)) _attackAtAlly++;   // M4.1: an attack mid-swing right at the downed player
                }
                if (_surv && down != null) SurvNearDown(n, downPos, now);   // 0.4.14 Survival: crowd / aim count at the downed player
                bool hurtable = Hurtable(n);
                if (hurtable && n is Shredder && BossScene.InCounter(n)) hurtable = false;   // M4.10: never hit a Shredder in his counter stance (it triggers the armored thunder attack)
                bool boss = n is Boss && n.IsVisibleOnScreen;
                float sdx = Math.Abs(d.X), sdy = Math.Abs(d.Y);
                if (boss) { float gx, gy; if (BossReach(n, me, out gx, out gy)) { sdx = Math.Min(sdx, gx + 14f); sdy = Math.Min(sdy, gy); } }   // the boss's real hurtbox, not its centre
                if (hurtable && sdx <= 80f && sdy <= 26f && Math.Abs(n.Position.Z - p.Position.Z) <= 26f)
                {
                    _superRange++; float side = d.X > 0f ? 1f : (d.X < 0f ? -1f : face);
                    if (side == face) _superFacing++; else _superSide = side;
                }
                bool ranged = IsRangedAttacker(n, now);
                if (ranged && dist < 480f) _rangedThreat = true;
                if (ranged) NoteShooter(n, d, now);
                if (!hurtable || IsBlack(n, now)) { _skippedNoHurt++; continue; }
                // 0.4.16 Tenderizer-only: jetpack / ranged peel only if that add is on us or very close; at most one bot on jetpackers
                if (tenLive && ranged && !boss)
                {
                    bool aimed = AttackingNow(n) && dist < 140f;
                    bool close = dist < 90f;
                    bool isJet = n.GetType().Name.IndexOf("Jetpack", StringComparison.Ordinal) >= 0;
                    if (isJet && jetClaims >= 1 && !ReferenceEquals(_target, n)) continue;
                    if (!aimed && !close)
                    {
                        if (_target is Tenderizer) continue;   // stay on the boss
                        // still allow as a weak candidate later only if no boss pick; strip the ranged bonus below
                    }
                    else { /* peel allowed */ }
                }
                if (!boss && !ranged && !nearAlly && Math.Abs(d.X) + Math.Abs(d.Y) > AggEngageRange()) continue;
                if (!boss && ranged && Math.Abs(d.X) + Math.Abs(d.Y) > 560f) continue;
                if (_surv && !boss && ranged && !nearAlly && SurvFarFromHuman(n) && SurvRangedCrowded(n, now)) continue;   // 0.4.14 Survival: at most 2 bots chase ranged attackers away from the human
                if (!boss && _leader != null && !nearAlly && !ranged && Vector2.Distance(n.Position2D, lead) > AggEngageRange() * 1.5f) continue;
                float cost = Math.Abs(d.X) + 2.5f * Math.Abs(d.Y);
                if (nearAlly) cost -= 60f;
                if (ranged)
                {
                    bool tenPeel = tenLive && !(AttackingNow(n) && dist < 140f) && dist >= 90f;
                    if (!tenPeel) cost -= (_surv && SurvRangedCrowded(n, now)) ? 0f : 160f;
                    else cost += 80f;   // 0.4.16: with Tenderizer up, do not prefer distant jetpackers
                }
                else if (!boss && n != _target) cost += ((_surv && (n is FootBaton || n is StoneWarriorGrenadier)) ? 140f : 45f) * ClaimsOn(n);   // M4: a lower-numbered bot (or one mid-combo) already has this enemy: prefer another when it costs about the same. M4.1: never counted against our own current target (it split up 0.3.1 combos), never against a boss
                if (boss) cost = -1000f + 0.1f * Math.Abs(d.X);   // M4.1: a boss on screen is always the top priority target, for every bot
                if (boss && n is StatueOfTyranny && !StatueReachable(n, me)) cost = Math.Abs(d.X) + 2.5f * Math.Abs(d.Y) + 250f;   // M4.11: the Statue sits across a pit: Foot Clan in reach come first, it stays a candidate and wins again as soon as it is in reach
                if (boss && n is Shredder && ((Shredder)n).IsCloneVersion && BossScene.RealReachable)
                {
                    // M4.10: the real Shredder can be hit: he is the target, the shadow clones only when one is actually attacking us
                    bool cloneAtMe = sdx <= 110f && sdy <= 30f && AttackingNow(n);
                    cost = cloneAtMe ? -700f + 0.1f * Math.Abs(d.X) : Math.Abs(d.X) + 2.5f * Math.Abs(d.Y) + 400f;
                }
                if (_surv && n is FootBaton && StateNameOf(n).IndexOf("Block", StringComparison.Ordinal) >= 0) cost += 90f;   // 0.4.14 Survival: a blocking FootBaton hurts the attacker
                if (n == _target) stickyCost = cost;
                if (cost < bestCost) { bestCost = cost; best = n; bestRanged = ranged; }
            }
            _behindEnemy = behindBest < 9000f ? _behindCand : null;
            // breakable props: only ones that can really be broken; only when no enemy is around, except barrels with enemies next to them
            Breakable prop = null; float propCost = float.MaxValue; bool propIsBarrel = false;
            bool enemiesAround = best != null || _nearestEnemyDist < 220f;
            foreach (Breakable b in BotScene.Props(scene, now))
            {
                if (b == null || !b.Active || !b.CanBeTargeted || b.IsDead || IsBlack(b, now)) continue;
                if (ClaimsOn(b) > 0) continue;   // M4: another bot is already on this prop
                float br; bool expl = IsExplosive(b, out br);
                Vector2 d = b.Position2D - me;
                if (BotLog.On && _seenProps.Count < 40 && !_seenProps.ContainsKey(b) && Math.Abs(d.X) + Math.Abs(d.Y) < 400f)
                {
                    _seenProps[b] = true;
                    BotLog.Evt(BotIndex, "prop-seen", Name(b) + " '" + b.Name + "' hurtable " + Hurtable(b) + " bump " + b.BumpEnabled + " explosive " + expl + (expl ? " r" + (int)br : "") + " dist " + (int)d.Length() + " z " + (int)(b.Position.Z - p.Position.Z), 0f);
                }
                if (!Hurtable(b) || b.BumpEnabled) continue;
                if (Math.Abs(b.Position.Z - p.Position.Z) > 30f) continue;
                if (Math.Abs(d.X) + Math.Abs(d.Y) > (enemiesAround ? 150f : 240f)) continue;
                if (_leader != null && Vector2.Distance(b.Position2D, lead) > AggEngageRange() * 1.2f) continue;
                if (expl)
                {
                    if (OtherPlayerNear(b.Position2D, br + 28f, p)) continue;   // never with the human or another bot next to it
                    int near = 0; foreach (Vector2 ep in _enemyPos) if (Vector2.Distance(ep, b.Position2D) < br) near++;
                    if (enemiesAround && near == 0) continue;
                    if (near > 0) { /* use it as a weapon */ }
                }
                else if (enemiesAround) continue;
                float cost = Math.Abs(d.X) + 2.5f * Math.Abs(d.Y);
                if (cost < propCost) { propCost = cost; prop = b; propIsBarrel = expl; }
            }
            if (prop == null && enemiesAround) { /* nothing to log: props simply skipped */ }
            if (prop != null && propIsBarrel && enemiesAround && !(best is Boss) && (best == null || bestRanged == false))
            { best = prop; bestCost = propCost; stickyCost = prop == _target ? propCost : float.MaxValue; }
            else if (best == null && prop != null && !enemiesAround && down == null) { best = prop; bestCost = propCost; stickyCost = prop == _target ? propCost : float.MaxValue; }

            if (BossScene.ShadowLink && ShadowScan(p, me, now)) return;   // M4.10: Super Shredder's Shadow Link: one distinct figure per bot
            // committed to a combo: stay on this enemy (also while it is knocked back) until the combo counter runs out
            if (_target != null && comboActive && _target.Active && !_target.IsInStateKO && !IsBlack(_target, now) && Vector2.Distance(_target.Position2D, me) < 220f && !BossScene.InCounter(_target))
            { _targetUntil = now + 1.2f; return; }
            if (_target != null && !(_target is Breakable) && stickyCost < float.MaxValue && stickyCost <= bestCost + 40f && now < _targetUntil) return;   // keep the current target for a bit
            if (_target is Breakable && best == _target && now < _targetUntil) return;
            if (tenLive && bestRanged && best != null && !(_target is Tenderizer))
            {
                Tenderizer ten = BotScene.LiveTenderizer(scene, now);
                if (ten != null && ten.Active && Hurtable(ten) && !IsBlack(ten, now))
                {
                    Enemy be = best as Enemy;
                    bool allowPeel = be != null && ((AttackingNow(be) && Vector2.Distance(best.Position2D, me) < 140f) || Vector2.Distance(best.Position2D, me) < 90f);
                    if (!allowPeel) { best = ten; bestCost = -1000f; bestRanged = false; }
                }
            }
            if (best != _target)
            {
                BotLog.Evt(BotIndex, "target-switch", Name(_target) + " -> " + Name(best) + (best != null ? " dist " + (int)(best.Position2D - me).Length() : "") + (down != null ? " (ally down)" : ""), 1.5f);
                if (bestRanged && best != null) BotLog.Evt(BotIndex, "threat-priority", "ranged attacker " + Name(best) + " first, dist " + (int)(best.Position2D - me).Length() + ", hurt " + (now - _hurtAt).ToString("0.0") + "s ago", 2f); _wantGrab = Rng.NextDouble() < DiffGrabP(0.25); _grabTimeout = 0f; _tgtSwings = 0; _spPlan = Rng.NextDouble() < 0.3; _climbTries = 0;
                _tgtHp = best != null ? SafeHp(best) : 0; _propSince = now;
            }
            _target = best; _targetUntil = now + 1.2f;
        }


        // ------------------------------------------------------------ M4: situational dodges
        private void ObserveClose(Enemy n, Player p, Vector2 d, float face, float now, ref float behindBest)
        {
            float adx = Math.Abs(d.X), ady = Math.Abs(d.Y);
            if (adx > 115f || ady > 22f || Math.Abs(n.Position.Z - p.Position.Z) > 26f) return;
            float side = d.X >= 0f ? 1f : -1f;
            if (side > 0f) _rightNear++; else _leftNear++;
            bool inFront = side == face;
            if (adx <= 80f)
            {
                if (inFront) _frontClose++;
                else if (adx <= 55f) { _behindClose++; if (adx < behindBest) { behindBest = adx; _behindCand = n; } }
            }
            // an attack coming at us: the enemy is in an attack state, facing us, within reach, on the ground
            if (adx <= 95f && ady <= 18f && n.OnGround)
            {
                string sn = StateNameOf(n);
                if (sn.IndexOf("Attack", StringComparison.Ordinal) >= 0 && sn.IndexOf("Hurt", StringComparison.Ordinal) < 0 && sn.IndexOf("Wait", StringComparison.Ordinal) < 0
                    && sn.IndexOf("Reaction", StringComparison.Ordinal) < 0 && sn.IndexOf("Stun", StringComparison.Ordinal) < 0 && sn.IndexOf("Block", StringComparison.Ordinal) < 0)
                {
                    bool facingMe = side > 0f ? n.HorizontalValue < 0 : n.HorizontalValue > 0;
                    if (facingMe)
                    {
                        _incomingN++;
                        if (adx < _incomingDist)
                        {
                            _incomingDist = adx; _incomingSide = side; _incomingEnemy = n; _incomingBoss = n.GetType().Name.IndexOf("Boss", StringComparison.Ordinal) >= 0;
                            _incomingWhy = Name(n) + " in '" + sn + "'";
                        }
                    }
                }
            }
        }

        /// <summary>A ranged enemy that is shooting / throwing along our lane at us.</summary>
        private void NoteShooter(Enemy n, Vector2 d, float now)
        {
            float adx = Math.Abs(d.X), ady = Math.Abs(d.Y);
            if (adx < 50f || adx > 340f || ady > 14f) return;
            string sn = StateNameOf(n);
            if (sn.IndexOf("Attack", StringComparison.Ordinal) < 0 && sn.IndexOf("Shoot", StringComparison.Ordinal) < 0 && sn.IndexOf("Throw", StringComparison.Ordinal) < 0 && sn.IndexOf("Aim", StringComparison.Ordinal) < 0) return;
            if (sn.IndexOf("Hurt", StringComparison.Ordinal) >= 0 || sn.IndexOf("Thrown", StringComparison.Ordinal) >= 0) return;
            bool facingMe = d.X > 0f ? n.HorizontalValue < 0 : n.HorizontalValue > 0;
            if (!facingMe) return;
            if (_projEnemy == null || adx < Math.Abs(_projEnemy.Position2D.X - ActivePlayer.Position2D.X)) { _projEnemy = n; _projAt = now; _projDy = d.Y; _projSide = d.X >= 0f ? 1f : -1f; }
        }

        private bool TryEvade(Player p, Vector2 me, float now)
        {
            if (!p.OnGround || !InMoveState) return false;
            if (now < _sideUntil) { _move = new Vector2(0f, _sideDir); _ctlOk = false; SetIntent("sidestep a shot"); return true; }
            if (_superPendUntil > now || now < _fleeUntil || _sp != Sp.None || _tapT > 0f) return false;
            if (_comboHits > 0 && now - _lastHitAt < 0.5f) return false;
            float face = p.HorizontalValue >= 0 ? 1f : -1f;
            bool recentlyHurt = now - _hurtAt < 3f;

            // 1. surrounded (enemies close on both sides, three or more): flip out toward the emptier side, often with a kick on the way
            if (_surroundCd <= 0f && _leftNear >= 1 && _rightNear >= 1 && _leftNear + _rightNear >= 3)
            {
                _surroundCd = 2f;
                if (Rng.NextDouble() < DiffDodgeP(recentlyHurt ? 0.7 : 0.4, false))
                {
                    float open = _leftNear < _rightNear ? -1f : (_rightNear < _leftNear ? 1f : (_leader != null && Math.Abs(_leader.Position2D.X - me.X) > 4f ? Math.Sign(_leader.Position2D.X - me.X) : -face));
                    _surroundCd = 7f; _evadeCd = 3f;
                    if (DoDodge("escape-surround", open, face, open != face && RollAdv(0.6), "surrounded L" + _leftNear + " R" + _rightNear + ", " + _nearCount + " within 90", now)) return true;
                }
            }

            // 2. a melee attack aimed at us: dodge (roll chance per incoming attack, higher for bosses / when already hurt)
            if (_evadeCd <= 0f && _incomingN > 0 && _incomingEnemy != null && _incomingEnemy.Active)
            {
                if (!ReferenceEquals(_evadeRollFor, _incomingEnemy) || now - _evadeRollAt > 1.5f)
                {
                    _evadeRollFor = _incomingEnemy; _evadeRollAt = now;
                    double pr = _incomingBoss ? 0.6 : (recentlyHurt ? 0.5 : 0.28);
                    if (_incomingN >= 2) pr += 0.15;
                    if (_incomingEnemy is Shredder) pr = 0.85;   // M4.10: Shredder only (real and clones): his thunder / savate / summon are the ones that hurt
                    _evadeDo = Rng.NextDouble() < DiffDodgeP(pr, _incomingBoss || _incomingEnemy is Shredder);
                    BotLog.Note(BotIndex, "evade-seen:" + _incomingWhy, "attack aimed at us: " + _incomingWhy + " dx " + (int)(_incomingSide * _incomingDist) + ", dodge " + (_evadeDo ? "yes" : "no (roll)"), 15f);
                }
                if (_evadeDo && (Diff >= Config.DiffHard || now - _evadeRollAt >= DiffEvadeDelay))   // M4.12: Easy / Normal react a little later
                {
                    bool shr = _incomingEnemy is Shredder;
                    _evadeDo = false; _evadeCd = shr ? 2.5f : 5f;   // M4.10: Shredder only: shorter cooldown
                    float away = -_incomingSide;
                    if ((away > 0f ? _rightNear : _leftNear) > (shr ? 1 : 0)) { _evadeCd = 1f; return false; }   // another enemy is on the way out: not a dodge
                    bool kick = away != face && _incomingSide == face && RollAdv(0.5);
                    if (DoDodge("evade-attack", away, face, kick, "dodge " + _incomingWhy + " at dx " + (int)(_incomingSide * _incomingDist), now)) return true;
                }
            }

            // 3. a shot coming along our lane: step out of the lane for a moment
            if (_evadeCd <= 0f && _projEnemy != null && _projEnemy.Active && now - _projAt < 0.25f && _nearestEnemyDist > 60f)
            {
                if (!ReferenceEquals(_evadeRollFor, _projEnemy) || now - _evadeRollAt > 1.5f)
                {
                    _evadeRollFor = _projEnemy; _evadeRollAt = now; _evadeDo = Rng.NextDouble() < DiffDodgeP(0.35, false);
                }
                if (_evadeDo)
                {
                    _evadeDo = false; _evadeCd = 4f; _sideUntil = now + 0.35f; _sideDir = _projDy > 0f ? -1f : 1f;
                    BotLog.Move(BotIndex, "evade-sidestep", Name(_projEnemy) + " shooting along our lane, dx " + (int)(_projSide * Math.Abs(_projEnemy.Position2D.X - me.X)));
                    _move = new Vector2(0f, _sideDir); _ctlOk = false; SetIntent("sidestep a shot");
                    return true;
                }
            }

            // 4. an enemy right behind us and nobody in front: the game's own back kick (attack press with an enemy behind and none ahead)
            if (_backKickCd <= 0f && _behindEnemy != null && _frontClose == 0 && _behindClose >= 1 && _behindEnemy.Active && Hurtable(_behindEnemy))
            {
                _backKickCd = 3.5f;
                bool autoBack = true; try { autoBack = SettingsSaveGame.StaticAutoBackAttack; } catch { }
                if (autoBack && Rng.NextDouble() < 0.6)
                {
                    Tap(A_ATTACK); _tapT = 0.3f; SetIntent("back kick");
                    BotLog.Move(BotIndex, "back-kick", Name(_behindEnemy) + " behind us at dx " + (int)(_behindEnemy.Position2D.X - me.X) + ", none in front");
                    Expect("back-kick", 0.5f, "BackKick");
                    return true;
                }
            }
            return false;
        }

        /// <summary>Backflip away (no push = the flip goes backwards from the facing) or round-off forward (push along the facing): the game's DoBackflipOrRoundoff decides by that push.</summary>
        private bool DoDodge(string move, float open, float face, bool kick, string why, float now)
        {
            // M4.6: the flip / round-off carries ~110-150 px: never toward a pit. Switch to the other direction when that is clear, else no dodge.
            Player me0 = ActivePlayer;
            if (me0 != null)
            {
                bool fwd = open == face;
                if (PitNear(me0, fwd ? face : -face, 150f))
                {
                    if (!PitNear(me0, fwd ? -face : face, 150f)) { open = fwd ? -face : face; kick = kick && open != face; BotLog.Note(BotIndex, "pit-dodge", move + ": pit that way, flipping the other way", 3f); }
                    else { BotLog.Note(BotIndex, "pit-dodge", move + ": pits both ways, no dodge", 3f); return false; }
                }
            }
            Tap(A_BACKFLIP);
            _move = open == face ? new Vector2(face, 0f) : Vector2.Zero;
            _evadeAirUntil = now + 0.7f;
            if (kick) _bfKickAt = now + 0.22f;
            _ctlOk = false; SetIntent(move);
            BotLog.Move(BotIndex, move, why + (open == face ? ", round-off forward" : ", backflip") + (kick ? ", then a kick" : ""));
            Expect(move, 0.5f, "Backflip", "RoundOff", "Roll");
            return true;
        }

        // ------------------------------------------------------------ M4: multi-bot coordination
        /// <summary>Other bots (and players) the shared claim / spacing rules look at.</summary>
        private int ClaimsOn(NonPlayerCharacter n)
        {
            if (n == null) return 0;
            int c = 0;
            foreach (BotController o in All)
                if (o != this && o.Live && ReferenceEquals(o._target, n) && (o.BotIndex < BotIndex || o._comboHits > 0)) c++;
            return Math.Min(c, 2);
        }
        /// <summary>0.4.16 Tenderizer-only: how many other bots are already on a FootJetpack.</summary>
        private static int JetpackClaims()
        {
            int c = 0;
            foreach (BotController o in All)
            {
                if (o == null || !o.Live || o._target == null) continue;
                if (o._target.GetType().Name.IndexOf("Jetpack", StringComparison.Ordinal) >= 0) c++;
            }
            return c;
        }

        /// <summary>Any other player (human or bot, standing or down) within r of a point: barrels are never lit next to anybody.</summary>
        private static bool OtherPlayerNear(Vector2 pos, float r, Player ignore)
        {
            Scene2d sc = Scene2d.Active; if (sc == null) return false;
            foreach (Player o in sc.Players.Values)
                if (o != null && o != ignore && o.Active && Vector2.Distance(o.Position2D, pos) < r) return true;
            return false;
        }

        private struct Lit { public Vector2 Pos; public float R, Until; }
        private static readonly List<Lit> LitList = new List<Lit>(4);
        private static void NoteLit(Vector2 pos, float r, float now)
        {
            LitList.RemoveAll(l => l.Until < now);
            LitList.Add(new Lit { Pos = pos, R = r, Until = now + 3.6f });
        }

        private bool CanReviveNow(Player down)
        {
            Player a = ActivePlayer;
            if (!Live || a == null || !a.Active || ReferenceEquals(a, down) || !a.HasControl || a.IsInStateKO || a.IsInStateHurt || a.IsInStateKnockdown) return false;
            return Vector2.Distance(a.Position2D, down.Position2D) <= 320f;
        }

        private static readonly Dictionary<Player, BotController> ReviverOf = new Dictionary<Player, BotController>();
        /// <summary>Exactly one bot goes for a downed player: the nearest able one (a little stickiness so two bots do not swap), not everybody at once.</summary>
        private bool AmReviver(Player down, Vector2 me)
        {
            if (All.Count <= 1) return true;
            if (ReviverOf.Count > 8) ReviverOf.Clear();
            BotController best = null; float bd = float.MaxValue;
            foreach (BotController c in All)
            {
                if (!ReferenceEquals(c, this) && !c.CanReviveNow(down)) continue;
                if (ReferenceEquals(c, this) && !CanReviveNow(down) && ActivePlayer != null && ActivePlayer.IsInStateKO) continue;
                float d = Vector2.Distance(c.ActivePlayer.Position2D, down.Position2D);
                if (d < bd - 0.5f || (Math.Abs(d - bd) <= 0.5f && best != null && c.BotIndex < best.BotIndex)) { bd = d; best = c; }
            }
            BotController cur;
            if (ReviverOf.TryGetValue(down, out cur) && cur != null && cur != best && cur.CanReviveNow(down) && Vector2.Distance(cur.ActivePlayer.Position2D, down.Position2D) <= bd + 50f) best = cur;
            ReviverOf[down] = best;
            return best == null || ReferenceEquals(best, this);
        }

        /// <summary>Gentle push away from a player standing on top of us (idle only).</summary>
        private Vector2 Separation(Player p, Vector2 me)
        {
            Scene2d sc = Scene2d.Active; if (sc == null) return Vector2.Zero;
            foreach (Player o in sc.Players.Values)
            {
                if (o == null || o == p || !o.Active || o.IsInStateKO) continue;
                Vector2 d = me - o.Position2D;
                if (Math.Abs(d.X) < 22f && Math.Abs(d.Y) < 9f)
                {
                    float x = Math.Abs(d.X) < 1f ? ((BotIndex & 1) == 0 ? 1f : -1f) : Math.Sign(d.X);
                    float y = Math.Abs(d.Y) < 2f ? ((BotIndex & 2) == 0 ? 1f : -1f) : Math.Sign(d.Y);
                    return new Vector2(x, y * 0.6f);
                }
            }
            return Vector2.Zero;
        }

        // ------------------------------------------------------------ M4 / M4.7: sandwich attack (with another bot, and with the human)
        // The game's sandwich (Player.FindSandwichAttackVictim): two players stand on opposite sides of an enemy, each 74..106 px from it in x and within 8 px in y, both on the ground at the
        // enemy's height and facing it, the enemy on screen / on the ground / not knocked down / not tossed; the second player presses attack within 0.06 s of the first one's press (the first
        // press is an ordinary stationary / moving attack). Nothing else is needed: no grab, no stun. M4.7: far more attempts the human is a partner too (the bot stands on the
        // far side and presses together with the human's attack), the others hold off the enemy while a pair sets up, and a flicker of the enemy's state no longer kills the plan.
        private sealed class SandPair
        {
            public BotController A, B; public Enemy E; public float Start, FireAt = -1f, ReadyA = -9f, ReadyB = -9f, SideA, SoftSince = -1f; public bool BTapped;
        }
        private static SandPair _pair;
        private sealed class HumanSand { public BotController Bot; public Enemy E; public Player H; public float Start, ReadyAt = -1f, HSide, SoftSince = -1f; }
        private static HumanSand _hs;
        private float _sandRollT = -9f, _sandHCd, _sandHEvalT = -9f, _sandHRollT = -9f; private object _sandHRollFor; private int _sandJoinAtk = -1;
        private static string _hSn = ""; private static float _hSnSince, _hSnStamp = -1f; private static int _hAtkCount;

        private bool SandFree(float now)
        {
            return Live && ActivePlayer != null && _sandCd <= 0f && InMoveState && ActivePlayer.OnGround && _nearCount <= 1 && _comboHits == 0 && now >= _giveUpUntil && !(_target is Boss);   // M4.7: boss priority
        }

        private static void EndPair(SandPair sp, string why, bool fired, float now)
        {
            if (_pair != sp) return;
            _pair = null;
            float cd = fired ? 10f : (now - sp.Start < 0.6f ? 2.5f : 4f);   // M4.7: was 25 / 12 s
            if (sp.A != null) sp.A._sandCd = cd;
            if (sp.B != null) sp.B._sandCd = cd;
            BotLog.Evt(sp.A != null ? sp.A.BotIndex : 0, fired ? "sandwich-end" : "sandwich-abort", why + " (partner b" + (sp.B != null ? sp.B.BotIndex + 1 : 0) + ", took " + (now - sp.Start).ToString("0.0") + "s)", 0f);
        }

        private static void EndHuman(HumanSand hs, string why, bool fired, float now)
        {
            if (_hs != hs) return;
            _hs = null;
            if (hs.Bot != null) hs.Bot._sandHCd = fired ? 10f : (now - hs.Start < 0.6f ? 2.5f : 5f);
            BotLog.Evt(hs.Bot != null ? hs.Bot.BotIndex : 0, fired ? "sandwich-end" : "sandwich-abort", why + " (partner: the human, took " + (now - hs.Start).ToString("0.0") + "s)", 0f);
        }

        /// <summary>null = the enemy can be sandwiched; else why not. 'soft' = only the "can be hurt / targeted" part failed (a flicker the game itself does not care about): callers wait 0.3 s before giving up.</summary>
        private static string SandWhyNot(Enemy e, out bool soft)
        {
            soft = false;
            try
            {
                if (e == null) return "enemy gone";
                if (!e.Active) return "enemy inactive";
                if (!e.OnGround) return "enemy in the air";
                if (e.IsInStateKnockdown) return "enemy knocked down";
                if (e.IsInStateKO) return "enemy KO";
                string tn = e.GetType().Name;
                if (tn.IndexOf("Boss", StringComparison.Ordinal) >= 0 || tn.IndexOf("Rodney", StringComparison.Ordinal) >= 0) return "enemy is a boss";
                if (!e.CanBeTargeted || !Hurtable(e)) { soft = true; return "enemy not hurtable"; }
                return null;
            }
            catch { return "enemy error"; }
        }

        private bool EnemyOkForSandwich(Enemy e) { bool soft; return SandWhyNot(e, out soft) == null; }

        /// <summary>pos is on the given side (+1 right, -1 left) of the enemy inside the game's trigger zone (x 74..106, |y| &lt;= 8), shrunk by a safety margin.</summary>
        private static bool InSandZone(Vector2 pos, Vector2 ed, float side, float mx, float my)
        {
            float dx = (pos.X - ed.X) * side;
            return dx >= 74f + mx && dx <= 106f - mx && Math.Abs(pos.Y - ed.Y) <= 8f - my;
        }

        private static bool FacesRight(Player q) { try { return q.HorizontalValue >= 0; } catch { return true; } }

        /// <summary>True during the first 0.05 s of the human's ordinary attack (state 6 / 11 = the only ones the game turns into a sandwich).</summary>
        private static bool HumanAttackFresh(Player h, float now)
        {
            if (_hSnStamp != now)
            {
                _hSnStamp = now;
                string sn = StateNameOf(h);
                if (sn != _hSn) { _hSn = sn; _hSnSince = now; if (sn == "StateStationaryAttack" || sn == "StateMovingAttack") _hAtkCount++; }
            }
            return (_hSn == "StateStationaryAttack" || _hSn == "StateMovingAttack") && now - _hSnSince <= 0.05f;
        }

        private Player HumanPartner(Player p)
        {
            Player h = _leader;
            if (h == null || h == p || h.PInfo == null || h.PInfo.Controller is BotController) return null;
            try { if (!h.Active || h.IsDead || h.IsInStateKO || !h.IsVisibleOnScreen) return null; } catch { return null; }
            return h;
        }

        /// <summary>The human just pressed an ordinary attack and we already stand on the far side of an enemy in the zone, facing it: press now (inside the game's 0.06 s window).</summary>
        private bool SandJoinHuman(Player p, Vector2 me, float now, Scene2d scene)
        {
            Player h = HumanPartner(p);
            if (h == null || scene == null || !HumanAttackFresh(h, now) || _sandJoinAtk == _hAtkCount) return false;
            if (!InMoveState || !p.OnGround || _target is Boss || _tapT > 0f) return false;
            Vector2 hp2 = h.Position2D; bool hRight = FacesRight(h), myRight = FacesRight(p);
            foreach (Enemy e in BotScene.Enemies(scene, now))
            {
                bool soft; if (SandWhyNot(e, out soft) != null) continue;
                Vector2 ed = e.Position2D;
                float sideH = hp2.X >= ed.X ? 1f : -1f, sideM = -sideH;
                if (!InSandZone(hp2, ed, sideH, 1f, 0.5f) || (sideH < 0f) != hRight) continue;
                if (!InSandZone(me, ed, sideM, 3f, 1.5f) || (sideM < 0f) != myRight) continue;
                if (Math.Abs(p.Position.Z - e.Position.Z) > 1f || Math.Abs(h.Position.Z - e.Position.Z) > 1f) continue;
                _sandJoinAtk = _hAtkCount; Tap(A_ATTACK); _tapT = 0.3f;
                BotLog.Evt(BotIndex, "sandwich-press", "joining the human's press on " + Name(e) + " (human dx " + (int)Math.Abs(hp2.X - ed.X) + " dy " + (int)(hp2.Y - ed.Y) + ", me dx " + (int)Math.Abs(me.X - ed.X) + " dy " + (int)(me.Y - ed.Y) + ", " + (int)((now - _hSnSince) * 1000f) + " ms after)", 0f);
                BotLog.Move(BotIndex, "sandwich", "joining the human's press on " + Name(e)); Expect("sandwich", 0.8f, "Sandwich");
                HumanSand hs = _hs; if (hs != null && hs.Bot == this) EndHuman(hs, "pressed with the human", true, now);
                return true;
            }
            return false;
        }

        /// <summary>The human is walking up to an enemy: go stand on the far side of it (90 px away, same lane, facing it) and wait a moment; SandJoinHuman presses together with the human's attack.</summary>
        private bool SandHumanSetup(Player p, Vector2 me, float now, Scene2d scene)
        {
            HumanSand hs = _hs;
            if (hs == null)
            {
                if (_sandHCd > 0f || now - _sandHEvalT < 0.25f || _pair != null || !SandFree(now) || _target is Boss || scene == null) return false;
                _sandHEvalT = now;
                Player h = HumanPartner(p);
                if (h == null || !h.OnGround) return false;
                Vector2 hp2 = h.Position2D; bool hRight = FacesRight(h);
                Enemy best = null; float bd = 1e9f;
                foreach (Enemy e in BotScene.Enemies(scene, now))
                {
                    bool soft; if (SandWhyNot(e, out soft) != null || soft) continue;
                    Vector2 ed = e.Position2D; float dh = Vector2.Distance(hp2, ed);
                    if (dh < 70f || dh > 200f || Math.Abs(hp2.Y - ed.Y) > 40f || Math.Abs(h.Position.Z - e.Position.Z) > 1f) continue;
                    float sideH = hp2.X >= ed.X ? 1f : -1f; if ((sideH < 0f) != hRight) continue;   // the human is not facing it
                    if (dh < bd) { bd = dh; best = e; }
                }
                if (best == null) return false;
                if (ReferenceEquals(_sandHRollFor, best) && now - _sandHRollT < 3f) return false;
                _sandHRollFor = best; _sandHRollT = now;
                if (Rng.NextDouble() >= 0.6) return false;
                Vector2 bd2 = best.Position2D; float hs0 = hp2.X >= bd2.X ? 1f : -1f;
                Vector2 spot0 = bd2 + new Vector2(-hs0 * 90f, 0f);
                if (Vector2.Distance(me, spot0) > 320f) return false;
                if (_target != null && !ReferenceEquals(_target, best) && Vector2.Distance(me, _target.Position2D) < 110f) return false;   // busy in a fight
                foreach (BotController c in All) if (c != this && c._pairBusy) return false;
                { float myD = Vector2.Distance(me, spot0); foreach (BotController c in All) if (c != this && c.ActivePlayer != null && c._sandHCd <= 0f && c.SandFree(now) && Vector2.Distance(c.ActivePlayer.Position2D, spot0) < myD - 40f) return false; }   // a bot much closer to that spot takes it
                _hs = hs = new HumanSand { Bot = this, E = best, H = h, Start = now, HSide = hs0 };
                BotLog.Evt(BotIndex, "sandwich-plan", "with the human on " + Name(best) + " (human " + (int)bd + " px from it, me going to the far side)", 0f);
            }
            if (hs.Bot != this) return false;
            string why = null; bool softNow;
            Player hh = HumanPartner(p);
            if (hh == null || hh != hs.H) why = "human gone";
            else if (now - hs.Start > 3.5f) why = "timed out";
            else if (hs.ReadyAt >= 0f && now - hs.ReadyAt > 1.6f) why = "human did not attack";
            else if (_nearCount > 1 || p.IsInStateHurt || _target is Boss) why = "other enemies close / hurt";
            else if ((why = SandWhyNot(hs.E, out softNow)) != null) { if (softNow) { if (hs.SoftSince < 0f) hs.SoftSince = now; if (now - hs.SoftSince < 0.3f) why = null; } }
            else hs.SoftSince = -1f;
            if (why == null)
            {
                Vector2 ed = hs.E.Position2D, hp2 = hs.H.Position2D;
                float sideH = hp2.X >= ed.X ? 1f : -1f;
                if (sideH != hs.HSide || Vector2.Distance(hp2, ed) > 240f) why = "human moved away from the enemy";
            }
            if (why != null) { EndHuman(hs, why, false, now); return false; }
            {
                Vector2 ed = hs.E.Position2D; float side = -hs.HSide;
                Vector2 spot = ed + new Vector2(side * 90f, 0f);
                TargetLabel = "sandwich with the human";
                if (!InSandZone(me, ed, side, 3f, 1.5f)) { hs.ReadyAt = -1f; MoveTo(spot, 8f, 3f); SetIntent("sandwich: go to the far side of the enemy"); return true; }
                bool facing = (side < 0f) == FacesRight(p);
                if (!facing) { _move.X = -side; SetIntent("sandwich: face it"); return true; }
                if (hs.ReadyAt < 0f) hs.ReadyAt = now;
                SetIntent("sandwich: ready, waiting for the human's attack");
                return true;
            }
        }
        private bool _pairBusy { get { SandPair sp = _pair; HumanSand hs = _hs; return (sp != null && (sp.A == this || sp.B == this)) || (hs != null && hs.Bot == this); } }

        private bool RunSandwich(Player p, Vector2 me, float now)
        {
            if (!Config.TeamAttacks) return false;
            if (StatueScene.On && !(_pair != null && (_pair.A == this || _pair.B == this)) && !(_hs != null && _hs.Bot == this)) return false;   // M4.11: no new sandwich plans in the Statue fight (running ones finish)
            Scene2d scene = p.Scene as Scene2d;
            if (SandJoinHuman(p, me, now, scene)) return true;
            if (SandHumanSetup(p, me, now, scene)) return true;
            if (All.Count < 2) return false;
            SandPair sp = _pair;
            if (sp != null && !ReferenceEquals(sp.A, this) && !ReferenceEquals(sp.B, this))
            {
                // M4.7: a pair is setting up on this enemy: keep off it for a moment (they had 3 of 14 plans knocked apart by a third bot's hit), as long as it is safe to stand still
                if (sp.FireAt < 0f && now - sp.Start < 2.0f && ReferenceEquals(_target, sp.E) && _nearCount == 0 && now - _hurtAt > 3f && InMoveState && _comboHits == 0 && Vector2.Distance(me, sp.E.Position2D) > 80f)
                {
                    BotLog.Note(BotIndex, "sandwich-hold", "letting b" + (sp.A.BotIndex + 1) + " and b" + (sp.B.BotIndex + 1) + " set up the sandwich on " + Name(sp.E), 2f);
                    SetIntent("sandwich: let the pair set up"); return true;
                }
                return false;
            }
            if (sp == null)
            {
                // propose: with a free bot on the other side of the enemy we are about to fight
                Enemy e = _target as Enemy;
                if (e == null || !SandFree(now) || (ReferenceEquals(_sandRolledFor, e) && now - _sandRollT < 2f) || !EnemyOkForSandwich(e) || _hs != null) return false;
                _sandRolledFor = e; _sandRollT = now;
                if (Rng.NextDouble() >= 0.85) return false;   // M4.7: was 0.25, and once per enemy for good
                Vector2 ed = e.Position2D;
                float mySide = me.X >= ed.X ? 1f : -1f;
                if (Vector2.Distance(me, ed) > 320f) return false;
                BotController bestC = null; float bestD = 1e9f;
                foreach (BotController c in All)
                {
                    if (c == this || !c.SandFree(now) || c.ActivePlayer == null) continue;
                    Vector2 cp = c.ActivePlayer.Position2D;
                    float cSide = cp.X >= ed.X ? 1f : -1f;
                    if (cSide == mySide || Vector2.Distance(cp, ed) > 320f || Math.Abs(cp.Y - ed.Y) > 70f) continue;
                    if (c._target != null && !ReferenceEquals(c._target, e) && Vector2.Distance(cp, c._target.Position2D) < 110f) continue;   // busy in a fight with something else
                    float dd = Vector2.Distance(cp, ed + new Vector2(cSide * 90f, 0f));
                    if (dd < bestD) { bestD = dd; bestC = c; }
                }
                if (bestC == null) return false;
                _pair = sp = new SandPair { A = this, B = bestC, E = e, Start = now, SideA = mySide };
                BotLog.Evt(BotIndex, "sandwich-plan", "with b" + (bestC.BotIndex + 1) + " on " + Name(e) + ", me " + (mySide > 0 ? "right" : "left") + " of it", 0f);
            }
            bool isA = ReferenceEquals(sp.A, this), isB = ReferenceEquals(sp.B, this);
            if (!isA && !isB) return false;
            BotController other = isA ? sp.B : sp.A;
            if (now - sp.Start > 3.5f) { EndPair(sp, "timed out", sp.FireAt >= 0f, now); return false; }
            bool soft; string why = SandWhyNot(sp.E, out soft);
            if (why != null)
            {
                if (!soft || sp.FireAt >= 0f) { EndPair(sp, why, sp.FireAt >= 0f, now); return false; }
                if (sp.SoftSince < 0f) sp.SoftSince = now;
                if (now - sp.SoftSince > 0.3f) { EndPair(sp, why + " for 0.3 s", false, now); return false; }
            }
            else sp.SoftSince = -1f;
            if (other == null || !other.Live || other.ActivePlayer == null || other.ActivePlayer.IsInStateHurt || other.ActivePlayer.IsInStateKO) { EndPair(sp, "partner gone / hurt", sp.FireAt >= 0f, now); return false; }
            if (sp.FireAt >= 0f)
            {
                if (isB && !sp.BTapped && now - sp.FireAt < 0.2f) { sp.BTapped = true; Tap(A_ATTACK); _tapT = 0.3f; BotLog.Move(BotIndex, "sandwich", "joining b" + (sp.A.BotIndex + 1) + "'s press"); Expect("sandwich", 0.8f, "Sandwich"); return true; }
                if (now - sp.FireAt > 0.9f) EndPair(sp, "done", true, now);
                return false;
            }
            if (_nearCount > 1 || p.IsInStateHurt) { EndPair(sp, "other enemies close / hurt", false, now); return false; }
            float side = isA ? sp.SideA : -sp.SideA;
            Vector2 edn = sp.E.Position2D;
            Vector2 spot = edn + new Vector2(side * 90f, 0f);
            TargetLabel = "sandwich spot b" + (other.BotIndex + 1);
            if (!InSandZone(me, edn, side, 3f, 1.5f)) { if (isA) sp.ReadyA = -9f; else sp.ReadyB = -9f; MoveTo(spot, 8f, 3f); SetIntent("sandwich: position"); return true; }
            bool facing = (side < 0f) == FacesRight(p);
            if (!facing) { _move.X = -side; SetIntent("sandwich: face it"); return true; }
            if (isA) sp.ReadyA = now; else sp.ReadyB = now;
            SetIntent("sandwich: ready");
            if (isA && now - sp.ReadyA < 0.15f && now - sp.ReadyB < 0.15f)
            {
                sp.FireAt = now; Tap(A_ATTACK); _tapT = 0.3f;
                BotLog.Evt(BotIndex, "sandwich-press", "b" + (BotIndex + 1) + " and b" + (sp.B.BotIndex + 1) + " around " + Name(sp.E) + " (me dx " + (int)Math.Abs(me.X - edn.X) + " dy " + (int)(me.Y - edn.Y) + ", partner dx " + (int)Math.Abs(sp.B.ActivePlayer.Position2D.X - edn.X) + " dy " + (int)(sp.B.ActivePlayer.Position2D.Y - edn.Y) + ")", 0f);
                BotLog.Move(BotIndex, "sandwich", "both in place around " + Name(sp.E) + ", pressing together with b" + (sp.B.BotIndex + 1)); Expect("sandwich", 0.8f, "Sandwich");
            }
            return true;
        }

        private float MeanEnemyY(Scene2d scene, Vector2 me)
        {
            float sum = 0f; int c = 0;
            foreach (Enemy n in BotScene.Enemies(scene, BotManager.Time))
            {
                if (n == null || !n.Active || !n.CanBeTargeted) continue;
                if (Vector2.Distance(n.Position2D, me) < 110f) { sum += n.Position2D.Y; c++; }
            }
            return c == 0 ? me.Y : sum / c;
        }

        /// <summary>Nearest living human player (the one to follow); any other player if no human is alive.</summary>
        private Player FindLeader(Scene2d scene, Player me)
        {
            Player best = null, any = null; float bd = float.MaxValue, ad = float.MaxValue;
            foreach (Player o in scene.Players.Values)
            {
                if (o == null || o == me || !o.Active || o.IsDead || o.PInfo == null) continue;
                float d = Vector2.DistanceSquared(o.Position2D, me.Position2D);
                if (!(o.PInfo.Controller is BotController)) { if (d < bd) { bd = d; best = o; } }
                else if (d < ad) { ad = d; any = o; }
            }
            return best ?? any;
        }

        private Player FindDownedAlly(Scene2d scene, Player me, Vector2 p2)
        {
            Player best = null; float bd = 260f * 260f;
            foreach (Player o in scene.Players.Values)
            {
                if (o == null || o == me || !o.Active || !o.CanBeRevived || !o.IsVisibleOnScreen) continue;
                if (BossScene.Super != null && NoLivesLeft(o)) { BotLog.Note(BotIndex, "no-lives-skip", "not reviving P" + (o.PInfo != null ? o.PInfo.PlayerID + 1 : 0) + ": no lives left (LifeCount " + o.LifeCount + ")", 6f); continue; }   // M4.10: Super Shredder fight only: a game-over slot stays down
                float d = Vector2.DistanceSquared(o.Position2D, p2);
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }
    }

    /// <summary>One enemy / prop list per scene refresh (shared by all bots, ~50 ms old at most) instead of one scene query per bot per scan.</summary>
    internal static class BotScene
    {
        private static object _sc; private static float _t = -1f;
        private static readonly List<Enemy> _en = new List<Enemy>(48);
        private static readonly List<Breakable> _pr = new List<Breakable>(64);
        private static readonly List<Hazard> _hz = new List<Hazard>(16);
        private static readonly List<Pickup> _pz = new List<Pickup>(16);
        private static Tenderizer _ten;
        private static AutoScrollController _asc; private static bool _surf; private static float _surfT = -9f; private static object _surfSc;
        internal static string SurfWhy = "";
        internal static int Refreshes;
        private static void Refresh(Scene2d sc, float now)
        {
            if (ReferenceEquals(sc, _sc) && now - _t < 0.05f && now >= _t) return;
            _sc = sc; _t = now; Refreshes++;
            _en.Clear(); _pr.Clear(); _pz.Clear(); _ten = null;
            foreach (object o in sc.GetGameObjectsOfType<Enemy>()) { Enemy e = o as Enemy; if (e != null) { _en.Add(e); if (_ten == null) { var t = e as Tenderizer; if (t != null && t.Active) _ten = t; } } }
            foreach (object o in sc.GetGameObjectsOfType<Breakable>()) { Breakable b = o as Breakable; if (b != null) _pr.Add(b); }
            // 0.4.16: health pizza only (exclude PartyPizza / StatusEffectPizza)
            foreach (object o in sc.GetGameObjectsOfType<Pizza>())
            {
                Pizza p = o as Pizza; if (p == null || !p.Active) continue;
                if (p is PartyPizza) continue;
                _pz.Add(p);
            }
            foreach (object o in sc.GetGameObjectsOfType<FlyingPizza>())
            {
                FlyingPizza f = o as FlyingPizza; if (f != null && f.Active) _pz.Add(f);
            }
        }
        internal static List<Enemy> Enemies(Scene2d sc, float now) { Refresh(sc, now); return _en; }
        internal static List<Breakable> Props(Scene2d sc, float now) { Refresh(sc, now); return _pr; }
        internal static Tenderizer LiveTenderizer(Scene2d sc, float now) { Refresh(sc, now); return _ten != null && _ten.Active ? _ten : null; }
        internal static List<Pickup> HealthPizzas(Scene2d sc, float now) { Refresh(sc, now); return _pz; }

        private static void RefreshSurf(Scene2d sc, float now)
        {
            if (ReferenceEquals(sc, _surfSc) && now - _surfT < 0.25f && now >= _surfT) return;
            _surfSc = sc; _surfT = now; _hz.Clear(); _asc = null; _surf = false; SurfWhy = "no autoscroll";
            try
            {
                foreach (object o in sc.GetGameObjectsOfType<AutoScrollController>()) { AutoScrollController a = o as AutoScrollController; if (a != null) { _asc = a; break; } }
                foreach (object o in sc.GetGameObjectsOfType<Hazard>()) { Hazard h = o as Hazard; if (h != null) _hz.Add(h); }
                if (_asc != null) { _surf = true; SurfWhy = "autoscroll level (AutoScrollController present)"; }
                if (!_surf)
                {
                    foreach (object o in sc.GetGameObjectsOfType<Player>())
                    {
                        Player pl = o as Player;
                        if (pl != null && pl.VehicleType != VehicleType.None) { _surf = true; SurfWhy = "a player is on a vehicle (" + pl.VehicleType + ")"; break; }
                    }
                }
            }
            catch { }
        }
        /// <summary>Surf / autoscroll stage: the game's own AutoScrollController is in the scene (or a player rides a vehicle).</summary>
        internal static bool Surf(Scene2d sc, float now) { RefreshSurf(sc, now); return _surf; }
        internal static List<Hazard> Hazards(Scene2d sc, float now) { RefreshSurf(sc, now); return _hz; }
        internal static AutoScrollController Scroll(Scene2d sc, float now) { RefreshSurf(sc, now); return _asc; }
    }
}
