using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.System.Input;

namespace ShreddedBots
{
    /// <summary>Harmony hooks for the F7 menu: per-frame tick, drawing, and keyboard capture while the menu is open. (Non-critical: failures only disable the menu.)</summary>
    public static class GamePatches
    {
        private const string Cat = "menu";

        public static void Apply(Harmony h)
        {
            Menu.InitKey();
            Hooks.Patch(Cat, "PlayerManager.Tick(float) prefix (menu tick)", AccessTools.Method("Paris.Engine.System.PlayerManager:Tick", new[] { typeof(float) }), false, typeof(GamePatches), prefix: nameof(PlayerManagerTickPrefix));
            Hooks.Patch(Cat, "AchievementManagerBase.Render() postfix (menu drawing)", AccessTools.Method("Paris.Engine.System.Achievements.AchievementManagerBase:Render", Type.EmptyTypes), false, typeof(GamePatches), postfix: nameof(RenderPostfix));

            // keyboard is captured by the mod menu while it is open
            Hooks.Patch(Cat, "InputActionID.IsJustTriggered capture", AccessTools.Method(typeof(InputActionID), "IsJustTriggered", new[] { typeof(ParisInputType) }), false, typeof(GamePatches), prefix: nameof(BlockBoolPrefix));
            Hooks.Patch(Cat, "InputActionID.IsTriggered capture", AccessTools.Method(typeof(InputActionID), "IsTriggered", new[] { typeof(ParisInputType) }), false, typeof(GamePatches), prefix: nameof(BlockBoolPrefix));
            Hooks.Patch(Cat, "InputDirectionActionID.GetDirection(Vector2) capture", AccessTools.Method(typeof(InputDirectionActionID), "GetDirection", new[] { typeof(ParisInputType), typeof(bool), typeof(bool) }), false, typeof(GamePatches), prefix: nameof(BlockDirPrefix));
            Hooks.Patch(Cat, "InputDirectionActionID.GetDirection(float) capture", AccessTools.Method(typeof(InputDirectionActionID), "GetDirection", new[] { typeof(ParisInputType), typeof(InputDirection), typeof(bool), typeof(bool) }), false, typeof(GamePatches), prefix: nameof(BlockDirFloatPrefix));
            Hooks.Patch(Cat, "InputManagerBase.SomeonePressedStart capture", AccessTools.Method(typeof(InputManagerBase), "SomeonePressedStart", new[] { typeof(bool), typeof(bool), typeof(ulong).MakeByRefType() }), false, typeof(GamePatches), prefix: nameof(BlockStartPrefix));
            Hooks.Patch(Cat, "InputManager.SomeonePressedStart capture", AccessTools.Method(typeof(InputManager), "SomeonePressedStart", new[] { typeof(bool), typeof(bool), typeof(ulong).MakeByRefType() }), false, typeof(GamePatches), prefix: nameof(BlockStartPrefix));
        }

        public static void PlayerManagerTickPrefix(float deltaTime)
        {
            try { Menu.Tick(deltaTime); } catch (Exception ex) { Mod.LogOnce("menu-tick", ex); }
            try { BotManager.Tick(deltaTime); } catch (Exception ex) { Mod.LogOnce("bot-tick", ex); }
        }

        public static void RenderPostfix()
        {
            try { Menu.Render(); } catch (Exception ex) { Mod.LogOnce("menu-render", ex); }
        }

        private static bool Capturing(ParisInputType t) => Menu.Open && t == ParisInputType.Keyboard;

        public static bool BlockBoolPrefix(ParisInputType controllerType, ref bool __result)
        {
            if (!Capturing(controllerType)) return true;
            __result = false; return false;
        }

        public static bool BlockDirPrefix(ParisInputType controllerType, ref Vector2 __result)
        {
            if (!Capturing(controllerType)) return true;
            __result = Vector2.Zero; return false;
        }

        public static bool BlockDirFloatPrefix(ParisInputType controllerType, ref float __result)
        {
            if (!Capturing(controllerType)) return true;
            __result = 0f; return false;
        }

        public static bool BlockStartPrefix(ref ParisInputType __result, ref ulong handle)
        {
            if (!Menu.Open) return true;
            handle = 0; __result = ParisInputType.None; return false;
        }
    }
}
