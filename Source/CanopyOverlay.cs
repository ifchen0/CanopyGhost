using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Decides each frame whether the canopy picture (TreeOverlay) is needed: only while a spawned pawn in view
    /// stands under a canopy tree (checked every frame through CanopyCache, so a pawn walking under a tree is
    /// covered at once), or an item or corpse lies under a canopy tree in view (checked a slice of trees per
    /// frame, every tree about once a second, as items seldom move). Otherwise the trees below pawns look the
    /// same as with the picture, and nothing is drawn.
    /// </summary>
    public class CanopyOverlay : MapComponent
    {
        private const int ItemCycleFrames = 60;
        private const int TreeListFrames = 600;
        // Tall canopies reach well above their cell.
        private const int ViewMargin = 8;

        private readonly HashSet<Plant> overItems = new HashSet<Plant>();
        private readonly List<Plant> trees = new List<Plant>();
        private int treesFrame = -TreeListFrames;
        private int next;

        public CanopyOverlay(Map map) : base(map)
        {
        }

        public override void MapComponentUpdate()
        {
            if (CanopyGhostMod.Settings.alpha >= 1f || map != Find.CurrentMap || WorldRendererUtility.WorldSelected)
                return;
            int frame = Time.frameCount;
            if (frame - treesFrame >= TreeListFrames)
                RefreshTreeList(frame);
            CheckItems();
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(ViewMargin);
            if (AnythingUnderCanopy(view))
                TreeOverlay.DrawInView(map, view);
        }

        private bool AnythingUnderCanopy(CellRect view)
        {
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (view.Contains(p.Position) && CanopyCache.UnderCanopy(p))
                    return true;
            }
            foreach (Plant t in overItems)
            {
                if (view.Contains(t.Position))
                    return true;
            }
            return false;
        }

        private void RefreshTreeList(int frame)
        {
            treesFrame = frame;
            trees.Clear();
            foreach (ThingDef def in TreeOverlay.CanopyDefs)
            {
                foreach (Thing t in map.listerThings.ThingsOfDef(def))
                {
                    if (t is Plant plant)
                        trees.Add(plant);
                }
            }
            overItems.RemoveWhere(t => !t.Spawned || t.Map != map);
            TreeOverlay.ForgetDespawned();
            next = 0;
        }

        // A slice of the trees per frame: is an item or corpse lying under the canopy?
        private void CheckItems()
        {
            if (trees.Count == 0)
                return;
            int count = Mathf.Max(1, Mathf.CeilToInt(trees.Count / (float)ItemCycleFrames));
            for (int k = 0; k < count; k++)
            {
                if (next >= trees.Count)
                    next = 0;
                Plant tree = trees[next++];
                if (!tree.Spawned || tree.Map != map)
                {
                    overItems.Remove(tree);
                    continue;
                }
                if (HasItemUnder(tree))
                    overItems.Add(tree);
                else
                    overItems.Remove(tree);
            }
        }

        private bool HasItemUnder(Plant tree)
        {
            CanopyCache.FullQuad(tree, out float cx, out float cz, out float half);
            // Items are drawn about a cell wide around their cell centre.
            int minX = Mathf.FloorToInt(cx - half - 0.5f);
            int maxX = Mathf.FloorToInt(cx + half + 0.5f) - 1;
            int minZ = Mathf.FloorToInt(cz - half - 0.5f);
            int maxZ = Mathf.FloorToInt(cz + half + 0.5f) - 1;
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map))
                        continue;
                    List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (things[i].def.category == ThingCategory.Item)
                            return true;
                    }
                }
            }
            return false;
        }
    }
}
