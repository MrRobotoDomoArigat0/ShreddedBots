using System;
using HarmonyLib;
using Paris.System.FSM;
using Paris.Engine.System;
using Paris.Game;
using Paris.Game.HUD;
using Paris.Game.Menu;
using Paris.Game.System;

namespace ShreddedBots
{
    /// <summary>Hooks the bots need (non-critical: if any is missing, bots simply do not join and the F7 menu says so).</summary>
    public static class BotPatches
    {
        private const string Cat = "bots";
        private static readonly AccessTools.FieldRef<CharacterSelectionPanel, CharacterSelectMode> PanelMode =
            AccessTools.FieldRefAccess<CharacterSelectionPanel, CharacterSelectMode>("_mode");

        public static void Apply(Harmony h)
        {
            Hooks.Patch(Cat, "CharacterSelect.Tick(float) postfix (character select seen)", AccessTools.Method(typeof(CharacterSelect), "Tick", new[] { typeof(float) }), false, typeof(BotPatches), postfix: nameof(CharSelectTickPostfix));
            Hooks.Patch(Cat, "MainHUD.Tick(float) postfix (in stage)", AccessTools.Method(typeof(MainHUD), "Tick", new[] { typeof(float) }), false, typeof(BotPatches), postfix: nameof(HudTickPostfix));
            Hooks.Patch(Cat, "CharacterSelectionPanel.StateSelectCharacter prefix (bot hero)", AccessTools.Method(typeof(CharacterSelectionPanel), "StateSelectCharacter", new[] { typeof(FSM.StateStep), typeof(float) }), false, typeof(BotPatches), prefix: nameof(StateSelectCharacterPrefix));
            Hooks.Patch(Cat, "CharacterSelectionPanel.StateSelectPalette prefix (bot color)", AccessTools.Method(typeof(CharacterSelectionPanel), "StateSelectPalette", new[] { typeof(FSM.StateStep), typeof(float) }), false, typeof(BotPatches), prefix: nameof(StateSelectPalettePrefix));
        }

        public static void CharSelectTickPostfix(CharacterSelect __instance) => BotManager.SeenCharSelect(__instance);
        public static void HudTickPostfix(MainHUD __instance) => BotManager.SeenHud(__instance);

        public static bool StateSelectCharacterPrefix(CharacterSelectionPanel __instance, FSM.StateStep stateStep)
        {
            if (stateStep != FSM.StateStep.Tick) return true;
            try
            {
                GamePlayerInfo pi = __instance.PlayerInfo;
                int k = BotManager.BotIndexOf(pi);
                if (k < 0) return true;
                if (PlayerManager.Singleton.GetPlayerByID(pi.PlayerID) == null) return true;   // vanilla handles leaving
                bool gameMode = PanelMode(__instance) == CharacterSelectMode.Game;
                int target = BotManager.ResolveHero(k, pi);
                if (target < 0) return false;   // nothing usable: keep waiting (never confirm a locked hero)
                if (__instance.CurrentSelection != target) __instance.CurrentSelection = target;   // bot "hovers" its hero
                if (gameMode)
                {
                    int pal = BotManager.ResolvePalette(k, target, pi);
                    if (__instance.CurrentPalette != pal) __instance.CurrentPalette = pal;          // in-stage confirm reads the panel's palette
                }
                if (!BotManager.ShouldLockIn(gameMode, k)) return false;   // wait: skip vanilla (it would auto-confirm at once)
                return true;                                            // vanilla Debug path confirms CurrentSelection
            }
            catch (Exception ex) { Mod.LogOnce("charselect", ex); return true; }
        }

        public static bool StateSelectPalettePrefix(CharacterSelectionPanel __instance, FSM.StateStep stateStep)
        {
            if (stateStep != FSM.StateStep.Tick) return true;
            try
            {
                GamePlayerInfo pi = __instance.PlayerInfo;
                int k = BotManager.BotIndexOf(pi);
                if (k < 0) return true;
                int pal = BotManager.ResolvePalette(k, __instance.CurrentSelection, pi);
                if (__instance.CurrentPalette != pal) __instance.CurrentPalette = pal;   // vanilla then confirms the portrait's palette
            }
            catch (Exception ex) { Mod.LogOnce("palette-select", ex); }
            return true;
        }
    }
}
