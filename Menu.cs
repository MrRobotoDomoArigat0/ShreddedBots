using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Paris.Engine.Data;
using Paris.Engine.Graphics;
using Paris.System.Input;

namespace ShreddedBots
{
    /// <summary>F7 overlay menu stub (keyboard: Up/Down, Left/Right/Enter to change, Esc/Backspace/F7 to close). Bot toggles and settings.</summary>
    public static class Menu
    {
        public static bool Open;
        private static int _row;
        private static Keys _key = Keys.F7;
        private static string _toast = ""; private static float _toastTime;
        private static bool _ranRuntimeCheck;

        private enum RowKind { Info, Close, BotsHeader, Difficulty, Aggression, BotSlot, BotHero, BotColor,
        }
        private struct Row { public RowKind Kind; public int Bot; }
        private static readonly List<Row> Rows = new List<Row>();

        static Menu()
        {
            BuildRows();
            _row = Rows.FindIndex(Selectable);
        }

        private static void BuildRows()
        {
            Rows.Clear();
            if (SelfCheck.Problems) Rows.Add(new Row { Kind = RowKind.Info, Bot = 0 });   // M4.11: the hook summary only shows when a hook FAILED (the check itself always runs)
            Rows.Add(new Row { Kind = RowKind.BotsHeader });
            Rows.Add(new Row { Kind = RowKind.Difficulty });   // M4.12: replaces the "Bots: ON" and "BOTS count" rows; every slot has its own On / Off
            Rows.Add(new Row { Kind = RowKind.Aggression });   // 0.4.16: Low / Normal / High (Normal = 0.4.15 engage)
            for (int i = 0; i < Config.MaxBots; i++)
            {
                Rows.Add(new Row { Kind = RowKind.BotSlot, Bot = i });
                Rows.Add(new Row { Kind = RowKind.BotHero, Bot = i });
                Rows.Add(new Row { Kind = RowKind.BotColor, Bot = i });
            }
            Rows.Add(new Row { Kind = RowKind.Close });
            if (_row >= Rows.Count) _row = Rows.Count - 1;
        }

        /// <summary>Menu-safe text: printable ASCII only (the debug font has no accents).</summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c >= 32 && c < 127 ? c : '?');
            return sb.ToString();
        }

        public static void InitKey()
        {
            if (!Enum.TryParse(Config.MenuKey, true, out _key)) _key = Keys.F7;
            Log.Info("Menu key: " + _key);
        }

        private static bool Pressed(Keys k)
        {
            var im = InputManagerBase.Singleton;
            return im != null && im.IsKeyJustPressed(k);
        }

        public static void Toast(string s) { _toast = s; _toastTime = 3f; Log.Info("[menu] " + s); }

        public static void Tick(float dt)
        {
            if (!_ranRuntimeCheck) { _ranRuntimeCheck = true; OnlineBlock.RuntimeCheck(); }
            if (_toastTime > 0f) _toastTime -= dt;
            if (Pressed(_key)) { Open = !Open; if (Open) OnOpen(); return; }
            if (!Open) return;
            if (Pressed(Keys.Escape) || Pressed(Keys.Back)) { Open = false; return; }
            if (Pressed(Keys.Up)) Move(-1);
            if (Pressed(Keys.Down)) Move(1);
            if (Pressed(Keys.Left)) Change(-1, false);
            if (Pressed(Keys.Right)) Change(1, false);
            if (Pressed(Keys.Enter) || Pressed(Keys.Space)) Change(1, true);
        }

        /// <summary>M4.11: rebuilt on every open (the hook warning row depends on the self-check, which runs after this class is created); the info the old grey lines showed goes to the dev log.</summary>
        private static void OnOpen()
        {
            BuildRows();
            if (_row < 0 || _row >= Rows.Count || !Selectable(Rows[_row])) _row = Rows.FindIndex(Selectable);
        }

        private static bool Selectable(Row r)
        {
            switch (r.Kind)
            {
                case RowKind.Info: return false;
                case RowKind.BotsHeader: return false;
                default: return true;
            }
        }

        private static void Move(int d)
        {
            int n = Rows.Count;
            for (int i = 0; i < n; i++)
            {
                _row = (_row + d + n) % n;
                if (Selectable(Rows[_row])) return;
            }
        }

