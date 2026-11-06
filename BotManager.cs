using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Paris.Engine;
using Paris.Engine.Context;
using Paris.Engine.Data;
using Paris.Engine.System;
using Paris.Engine.Scene;
using Paris.Engine.System.Networking;
using Paris.Game;
using Paris.Game.Actor;
using Paris.Game.Data;
using Paris.Game.HUD;
using Paris.Game.Menu;
using Paris.Game.Menu.Control;
using Paris.Game.System;
using Paris.System.Input;

namespace ShreddedBots
{
    /// <summary>
    /// Adds / removes the bot players and decides which hero and palette each one takes. Bots are ordinary local players (a
    /// DebugPlayerController scaffold driven by BotController), joined with PlayerManager.AddPlayer exactly like a second local human
    /// would be. Everything here is local: bots are never added while an online session exists.
    /// </summary>
    public static class BotManager
    {
        public static float Time;
        private static float _charSelectSeen = -10f, _hudSeen = -10f, _reconcileTimer, _nextAddAt;
        public static CharacterSelect CharSelect;
        public static MainHUD Hud;
        public static string LastAction = "";
        private static readonly bool[] RequeueOnSelect = new bool[Config.MaxBots];
        private static readonly float[] RequeueAt = new float[Config.MaxBots];
        private static readonly bool[] FallbackLogged = new bool[Config.MaxBots];
        private static readonly float[] _fallbackLogAt = NewFloats(-10f), _noHeroSince = NewFloats(-1f);   // M4.5
        private static float[] NewFloats(float v) { var a = new float[Config.MaxBots]; for (int i = 0; i < a.Length; i++) a[i] = v; return a; }
        private static bool _saveInfoLogged, _limitLogged;
        private static readonly int[] Planned = NewPlanned();
        private static int[] NewPlanned() { var a = new int[Config.MaxBots]; for (int i = 0; i < a.Length; i++) a[i] = -1; return a; }

        /// <summary>M4: stagger for supers. A bot may only start a (non-desperate) super when this time has passed; every super pushes it back a little so five bots do not all burn their meter in the same second.</summary>
        public static float SuperGateUntil;
        public static bool SuperGateOpen => Time >= SuperGateUntil;
        public static void SuperFired(float now, float rnd) { SuperGateUntil = Math.Max(SuperGateUntil, now + 1.2f + rnd * 0.9f); }

        public static bool InCharSelect => CharSelect != null && Time - _charSelectSeen < 0.3f;
        public static bool InLevel => Hud != null && Time - _hudSeen < 0.3f && Scene2d.Active != null;
        public static void SeenCharSelect(CharacterSelect cs) { CharSelect = cs; _charSelectSeen = Time; }
        public static void SeenHud(MainHUD hud) { Hud = hud; _hudSeen = Time; }

        /// <summary>Hero / color changed in the F7 menu: re-join after a short debounce (only possible on the character select screen).</summary>
        public static void RequestRequeue(int bot, float delay = 1.0f) { if (bot >= 0 && bot < Config.MaxBots) { RequeueOnSelect[bot] = true; RequeueAt[bot] = Time + delay; FallbackLogged[bot] = false; } }

        public static bool Online
        {
            get
            {
                try
                {
                    var nm = NetworkManagerBase.Singleton;
                    if (nm != null && nm.IsConnected) return true;
                    var pm = PlayerManager.Singleton;
                    return pm != null && pm.HasNetworkPlayers();
                }
                catch { return false; }
            }
        }

        private static readonly AccessTools.FieldRef<CharacterSelect, CharacterSelectionContainer> CharSelectContainerRef =
            AccessTools.FieldRefAccess<CharacterSelect, CharacterSelectionContainer>("_charContainer");
        private static readonly AccessTools.FieldRef<MainHUD, CharacterSelectionContainer> HudContainerRef =
            AccessTools.FieldRefAccess<MainHUD, CharacterSelectionContainer>("_charContainer");

        public static CharacterSelectionContainer Container
        {
            get
            {
                try
                {
                    if (InCharSelect) return CharSelectContainerRef(CharSelect);
                    if (InLevel) return HudContainerRef(Hud);
                }
                catch (Exception ex) { Mod.LogOnce("container", ex); }
                return null;
            }
        }

