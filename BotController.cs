using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine.System;
using Paris.Game.Actor;
using Paris.Game.Controller;
using Paris.Game.System;
using Paris.System.Input;

namespace ShreddedBots
{
    /// <summary>
    /// A bot's "controller". Extends the game's own DebugPlayerController (the scaffold the game uses for AI-driven players), so every
    /// engine path that special-cases debug players (character select auto-confirm, drop-in, ready-up, removal) keeps working.
    /// The game reads a player's input only through IsTriggered / IsJustTriggered / GetDirection, overridden here. The brain (BotBrain.cs)
    /// decides what to "press". Nothing here touches the keyboard, the network or any online code.
    /// </summary>
    public partial class BotController : DebugPlayerController
    {
        // Action ids of the gameplay buttons (see GamePlayerController).
        internal const string A_ATTACK = "PlayerAttack", A_JUMP = "PlayerJump", A_BACKFLIP = "PlayerBackflip",
            A_SPECIAL = "PlayerSpecialAttack", A_TAUNT = "PlayerTaunt", A_REVIVE = "PlayerCheerUpRevive";

        /// <summary>Which bot this is (0-based; selects the Bots config rows). Set by BotManager right before AddPlayer.</summary>
        public static int PendingIndex;
        public int BotIndex { get; private set; }

        private readonly HashSet<string> _down = new HashSet<string>();
        private readonly HashSet<string> _prev = new HashSet<string>();
        private readonly HashSet<string> _deferredTaps = new HashSet<string>();
        private readonly Dictionary<string, float> _holds = new Dictionary<string, float>();
        private readonly List<string> _holdKeys = new List<string>(8);
        private Vector2 _move;                    // world-space direction (X right, Y = depth lane, +Y toward the camera)

        public GamePlayerInfo Info { get; private set; }
        public Player ActivePlayer { get; private set; }
        public string Status { get; private set; } = "waiting";
        /// <summary>What the bot is doing right now (debug overlay).</summary>
        public string Intent { get; private set; } = "";
        public string TargetLabel { get; private set; } = "";

        /// <summary>All bot controllers (M4: shared target / revive / barrel coordination between bots). An entry is "live" only while it keeps ticking.</summary>
        internal static readonly List<BotController> All = new List<BotController>(6);
        internal float LastTick = -100f;
        internal bool Live => BotManager.Time - LastTick < 0.3f && ActivePlayer != null;

        public BotController()
        {
            BotIndex = PendingIndex;
            _scanT = 0.021f * BotIndex;   // stagger the scans: bots do not all scan in the same frame
            All.RemoveAll(c => BotManager.Time - c.LastTick > 10f || c.BotIndex == BotIndex);
            All.Add(this);
        }

        public override void Tick(float deltaTime)
        {
            base.Tick(deltaTime);
            _prev.Clear();
            foreach (string s in _down) _prev.Add(s);
            _down.Clear();
            foreach (string s in _deferredTaps) _down.Add(s);   // taps that had to wait one frame because the button was still down
            _deferredTaps.Clear();
            if (_holds.Count > 0)
            {
                _holdKeys.Clear(); _holdKeys.AddRange(_holds.Keys);
                foreach (string k in _holdKeys)
                {
                    float t = _holds[k];
                    if (t > 0f) { _down.Add(k); _holds[k] = t - deltaTime; }
                    else _holds.Remove(k);
                }
            }
            _move = Vector2.Zero;
            LastTick = BotManager.Time;
            try { Think(deltaTime); FilterMove(); }
            catch (Exception ex)
            {
                _down.Clear(); _holds.Clear(); _move = Vector2.Zero;
                Status = "error";
                Mod.LogOnce("bot-think", ex);
            }
        }

        // ---------------------------------------------------------------- input primitives used by the brain
        /// <summary>Press for one frame (a fresh "just pressed" edge). If the button was down last frame, the press happens next frame.</summary>
        private void Tap(string id)
        {
            if (_prev.Contains(id) && !_down.Contains(id)) _deferredTaps.Add(id);
            else _down.Add(id);
        }

        /// <summary>Press now and keep it down for <paramref name="seconds"/>.</summary>
        private void Hold(string id, float seconds)
        {
            Tap(id);
            _holds[id] = Math.Max(seconds, _holds.TryGetValue(id, out float cur) ? cur : 0f);
        }

        private void Release(string id) { _holds.Remove(id); _down.Remove(id); _deferredTaps.Remove(id); }
        private bool IsHolding(string id) => _holds.ContainsKey(id);

        // Player.GetDirection normalizes any non-zero stick value and facing flips on any non-zero X, so drop tiny components.
        private void FilterMove()
        {
            Vector2 m = _move;
            if (m != Vector2.Zero)
            {
                if (m.LengthSquared() > 1f) m.Normalize();
                if (Math.Abs(m.X) < 0.2f && Math.Abs(m.Y) >= 0.2f) m.X = 0f;
                if (Math.Abs(m.Y) < 0.2f && Math.Abs(m.X) >= 0.2f) m.Y = 0f;
            }
            _move = m;
        }

        // ---------------------------------------------------------------- input surface read by the game
        public override bool IsTriggered(InputActionID action) => action != null && _down.Contains(action.ID);

        public override bool IsJustTriggered(InputActionID action)
        {
            if (action == null) return false;
            if (action.ID == "MenuAccept") return base.IsJustTriggered(action);   // keep the engine's one-shot auto-confirm of hero/palette
            return _down.Contains(action.ID) && !_prev.Contains(action.ID);
        }

        public override Vector2 GetDirection(bool justPressed)
        {
            if (justPressed || _move == Vector2.Zero) return Vector2.Zero;
            Vector2 d = _move;
            if (d.LengthSquared() > 1f) d.Normalize();
            d.Y = -d.Y;   // Player.GetDirection flips Y back (same convention as the game's DebugPlayerController)
            return d;
        }

        // Menus never see a push from a bot. The game's sprint double-tap detector reads the ANALOG movement id only: "just pressed" yields the sprint direction
        // while the brain runs its sprint sequence (_sprintJp), the plain read yields the current push. Same shape the game's own HowToPlay controller uses.
        public override Vector2 GetDirection(InputDirectionActionID inputId, bool justPressed, bool shouldGetMouseDelta = false)
        {
            if (inputId == null || inputId.ID != "PlayerMoveAnalog" || ActivePlayer == null || Status != "active") return Vector2.Zero;
            if (justPressed) return _sprintJp && _sprintDirX != 0f ? new Vector2(_sprintDirX, 0f) : Vector2.Zero;
            if (_move == Vector2.Zero) return Vector2.Zero;
            Vector2 d = _move; if (d.LengthSquared() > 1f) d.Normalize();
            d.Y = -d.Y;
            return d;
        }

        private GamePlayerInfo FindMyInfo()
        {
            if (Info != null && Info.Controller == this) return Info;
            PlayerManager pm = PlayerManager.Singleton;
            if (pm == null) return null;
            foreach (PlayerInfo pi in pm.Players)
                if (pi != null && pi.Controller == this) return pi as GamePlayerInfo;
            return null;
        }
    }
}
