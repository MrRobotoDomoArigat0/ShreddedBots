using System;
using Microsoft.Xna.Framework;
using Paris.Engine.Graphics.Playfield;
using Paris.Engine.Physic;

namespace ShreddedBots
{
    /// <summary>M4.6: cheap pit / gap queries on the level's own height map (PlayfieldFlag.PIT tiles, the ones the game drops a player through). Pure functions take a probe delegate so tests can check them without a scene.</summary>
    internal static class PitMap
    {
        // ---- the real probe
        private static Playfield _pf; private static bool _has; private static int _count, _total; private static string _first = "";
        internal static int PitTiles { get { return _count; } }
        internal static string Summary { get { return _count + " pit tiles of " + _total + ", first " + _first; } }

        /// <summary>The scene's playfield when it has at least one pit tile (so levels without pits cost nothing), else null.</summary>
        internal static Playfield Field(Playfield pf)
        {
            if (pf == null) return null;
            if (!ReferenceEquals(pf, _pf)) Index(pf);
            return _has ? pf : null;
        }
        internal static bool NewField(Playfield pf) { return pf != null && !ReferenceEquals(pf, _pf); }

        private static void Index(Playfield pf)
        {
            _pf = pf; _has = false; _count = 0; _total = 0; _first = "";
            try
            {
                if (!pf.Is3D || pf.Heightmap == null || pf.CollisionLayer == null) return;
                int ts = pf.CollisionLayer.TileScale;
                HeightmapInfo[][] hm = pf.Heightmap;
                for (int y = 0; y < hm.Length; y++)
                {
                    HeightmapInfo[] row = hm[y]; if (row == null) continue;
                    for (int x = 0; x < row.Length; x++)
                    {
                        _total++;
                        HeightmapInfo h = row[x]; if (h == null) continue;
                        if (IsPitInfo(h)) { if (_count == 0) _first = "tile " + x + "," + y + " (tile size " + ts + ")"; _count++; }
                    }
                }
                _has = _count > 0;
            }
            catch { _has = false; }
        }

        internal static bool IsPitInfo(HeightmapInfo h) { return (h.Flag & PlayfieldFlag.PIT) != 0 || h.Height <= -100; }

        internal static bool IsPit(Playfield pf, float x, float y)
        {
            try { return IsPitInfo(pf.GetHeightAtPos(new Vector2(x, y))); } catch { return false; }
        }

        // ---- pure helpers
        /// <summary>M4.8: double jump for a gap? From 75 % of the single-jump reach estimate (the estimate is optimistic: Episode 13 a single jump "reaching 95" missed a 92 px gap), always when forced (after a miss). Heroes without a double jump keep the old rule (single estimate exceeded).</summary>
        internal static bool UseDouble(float gapWidth, float singleReach, bool canDouble, bool forced)
        {
            if (!canDouble) return gapWidth > singleReach;
            return forced || gapWidth > singleReach * 0.75f;
        }

        /// <summary>M4.8: stall watchdog due? Blocked at one spot since <paramref name="since"/> (last seen blocked at <paramref name="seen"/>), over 6 s, still blocked a moment ago, and not re-planned in the last 12 s.</summary>
        internal static bool StallDue(float now, float since, float seen, float nextOk)
        {
            return since >= 0f && now - seen <= 0.6f && now - since > 6f && now >= nextOk;
        }

        /// <summary>Walk from (x, y) along (dx, dy) in steps. Found = a pit starts within lookStart px; start = the last safe distance before it; width = distance from start to the first safe sample after the pit (+Infinity: still pit after maxWidth).</summary>
        internal static bool Scan(Func<float, float, bool> pit, float x, float y, float dx, float dy, float lookStart, float step, float maxWidth, out float start, out float width)
        {
            start = 0f; width = 0f;
            if (pit(x, y)) return false;   // already over one: nothing to look ahead for
            float d = step;
            for (; d <= lookStart + step; d += step)
            {
                if (pit(x + dx * d, y + dy * d)) break;
            }
            if (d > lookStart + step) return false;
            start = d - step;
            float lim = start + maxWidth;
            for (float e = d; e <= lim + step; e += step)
            {
                if (!pit(x + dx * e, y + dy * e)) { width = e - start; return true; }
            }
            width = float.PositiveInfinity;
            return true;
        }

        /// <summary>Look at the neighbouring lanes (same x, other y): is there one with a narrower gap (or none) to the far side? Only lanes reachable without crossing a pit sideways. Returns the best lane's gap width (0 = no pit at all) and its y.</summary>
        internal static float BestLane(Func<float, float, bool> pit, float x, float y, float sx, float reach, float curWidth, out float laneY)
        {
            laneY = y; float best = curWidth, bestDy = 0f;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                for (int k = 1; k <= 12; k++)
                {
                    float dy = sgn * 8f * k;
                    if (pit(x, y + dy)) break;   // the way sideways is cut
                    float st, w;
                    float lw;
                    if (!Scan(pit, x, y + dy, sx, 0f, reach, 4f, 260f, out st, out w)) lw = 0f; else lw = w;
                    if (lw < best - 0.5f || (Math.Abs(lw - best) < 0.5f && lw < curWidth && Math.Abs(dy) < Math.Abs(bestDy)))
                    { best = lw; bestDy = dy; }
                }
            }
            laneY = y + bestDy;
            return best;
        }

        /// <summary>M4.7: BestLane for slanted gaps (Episode 13). Differences from BestLane: (1) a lane may be reached by first backing away from the edge along the bot's own row (up to 80 px) because on a diagonal edge the same x is already pit on the other rows; (2) a lane with no pit at all only counts when it is within freeMaxDy px (a pit-free strip far away is usually the back wall, not floor); (3) a lane whose floor height differs by more than 16 from here is cut (wall top / ledge). Returns the best gap width (0 = none) and the lane point.</summary>
        internal static float BestLane2(Func<float, float, bool> pit, Func<float, float, float> hgt, float x, float y, float sx, float reach, float curWidth, float freeMaxDy, out float laneX, out float laneY)
        {
            laneX = x; laneY = y; float best = curWidth, bestCost = 1e9f;
            float h0 = hgt != null ? hgt(x, y) : 0f;
            for (int j = 0; j <= 10; j += 2)
            {
                float xr = x - sx * 8f * j;
                if (j > 0)
                {
                    bool clear = true;
                    for (int r = 1; r <= j && clear; r++) if (pit(x - sx * 8f * r, y)) clear = false;
                    if (!clear) break;
                }
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    for (int k = 1; k <= 12; k++)
                    {
                        float dy = sgn * 8f * k;
                        if (pit(xr, y + dy)) break;
                        if (hgt != null && Math.Abs(hgt(xr, y + dy) - h0) > 16f) break;
                        float st, w, lw;
                        if (!Scan(pit, xr, y + dy, sx, 0f, reach + 8f * j, 4f, 260f, out st, out w)) { lw = 0f; if (Math.Abs(dy) > freeMaxDy) continue; } else lw = w;
                        float cost = Math.Abs(dy) + 8f * j;
                        if (lw < best - 0.5f || (Math.Abs(lw - best) < 0.5f && lw < curWidth && cost < bestCost))
                        { best = lw; bestCost = cost; laneX = xr; laneY = y + dy; }
                    }
                }
            }
            return best;
        }
    }
}