        /// <summary>True when bots may be added right now: a real context, the host exists, and (in a stage) the host is actually playing.</summary>
        public static bool ContextReady
        {
            get
            {
                var cm = ContextManager.Singleton;
                if (cm == null || cm.State != ContextManager.LoadingState.None) return false;
                PlayerInfo host = PlayerManager.Singleton?.LocalHostPlayer;
                if (host == null) return false;
                if (InCharSelect) return true;
                if (InLevel)
                {
                    Player hp = Scene2d.Active.GetPlayer(host) as Player;
                    return hp != null && hp.Active && hp.HasControl;
                }
                return false;
            }
        }

        public static bool HooksOk => Hooks.CategoryOk("bots");

        public static PlayerInfo GetBot(int k)
        {
            var pm = PlayerManager.Singleton;
            if (pm == null) return null;
            foreach (PlayerInfo pi in pm.Players)
                if (pi?.Controller is BotController b && b.BotIndex == k) return pi;
            return null;
        }

        public static int BotIndexOf(PlayerInfo pi) => pi?.Controller is BotController b ? b.BotIndex : -1;

        public static string BotStatus(int k)
        {
            PlayerInfo pi = GetBot(k);
            if (pi == null)
            {
                if (!Config.BotOn[k]) return "off";   // M4.12: the per-bot switch is the only control
                if (!HooksOk) return "unavailable (hooks)";
                return Online ? "disabled (online)" : "waiting to join";
            }
            var g = pi as GamePlayerInfo;
            string hero = g != null && g.SelectedCharacter != byte.MaxValue ? HeroName(g.SelectedCharacter) : "picking";
            return "P" + (pi.PlayerID + 1) + " " + hero + ", " + ((BotController)pi.Controller).Status;
        }

        public static void Tick(float dt)
        {
            Time += dt;
            BotLog.Tick(dt);
            _reconcileTimer -= dt;
            if (_reconcileTimer > 0f) return;
            _reconcileTimer = 0.25f;
            try { Reconcile(); } catch (Exception ex) { Mod.LogOnce("reconcile", ex); }
        }

        private static void Reconcile()
        {
            PlayerManager pm = PlayerManager.Singleton;
            if (pm == null) return;
            bool online = Online;
            bool canRun = HooksOk;
            PreviewTick();
            for (int k = 0; k < Config.MaxBots; k++)
            {
                PlayerInfo pi = GetBot(k);
                bool want = Config.BotOn[k] && !online && canRun;   // M4.12: was also Enabled and k < Count (both folded into the per-bot switches)
                if (pi != null)
                {
                    if (!want) Remove(k, online ? "online session detected" : "disabled");
                    else if (pi.PlayerID != SlotOf(k)) Remove(k, "in slot P" + (pi.PlayerID + 1) + ", not its own slot P" + (SlotOf(k) + 1) + " (the game renumbers players on character select); rejoining in P" + (SlotOf(k) + 1));   // M4.18
                    else if (RequeueNow(k)) Remove(k, "re-picking hero / color");
                }
                else if (want && ContextReady && Time >= _nextAddAt && SlotFree(k) && CanAddMore())
                {
                    Add(k); _nextAddAt = Time + 0.5f;
                }
                if (!want || pi != null) _slotBusyLogged[k] = false;
            }
        }

        private static bool RequeueNow(int k)
        {
            if (!RequeueOnSelect[k]) return false;
            if (!InCharSelect) { RequeueOnSelect[k] = false; return false; }   // not on the select screen: applies at the next join
            if (Time < RequeueAt[k]) return false;
            RequeueOnSelect[k] = false;
            return true;
        }

        // M4.18: "Pn Bot" owns exactly player slot n (PlayerID n-1). The game's AddPlayer always takes the lowest free
        // PlayerID (PlayerManager.FindAvailableID), so the bot is moved to its own slot right after it joins.
        private static readonly bool[] _slotBusyLogged = new bool[Config.MaxBots];
        public static int SlotOf(int k) => k + 1;

