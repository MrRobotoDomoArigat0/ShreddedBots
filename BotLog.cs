using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Paris.Engine.Scene;

namespace ShreddedBots
{
    public static class BotLog
    {
        public const int Human = Config.MaxBots;
        public static bool On => false;
        public static void Tick(float dt) { }
        public static void SetLocked(int bot, string[] moves) { }
        public static int AttackIndex(string s) { return 0; }
        public static void Observe(int slot, bool isHuman, string state, int totalHits, float z, float dt, float now, bool onGround) { }
        [Conditional("SB_DEBUG")] public static void Move(int bot, string move, string why) { }
        [Conditional("SB_DEBUG")] public static void Evt(int bot, string kind, string detail, float gap = 1.0f) { }
        [Conditional("SB_DEBUG")] public static void Result(int bot, string move, bool ok) { }
        [Conditional("SB_DEBUG")] public static void Note(int bot, string key, string text, float gap = 2.0f) { }
        [Conditional("SB_DEBUG")] public static void Boss(string kind, string detail, float gap = 0f) { }
        [Conditional("SB_DEBUG")] public static void Tally(int bot, string key) { }
        [Conditional("SB_DEBUG")] public static void Add(int bot, string key, float v) { }
        [Conditional("SB_DEBUG")] public static void StageEnd(int bot, string why) { }
        [Conditional("SB_DEBUG")] public static void AiTime(int bot, long ticks) { }
    }
}
