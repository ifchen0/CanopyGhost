using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Caches per pawn whether a canopy tree overlaps the area it is drawn in (body, at least a humanlike's
    /// size, plus room for a held weapon or carried thing). Rechecked when the pawn changes cell or after a short
    /// interval (trees grow or get cut).
    /// </summary>
    public static class CanopyCache
    {
        private struct Entry
        {
            public IntVec3 cell;
            public int frame;
            public bool covered;
        }

        private const int RefreshFrames = 120;
        private const int ScanX = 4;
        private const int ScanBelow = 6;
        private const int ScanAbove = 1;
        internal const float PawnCenterRaise = 0.2f;
        private const float MinPawnDrawSize = 1.5f;
        private const float HeldMargin = 0.4f;
        // Tree textures have empty margins; TreeQuad leaves them out.
        private const float QuadShrink = 0.8f;

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        internal static void Clear() => entries.Clear();

        public static bool UnderCanopy(Pawn pawn)
        {
            IntVec3 cell = pawn.Position;
            int frame = Time.frameCount;
            if (entries.TryGetValue(pawn.thingIDNumber, out var e) && e.cell == cell && frame - e.frame < RefreshFrames)
                return e.covered;
            if (entries.Count > 4096)
                entries.Clear();
            bool covered = Scan(pawn, cell);
            entries[pawn.thingIDNumber] = new Entry { cell = cell, frame = frame, covered = covered };
            return covered;
        }

        /// <summary>Half the side of the square a pawn is drawn in, with room for a held or carried thing.</summary>
        internal static float DrawHalfSize(Pawn pawn)
        {
            Vector2 size = pawn.Drawer?.renderer?.BodyGraphic?.drawSize ?? Vector2.one;
            return (Mathf.Max(MinPawnDrawSize, size.x, size.y) + HeldMargin) * 0.5f;
        }

        private static bool Scan(Pawn pawn, IntVec3 cell)
        {
            float half = DrawHalfSize(pawn);
            float px = cell.x + 0.5f;
            float pz = cell.z + 0.5f + PawnCenterRaise;
            Map map = pawn.Map;
            int reach = Mathf.CeilToInt(half);
            for (int z = cell.z - ScanBelow - reach; z <= cell.z + ScanAbove + reach; z++)
            {
                for (int x = cell.x - ScanX - reach; x <= cell.x + ScanX + reach; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map))
                        continue;
                    var things = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (!(things[i] is Plant plant) || !IsCanopyTree(plant.def))
                            continue;
                        FullQuad(plant, out float cx, out float cz, out float th);
                        if (cx - th < px + half && cx + th > px - half && cz - th < pz + half && cz + th > pz - half)
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>The whole printed square of a canopy tree, texture margins included.</summary>
        public static void FullQuad(Plant plant, out float cx, out float cz, out float half)
        {
            TreeQuad(plant, out cx, out cz, out half);
            half /= QuadShrink;
        }

        /// <summary>
        /// Visible canopy square of a tree, mirroring Plant.Print for single-mesh plants
        /// (bottom edge sits on the tree's cell), shrunk for the empty texture margins.
        /// </summary>
        public static void TreeQuad(Plant plant, out float cx, out float cz, out float half)
        {
            IntVec3 pos = plant.Position;
            float visual = plant.def.plant.visualSizeRange.LerpThroughRange(plant.Growth);
            half = plant.def.graphicData.drawSize.x * visual * 0.5f * QuadShrink;
            cx = pos.x + 0.5f;
            cz = Mathf.Max(pos.z + 0.5f, pos.z + visual * 0.5f);
        }

        /// <summary>Trees that some mod (e.g. Better Trees) drew above pawns, now under TreeOverlay.</summary>
        public static bool IsCanopyTree(ThingDef def) => TreeOverlay.IsCanopyTree(def);
    }
}