        private static bool SlotFree(int k)
        {
            int slot = SlotOf(k);
            int max = EngineSettings.Singleton.MaxPlayers;
            var c = Container;
            if (c != null) max = Math.Min(max, c.MaxPlayerAllowed);
            PlayerInfo other = PlayerManager.Singleton.GetPlayerByID(slot);
            bool ok = slot < max && other == null;
            if (!ok && !_slotBusyLogged[k])
            {
                _slotBusyLogged[k] = true;
                Log.Info("[bot] P" + (slot + 1) + " Bot waits: " + (other != null ? "slot P" + (slot + 1) + " is taken by " + (other.Controller is BotController ? "another bot" : "a player") + " (not kicked)" : "slot P" + (slot + 1) + " is past the game limit " + max));
            }
            return ok;
        }

        private static bool CanAddMore()
        {
            int max = EngineSettings.Singleton.MaxPlayers;
            var c = Container;
            if (c != null) max = Math.Min(max, c.MaxPlayerAllowed);
            bool ok = PlayerManager.Singleton.GetPlayerCount() < max;
            if (!ok && !_limitLogged)
            {
                _limitLogged = true;
                Log.Info("[bot] player limit reached: " + PlayerManager.Singleton.GetPlayerCount() + " players, game limit " + max + " (EngineSettings.MaxPlayers=" + EngineSettings.Singleton.MaxPlayers + (c != null ? ", container MaxPlayerAllowed=" + c.MaxPlayerAllowed : "") + "); remaining bots wait");
            }
            else if (ok) _limitLogged = false;
            return ok;
        }

        public static void Add(int k)
        {
            if (Online) return;   // never from bot code while online
            PlayerManager pm = PlayerManager.Singleton;
            ulong handle = 0;
            while (handle < 16 && pm.ControllerAlreadyUsed(ParisInputType.Debug, handle)) handle++;
            Type prev = PlayerManager.DebugControllerType;
            PlayerManager.DebugControllerType = typeof(BotController);
            BotController.PendingIndex = k;
            try
            {
                PlayerInfo pi = pm.AddPlayer(ParisInputType.Debug, handle);
                if (pi != null && pi.PlayerID != SlotOf(k) && pm.GetPlayerByID(SlotOf(k)) == null)
                {
                    Log.Info("[bot] P" + (SlotOf(k) + 1) + " Bot: game gave slot P" + (pi.PlayerID + 1) + ", moved to P" + (SlotOf(k) + 1));
                    pi.ForcePlayerID((byte)SlotOf(k));   // M4.18: before any panel/stage tick binds the slot
                }
                LastAction = pi != null ? "P" + (SlotOf(k) + 1) + " Bot joined as P" + (pi.PlayerID + 1) : "P" + (SlotOf(k) + 1) + " Bot could not join";
                Log.Info("[bot] " + LastAction + (pi != null ? " (handle " + handle + ")" : "") + " [players now " + pm.GetPlayerCount() + ", game limit " + EngineSettings.Singleton.MaxPlayers + ", " + (InCharSelect ? "character select" : "in stage") + "]");
                if (pi != null) LogSaveInfo(pm, pi);
            }
            finally { PlayerManager.DebugControllerType = prev; }
        }