        private static void Change(int d, bool enter)
        {
            Row r = Rows[_row];
            switch (r.Kind)
            {
                case RowKind.Close:
                    if (enter) Open = false;
                    break;
                case RowKind.Difficulty:
                    Config.BotDifficulty = CycleDifficulty(Config.BotDifficulty, d); Config.Save();
                    Toast("Bot skill: " + Config.DifficultyName); break;
                case RowKind.Aggression:
                    Config.BotAggression = CycleAggression(Config.BotAggression, d); Config.Save();
                    Toast("Aggression: " + Config.AggressionName); break;
                case RowKind.BotSlot:
                    Config.BotOn[r.Bot] = !Config.BotOn[r.Bot]; Config.Save();
                    Toast(SlotName(r.Bot) + ": " + (Config.BotOn[r.Bot] ? "ON" : "OFF")); break;
                case RowKind.BotHero:
                    CycleHero(r.Bot, d >= 0 ? 1 : -1); break;
                case RowKind.BotColor:
                    CycleColor(r.Bot, d >= 0 ? 1 : -1); break;
            }
        }

        // ------------------------------------------------------------------ bot rows
        /// <summary>M4.12: bot slot k plays as player k + 2 (the human is P1): "P2 Bot" .. "P6 Bot".</summary>
        internal static string SlotName(int bot) { return "P" + (bot + 2) + " Bot"; }

        /// <summary>M4.12: Easy (0) / Normal (1) / Hard (2); Left goes down, Right / Enter goes up, wrapping around.</summary>
        internal static int CycleDifficulty(int cur, int d)
        {
            int n = cur + (d >= 0 ? 1 : -1);
            if (n < 0) n = 2; if (n > 2) n = 0;
            return n;
        }
        internal static int CycleAggression(int cur, int d)
        {
            int n = cur + (d >= 0 ? 1 : -1);
            if (n < 0) n = 2; if (n > 2) n = 0;
            return n;
        }

        /// <summary>M4.4: may this hero be offered to bot slot <paramref name="bot"/>? Owned / unlocked, not the human's hero, not already picked by another slot.</summary>
        private static bool Offerable(int idx, int bot)
        {
            if (!BotManager.HeroOwned(idx)) return false;
            if (BotManager.SameCharacterPicksAllowed()) return true;
            if (idx == BotManager.HumanHero()) return false;
            for (int j = 0; j < Config.MaxBots; j++) if (j != bot && Config.BotOn[j] && BotManager.IndexOf(Config.BotHero[j]) == idx) return false;
            return true;
        }

        private static void CycleHero(int bot, int dir)
        {
            var list = Paris.Game.System.CharacterManager.Singleton?.Characters;
            if (list == null || list.Count == 0) { Toast("Character list not loaded yet"); return; }
            // options: Auto, then every hero that can really be picked right now (owned / unlocked, not yours, not another slot's)
            var opts = new List<int> { -1 };
            for (int i = 0; i < list.Count; i++) if (list[i] != null && Offerable(i, bot)) opts.Add(i);
            int pos = opts.IndexOf(BotManager.IndexOf(Config.BotHero[bot]));   // -1 (Auto) is opts[0]; a stale / invalid pick counts as Auto
            if (pos < 0) pos = 0;
            pos = ((pos + dir) % opts.Count + opts.Count) % opts.Count;
            Config.BotHero[bot] = opts[pos] < 0 ? "" : list[opts[pos]].InternalName;
            Config.BotPalette[bot] = 0;   // a different hero: back to the first palette
            Config.Save();
            if (!BotManager.PreviewHero(bot)) BotManager.RequestRequeue(bot);   // on the select screen: lock the bot in and show it now
            if (!BotManager.InCharSelect && BotManager.GetBot(bot) != null) Toast("Character change applies the next time the bot joins (character select)");
        }

        private static void CycleColor(int bot, int dir)
        {
            int v = Config.BotPalette[bot];   // 0 = first
            v = v <= 1 ? 1 : v;
            int idx = Config.BotPalette[bot] <= 0 ? 0 : v;   // 0 first, 2.. palettes
            idx += dir; if (idx == 1) idx += dir;
            if (idx < 0) idx = 8; if (idx > 8) idx = 0;
            Config.BotPalette[bot] = idx;
            Config.Save();
            if (!BotManager.PreviewColor(bot)) BotManager.RequestRequeue(bot);   // on the select screen: lock the bot in and show the color now
            if (!BotManager.InCharSelect && BotManager.GetBot(bot) != null) Toast("Color change applies the next time the bot joins (character select)");
        }

