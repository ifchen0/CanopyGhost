using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Caches per pawn (or corpse) whether a tree drawn above pawns overlaps its cell.
    /// Rechecked when the pawn changes cell or after a short interval (trees grow or get cut).
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
        // Tree textures have empty margins; shrink the quad so bare corners do not count.
        private const float QuadShrink = 0.8f;
        private const float PawnHalfWidth = 0.35f;
        private const float PawnHalfHeight = 0.45f;
        private const float PawnCenterRaise = 0.2f;

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        /// <param name="anchor">The spawned thing standing for the pawn: the pawn itself, or its corpse.</param>
        public static bool IsUnderCanopy(Thing anchor)
        {
            IntVec3 cell = anchor.Position;
            int frame = Time.frameCount;
            if (entries.TryGetValue(anchor.thingIDNumber, out var e) && e.cell == cell && frame - e.frame < RefreshFrames)
                return e.covered;
            if (entries.Count > 4096)
                entries.Clear();
            bool covered = Scan(anchor.Map, cell);
            entries[anchor.thingIDNumber] = new Entry { cell = cell, frame = frame, covered = covered };
            return covered;
        }

        private static bool Scan(Map map, IntVec3 cell)
        {
            float pMinX = cell.x + 0.5f - PawnHalfWidth;
            float pMaxX = cell.x + 0.5f + PawnHalfWidth;
            float pCenterZ = cell.z + 0.5f + PawnCenterRaise;
            float pMinZ = pCenterZ - PawnHalfHeight;
            float pMaxZ = pCenterZ + PawnHalfHeight;

            for (int z = cell.z - ScanBelow; z <= cell.z + ScanAbove; z++)
            {
                for (int x = cell.x - ScanX; x <= cell.x + ScanX; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map))
                        continue;
                    var things = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (!(things[i] is Plant plant) || !IsCanopyTree(plant.def))
                            continue;
                        TreeQuad(plant, out float cx, out float cz, out float half);
                        if (cx - half < pMaxX && cx + half > pMinX && cz - half < pMaxZ && cz + half > pMinZ)
                            return true;
                    }
                }
            }
            return false;
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

        /// <summary>Trees that some mod (e.g. Better Trees) draws above pawns.</summary>
        public static bool IsCanopyTree(ThingDef def)
        {
            return def.plant != null && def.plant.IsTree && def.plant.maxMeshCount == 1
                && def.graphicData != null && def.altitudeLayer > AltitudeLayer.Pawn;
        }
    }
}