        /// <summary>Logs (once per game run) how the game gave the bot its save: shared with the host or its own file. This is the same thing the game does for a second local human.</summary>
        private static void LogSaveInfo(PlayerManager pm, PlayerInfo bot)
        {
            if (_saveInfoLogged) return;
            _saveInfoLogged = true;
            try
            {
                PlayerInfo host = pm.LocalHostPlayer;
                bool share = EngineSettings.Singleton.ShareSavePerAccount;
                bool same = host != null && ReferenceEquals(host.Save, bot.Save);
                string fn = "?";
                try { var pr = bot.Save?.GetType().GetProperty("Filename", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (pr != null) fn = (string)pr.GetValue(bot.Save, null); } catch { }
                Log.Info("[bot] SAVE: ShareSavePerAccount=" + share + ", bot save object is the host's save: " + same + ", bot save file: " + fn +
                         ". Same as a second local human: XP / power level go to the bot's hero entry in that save.");
            }
            catch (Exception ex) { Log.Warn("[bot] save info failed: " + ex.Message); }
        }

        public static void Remove(int k, string why)
        {
            PlayerManager pm = PlayerManager.Singleton;
            PlayerInfo pi = GetBot(k);
            if (pi == null) return;
            var bot = (BotController)pi.Controller;
            try
            {
                if (Scene2d.Active != null && Scene2d.Active.Players != null)
                {
                    // 0.8e: hide only THIS bot's current fighter. Owner was cached once and could point at a pooled fighter object
                    // the game has since handed to another player (P2 off hid P5), so it is re-read from the scene every time.
                    bot.Owner = Scene2d.Active.GetPlayer(pi) as Player;
                    if (bot.Owner != null) bot.Deactivate();   // same sequence the game's own debug-player removal uses
                }
            }
            catch (Exception ex) { Log.Warn("[bot] Deactivate failed: " + ex.Message); }
            BotLog.StageEnd(k, why);
            Planned[k] = -1; _noHeroSince[k] = -1f;
            byte id = pi.PlayerID;
            if (pi is GamePlayerInfo gpi) gpi.SelectedCharacter = byte.MaxValue;
            pm.RemovePlayer(id);
            try { Scene2d.Active?.RemovePlayer(pi); } catch (Exception ex) { Log.Warn("[bot] scene remove failed: " + ex.Message); }   // 0.8e: drop the scene's PlayerInfo -> fighter entry now (the game does the same on its next tick), so a quick re-join never shares a fighter with a stale entry
            LastAction = "bot " + (k + 1) + " left (" + why + ")";
            Log.Info("[bot] " + LastAction);
        }

        // ------------------------------------------------------------ hero / palette
        public static bool SameCharacterPicksAllowed()
        {
            try { return CustomGameConfig.Singleton.IsActive(CustomGameSetting.SameCharacterPicks) || (GameInfo.Singleton.GameMode == Paris.Game.GameMode.Survival && GameInfo.Singleton.SurvivalInfo.KidsMode); }
            catch { return false; }
        }

        /// <summary>Unlocked and (DLC) owned for the given save? Uses the save the game itself gave that player.</summary>
        public static bool IsHeroUnlocked(int idx, SaveGame save)
        {
            var list = CharacterManager.Singleton?.Characters;
            if (list == null || idx < 0 || idx >= list.Count) return false;
            CharacterInfo ci = list[idx];
            try
            {
                if (ci == null || ci.Disabled) return false;
                if (save != null && (idx >= save.Heroes.Count || (int)save.Heroes[idx].Lockstate == 0)) return false;
                return ci.DLCRequirement.IsEmpty || ci.DLCRequirement.IsUnlocked();
            }
            catch { return false; }
        }

        /// <summary>M4.4 (menu): the human's hero right now (confirmed pick of the local host), or -1.</summary>
        public static int HumanHero()
        {
            try { return PlayerManager.Singleton?.LocalHostPlayer is GamePlayerInfo g && g.SelectedCharacter != byte.MaxValue ? g.SelectedCharacter : -1; }
            catch { return -1; }
        }

        /// <summary>M4.4 (menu): unlocked and (DLC) owned in the human's save, the same test the bots use when they pick.</summary>
        public static bool HeroOwned(int idx)
        {
            try { return IsHeroUnlocked(idx, (PlayerManager.Singleton?.LocalHostPlayer as GamePlayerInfo)?.Save as SaveGame); }
            catch { return false; }
        }

        public static bool IsHeroTakenByOther(int idx, PlayerInfo me)
        {
            if (SameCharacterPicksAllowed()) return false;
            foreach (PlayerInfo o in PlayerManager.Singleton.Players)
                if (o != me && o is GamePlayerInfo g && g.SelectedCharacter == idx) return true;
            return false;
        }

        public static int IndexOf(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return -1;
            var list = CharacterManager.Singleton?.Characters;
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++) if (string.Equals(list[i]?.InternalName, internalName, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public static string HeroName(int idx)
        {
            var list = CharacterManager.Singleton?.Characters;
            if (list == null || idx < 0 || idx >= list.Count || list[idx] == null) return "?";
            string n = null;
            try { n = list[idx].ShortName?.GetString(); } catch { }
            if (string.IsNullOrEmpty(n) || n.StartsWith("#")) n = list[idx].InternalName;
            return Menu.Sanitize(n);
        }

        /// <summary>Another bot (lower or higher index) already planned / hovers this hero: never pick it twice, even before anybody has confirmed.</summary>
        private static bool PlannedByOtherBot(int idx, int k)
        {
            bool kExplicit = IndexOf(Config.BotHero[k]) == idx;
            for (int j = 0; j < Planned.Length; j++)
            {
                if (j == k || Planned[j] != idx || GetBot(j) == null) continue;
                // M4.5: a hero the user picked by name beats another slot that only landed on it through Auto (a picked hero could lose to an Auto slot and the slot bounced through several re-picks)
                bool jExplicit = IndexOf(Config.BotHero[j]) == idx;
                if (kExplicit && !jExplicit) continue;
                return true;
            }
            return false;
        }

        /// <summary>M4.5: does another present bot slot pick this hero by name (and can really get it)?</summary>
        private static bool WantedByOtherBot(int idx, int k, SaveGame save)
        {
            for (int j = 0; j < Config.MaxBots; j++)
                if (j != k && GetBot(j) != null && IndexOf(Config.BotHero[j]) == idx && IsHeroUnlocked(idx, save)) return true;
            return false;
        }

        private static bool HeroFree(int idx, int k, GamePlayerInfo pi, SaveGame save)
        {
            if (!IsHeroUnlocked(idx, save)) return false;
            if (IsHeroTakenByOther(idx, pi)) return false;
            if (!SameCharacterPicksAllowed())
            {
                if (PlannedByOtherBot(idx, k)) return false;
                if (IndexOf(Config.BotHero[k]) != idx && WantedByOtherBot(idx, k, save)) return false;   // Auto never takes a hero another slot asked for
            }
            return true;
        }

        /// <summary>The hero index bot k should confirm next, or -1. Each bot reserves its pick (Planned) so two bots hovering at the same time never end up on one hero; the human's hero and heroes that are locked or whose DLC is not owned are never used.</summary>
        public static int ResolveHero(int k, GamePlayerInfo pi)
        {
            var cm = CharacterManager.Singleton;
            if (cm == null) return -1;
            SaveGame save = pi.Save as SaveGame;
            int cur = Planned[k];
            int want = IndexOf(Config.BotHero[k]);
            if (Planned[k] >= 0) _noHeroSince[k] = -1f;
            if (want >= 0)
            {
                if (HeroFree(want, k, pi, save)) return Planned[k] = want;
                if (!FallbackLogged[k] && Time - _fallbackLogAt[k] > 3f) { FallbackLogged[k] = true; _fallbackLogAt[k] = Time; Log.Info("[bot] bot " + (k + 1) + ": wanted hero '" + Config.BotHero[k] + "' is " + (!IsHeroUnlocked(want, save) ? "locked / not owned" : IsHeroTakenByOther(want, pi) ? "already taken (the human or another player has it)" : "already used by another bot slot") + "; using Auto for this slot instead"); }
            }
            // keep an earlier valid plan (stable, no flicker while others join)
            if (cur >= 0 && want < 0 && HeroFree(cur, k, pi, save)) return cur;
            // Auto: the game's own "next available hero" walk (skips locked, DLC not owned, and heroes players already confirmed), repeated until one is also not reserved by another bot
            PlayerInfo host = PlayerManager.Singleton.LocalHostPlayer;
            int n = cm.Characters.Count;
            int start = host is GamePlayerInfo hg && hg.SelectedCharacter != byte.MaxValue ? hg.SelectedCharacter : 0;
            start = (start + k * 3) % n;
            int r = start;
            for (int guard = 0; guard < n + 1; guard++)
            {
                r = cm.GetNextAvailableCharacterIndex(r, save, false);
                if (HeroFree(r, k, pi, save)) return Planned[k] = r;
            }
            // M4.5: nothing free after the reservation rules: never leave the slot waiting for ever (a slot that never confirms blocks the whole select screen). After 4 s take the game's own next available hero, ignoring reservations.
            if (_noHeroSince[k] < 0f) _noHeroSince[k] = Time;
            if (Time - _noHeroSince[k] > 4f)
            {
                r = start;
                for (int guard = 0; guard < n + 1; guard++)
                {
                    r = cm.GetNextAvailableCharacterIndex(r, save, false);
                    if (r >= 0 && r < n && IsHeroUnlocked(r, save) && !IsHeroTakenByOther(r, pi)) { Log.Info("[bot] bot " + (k + 1) + ": no free hero after the reservation rules for 4 s; taking '" + HeroName(r) + "' so the slot is not left empty"); _noHeroSince[k] = -1f; return Planned[k] = r; }
                }
            }
            Planned[k] = -1;
            return -1;
        }

        /// <summary>For the debug summary line: hero of bot k (or "").</summary>
        public static string HeroOfBot(int k)
        {
            try
            {
                var g = GetBot(k) as GamePlayerInfo;
                return g != null && g.SelectedCharacter != byte.MaxValue ? HeroName(g.SelectedCharacter) : "";
            }
            catch { return ""; }
        }

        private static readonly MethodInfo IsValidPaletteM = AccessTools.Method(typeof(CharacterInfo), "IsValidPalette");

        /// <summary>The palette (animation texture id) bot k should wear on hero <paramref name="hero"/>; 1 = the first / default palette.
        /// M4.8: when two players may share a hero (same-character picks / kids mode) a palette another player already wears on that hero is skipped to the next free valid one, as the game does for a human.
        /// <paramref name="maxPal"/> = highest palette id the portrait has (0 = unknown, 8).</summary>
        public static int ResolvePalette(int k, int hero, GamePlayerInfo pi, int maxPal = 0)
        {
            int want = Config.BotPalette[k];
            int pal = 1;
            bool shared = false;
            Func<int, bool> valid = c => c <= 1;
            try
            {
                var save = pi.Save as SaveGame;
                var ci = CharacterManager.Singleton.Characters[hero];
                HeroInfo hi = save != null && hero < save.Heroes.Count ? save.Heroes[hero] : null;
                valid = c => c <= 1 || (IsValidPaletteM != null && (bool)IsValidPaletteM.Invoke(ci, new object[] { c, hi }));
                if (want > 1 && (maxPal <= 0 || want <= maxPal) && valid(want)) pal = want;
                shared = SameCharacterPicksAllowed();
                if (shared)
                {
                    var used = new List<int>();
                    foreach (PlayerInfo o in PlayerManager.Singleton.Players)
                        if (o != pi && o is GamePlayerInfo g && g.SelectedCharacter == hero) used.Add(g.SelectedPalette);
                    pal = PickFreePalette(pal, maxPal <= 0 ? 8 : maxPal, used, valid);
                }
            }
            catch (Exception ex) { Mod.LogOnce("palette", ex); }
            return pal;
        }

        /// <summary>M4.8 (pure): the first valid palette at or after <paramref name="want"/> (wrapping 1..max) that is not in <paramref name="used"/>; the wanted one when nothing is free.</summary>
        public static int PickFreePalette(int want, int max, ICollection<int> used, Func<int, bool> valid)
        {
            if (want < 1) want = 1;
            if (max < 1) max = 1;
            if (want > max) want = 1;
            int c = want;
            for (int i = 0; i <= max; i++)
            {
                if (valid(c) && (used == null || !used.Contains(c))) return c;
                c++; if (c > max) c = 1;
            }
            return want;
        }

        // ---------------- M4.8: live color / hero preview on the character select screen ----------------
        private static readonly bool[] PreviewLock = new bool[Config.MaxBots];
        private static float _outOfSelectSince = -1f;
        private static readonly FieldInfo PortraitF = AccessTools.Field(typeof(CharacterSelectionPanel), "_portrait");

        /// <summary>The character select panel that belongs to bot k (null when the bot has not joined).</summary>
        public static CharacterSelectionPanel PanelOf(int k)
        {
            CharacterSelectionContainer c = Container;
            if (c?.Panels == null) return null;
            foreach (CharacterSelectionPanel p in c.Panels)
                if (p?.PlayerInfo != null && BotIndexOf(p.PlayerInfo) == k) return p;
            return null;
        }

        /// <summary>Highest palette id the panel's portrait has (0 when it cannot be read).</summary>
        private static int PortraitMaxPalette(CharacterSelectionPanel panel)
        {
            try
            {
                object ctl = PortraitF?.GetValue(panel);
                object anim = ctl?.GetType().GetProperty("Animator")?.GetValue(ctl, null);
                object max = anim?.GetType().GetProperty("AnimationTextureIDMax")?.GetValue(anim, null);
                return max is int m && m > 0 && m < 64 ? m : 0;
            }
            catch { return 0; }
        }

        /// <summary>Called each reconcile: the "locked in for preview" flags only live while the character select screen is up.</summary>
        private static void PreviewTick()
        {
            if (InCharSelect) { _outOfSelectSince = -1f; return; }
            if (_outOfSelectSince < 0f) _outOfSelectSince = Time;
            else if (Time - _outOfSelectSince > 3f) for (int k = 0; k < PreviewLock.Length; k++) PreviewLock[k] = false;
        }

        /// <summary>F7 color change: on the character select screen lock the bot in on its hero (if it is not yet) and show the new palette on its portrait right away, by setting the panel's own CurrentPalette (what a player cycling palettes does). Returns true when it handled the change live.</summary>
        public static bool PreviewColor(int k)
        {
            if (k < 0 || k >= Config.MaxBots || !InCharSelect) return false;
            try
            {
                PreviewLock[k] = true;
                if (RequeueOnSelect[k]) return true;   // a hero change is re-joining the bot: it picks up the new color on the way in
                CharacterSelectionPanel panel = PanelOf(k);
                GamePlayerInfo gi = panel?.PlayerInfo;
                if (panel == null || gi == null)
                {
                    BotLog.Evt(k, "color-preview", "bot not on the select screen yet; it will lock in with the new color when it joins", 0f);
                    return true;
                }
                if (panel.IsSelectingCharacter)
                {
                    BotLog.Evt(k, "color-preview", "locking in on the hovered hero; the palette is applied when it confirms (palette " + Config.BotPalette[k] + ")", 0f);
                    return true;
                }
                if (panel.IsReady || panel.IsSelectingPalette)
                {
                    int hero = panel.CurrentSelection;
                    int pal = ResolvePalette(k, hero, gi, PortraitMaxPalette(panel));
                    int before = panel.CurrentPalette;
                    if (before != pal) panel.CurrentPalette = pal;   // updates the portrait and tells the container (SelectedPalette) like a human cycling palettes
                    if (gi.SelectedPalette != (byte)pal) gi.SelectedPalette = (byte)pal;
                    BotLog.Evt(k, "color-preview", "hero " + HeroName(hero) + " palette " + before + " -> " + pal + (pal != (Config.BotPalette[k] <= 1 ? 1 : Config.BotPalette[k]) ? " (wanted " + Config.BotPalette[k] + " is not available: next free)" : "") + (panel.IsReady ? " (ready)" : " (choosing)"), 0f);
                    return true;
                }
                return true;
            }
            catch (Exception ex) { Mod.LogOnce("charpal-live", ex); return false; }
        }

        /// <summary>F7 hero change: lock the slot in so it shows the hero for real. A bot that is only hovering simply confirms the new pick; one that is already ready re-joins (short debounce) and locks in again.</summary>
        public static bool PreviewHero(int k)
        {
            if (k < 0 || k >= Config.MaxBots || !InCharSelect) return false;
            try
            {
                PreviewLock[k] = true;
                CharacterSelectionPanel panel = PanelOf(k);
                if (panel != null && !panel.IsSelectingCharacter)
                {
                    RequestRequeue(k, 0.5f);
                    BotLog.Evt(k, "color-preview", "hero change: re-joining to lock in on the new hero", 0f);
                }
                else BotLog.Evt(k, "color-preview", "hero change: locking in on the new hero", 0f);
                return true;
            }
            catch (Exception ex) { Mod.LogOnce("charpal-live", ex); return false; }
        }

        /// <summary>Menu mode waits until every human has confirmed (so a human can still take a hero); in-stage joins confirm at once.</summary>
        public static bool ShouldLockIn(bool gameMode, int k)
        {
            if (!gameMode && k >= 0 && k < PreviewLock.Length && PreviewLock[k] && InCharSelect) return true;   // M4.8: the F7 menu is previewing this bot
            return ShouldLockIn(gameMode);
        }

        public static bool ShouldLockIn(bool gameMode)
        {
            if (gameMode) return true;
            CharacterSelectionContainer c = Container;
            if (c?.Panels == null) return false;
            int humans = 0;
            foreach (CharacterSelectionPanel p in c.Panels)
            {
                if (p?.PlayerInfo == null || p.PlayerInfo.Controller is BotController) continue;
                humans++;
                if (!p.IsReady) return false;
            }
            return humans > 0;
        }
    }
}