        private static string HeroLabel(int bot)
        {
            string n = Config.BotHero[bot];
            if (string.IsNullOrEmpty(n)) return "Auto (a free character)";
            int idx = BotManager.IndexOf(n);
            if (idx < 0) return Sanitize(n) + " (not found: auto)";
            string name = BotManager.HeroName(idx);
            if (!BotManager.HeroOwned(idx)) return name + " (locked / not owned: auto)";
            if (!BotManager.SameCharacterPicksAllowed())
            {
                if (idx == BotManager.HumanHero()) return name + " (yours: auto)";
                for (int j = 0; j < bot; j++) if (Config.BotOn[j] && BotManager.IndexOf(Config.BotHero[j]) == idx) return name + " (" + SlotName(j) + " has it: auto)";
            }
            return name;
        }

        // ------------------------------------------------------------------ drawing
        private static void Text(Renderer r, int font, Vector2 pos, string s, Color c, float scale)
        {
            r.DrawText(font, pos, s, c, Color.Black, scale, 0f);
        }

        public static void Render()
        {
            Renderer r = Renderer.Singleton;
            if (r == null || EngineSettings.Singleton == null) return;
            Vector2 res = EngineSettings.Singleton.GameResolution;
            float scale = Math.Max(1f, (float)Math.Floor(res.Y / 180f));
            if (Open) scale = Math.Max(1f, Math.Min(scale, res.Y / ((Rows.Count + 5) * 10f + 12f)));   // long lists (debug build) must still fit on screen
            int font = r.DebugFontID;
            float lh = 10f * scale;

            if (!Open)
            {
                float y = res.Y - lh * 3f;
                if (_toastTime > 0f) Text(r, font, new Vector2(4 * scale, y - lh), _toast, Color.Yellow, scale);
                if (Config.ShowStatusHud)
                {
                    Text(r, font, new Vector2(4 * scale, y), "ShreddedBots " + Loader.BuildTag + ": " + SelfCheck.Summary, Color.LightGreen, scale);
                }
                return;
            }

            float w = Math.Min(res.X - 8f * scale, 300f * scale), h = (Rows.Count + 5) * lh + 6f * scale;
            Vector2 o = new Vector2(4f * scale, 4f * scale);
            r.DrawBox(new Rectangle((int)o.X, (int)o.Y, (int)w, (int)h), new Color(0, 0, 0, 215));
            r.DrawRectangle(new Rectangle((int)o.X, (int)o.Y, (int)w, (int)h), new Color(255, 200, 60));
            Vector2 p = o + new Vector2(5f * scale, 3f * scale);
            Text(r, font, p, "SHREDDED BOTS  (" + _key + "/Esc close, arrows, Enter)", new Color(255, 200, 60), scale); p.Y += lh;
            Text(r, font, p, "by MrRobotoDomoArigato", new Color(180, 180, 180), scale); p.Y += lh * 1.2f;
            for (int i = 0; i < Rows.Count; i++)
            {
                Row row = Rows[i];
                bool sel = i == _row;
                Color c = row.Kind == RowKind.Info ? Color.OrangeRed : !Selectable(row) ? Color.Gray : sel ? Color.Yellow : Color.White;
                Text(r, font, p, (sel ? "> " : "  ") + RowText(i, row), c, scale);
                p.Y += lh;
            }
            p.Y += lh * 0.3f;
            if (_toastTime > 0f) Text(r, font, p, _toast, Color.Yellow, scale);
        }

        private static string RowText(int index, Row row)
        {
            switch (row.Kind)
            {
                case RowKind.Info:
                    return "WARNING: " + SelfCheck.Summary + " (see ShreddedBots.log)";
                case RowKind.BotsHeader: return "--- BOTS ---" + (BotManager.HooksOk ? "" : "  (unavailable: see log)");
                case RowKind.Difficulty: return "Bot skill: " + Config.DifficultyName;
                case RowKind.Aggression: return "Aggression: " + Config.AggressionName;
                case RowKind.BotSlot: return SlotName(row.Bot) + ": " + (Config.BotOn[row.Bot] ? "ON" : "OFF");
                case RowKind.BotHero: return SlotName(row.Bot) + ": " + HeroLabel(row.Bot);   // M4.12: the character pick (no "hero" word in the menu)
                case RowKind.BotColor: return SlotName(row.Bot) + " color: " + (Config.BotPalette[row.Bot] <= 1 ? "First (default)" : "Color " + Config.BotPalette[row.Bot]);
                default: return "Close";
            }
        }

    }
}
