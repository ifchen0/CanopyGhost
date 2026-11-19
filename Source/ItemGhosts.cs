using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Ghosts for items lying under canopies. Items are baked into the map mesh, so there is no per-frame draw
    /// to hook: this component keeps a grid of covered cells (rebuilt from the canopy trees every few seconds)
    /// and each frame draws a translucent copy of the items on covered cells in view, above the trees.
    /// </summary>
    public class ItemGhosts : MapComponent
    {
        private const int RebuildFrames = 600;
        private const float ItemHalf = 0.4f;

        private bool[] covered;
        private int builtFrame = -RebuildFrames;
        private static List<ThingDef> canopyDefs;
        private static int canopyDefsFrame = -RebuildFrames;

        public ItemGhosts(Map map) : base(map)
        {
        }

        // Between the trees and the pawn ghosts, so a pawn ghost stays on top of an item ghost.
        private static float Altitude =>
            (AltitudeLayer.PawnUnused.AltitudeFor() + AltitudeLayer.PawnState.AltitudeFor()) * 0.5f;

        public override void MapComponentUpdate()
        {
            var settings = CanopyGhostMod.Settings;
            if (!settings.itemGhosts || settings.alpha <= 0f || map != Find.CurrentMap || WorldRendererUtility.WorldSelected)
                return;
            int frame = Time.frameCount;
            if (covered == null || frame - builtFrame >= RebuildFrames)
            {
                Rebuild();
                builtFrame = frame;
            }

            CellRect view = Find.CameraDriver.CurrentViewRect.ClipInsideMap(map);
            CellIndices indices = map.cellIndices;
            float y = Altitude;
            foreach (IntVec3 c in view)
            {
                int index = indices.CellToIndex(c);
                // Only loose items on the ground; items on shelves, racks and other buildings are skipped.
                if (!covered[index] || map.fogGrid.IsFogged(c) || map.edificeGrid[index] != null)
                    continue;
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t.def.category == ThingCategory.Item && !(t is Corpse) && t.def.drawerType != DrawerType.None)
                        DrawGhost(t, y, settings.alpha);
                }
            }
        }

        // Same geometry as Graphic.Print: size, flip, rotation and offset of the baked item.
        private static void DrawGhost(Thing t, float y, float alpha)
        {
            Graphic g = t.Graphic;
            if (g == null)
                return;
            float extraRotation = 0f;
            // Weapons and other items with onGroundRandomRotateAngle: the wrapper only adds an angle
            // and prints its inner graphic, so use the inner graphic's geometry plus that angle.
            if (g is Graphic_RandomRotated rr)
            {
                extraRotation = RandomRotation(t, RandomMaxAngle(rr));
                g = rr.SubGraphic;
                if (g == null)
                    return;
            }
            Rot4 rot = t.Rotation;
            Vector2 size;
            bool flip;
            if (g.ShouldDrawRotated)
            {
                size = g.drawSize;
                flip = false;
            }
            else
            {
                size = rot.IsHorizontal ? g.drawSize.Rotated() : g.drawSize;
                flip = (rot == Rot4.West && g.WestFlipped) || (rot == Rot4.East && g.EastFlipped);
            }
            if (t.MultipleItemsPerCellDrawn())
                size *= 0.8f;
            Quaternion q = g.QuatFromRot(rot);
            float angle = extraRotation + (flip && g.data != null ? g.data.flipExtraRotation : 0f);
            if (angle != 0f)
                q *= Quaternion.AngleAxis(angle, Vector3.up);
            Material src = g.MatAt(rot, t);
            Texture tex = src?.mainTexture;
            if (tex == null)
                return;
            Color tint = src.color;
            tint.a = alpha;
            Material mat = MaterialPool.MatFrom(new MaterialRequest(tex, ShaderDatabase.Mote, tint));
            Vector3 pos = t.TrueCenter() + g.DrawOffset(rot);
            pos.y = y;
            Graphics.DrawMesh(MeshPool.GridPlane(size, flip), pos, q, mat, 0);
        }

        private static readonly AccessTools.FieldRef<Graphic_RandomRotated, float> RandomMaxAngle =
            AccessTools.FieldRefAccess<Graphic_RandomRotated, float>("maxAngle");

        // Mirrors Graphic_RandomRotated.Print for items on the ground (its rack angle needs a building on
        // the cell, and those cells are skipped): a per-thing angle from its id.
        private static float RandomRotation(Thing t, float maxAngle)
        {
            return -maxAngle + (float)(t.thingIDNumber * 542) % (maxAngle * 2f);
        }

        private void Rebuild()
        {
            int n = map.cellIndices.NumGridCells;
            if (covered == null || covered.Length != n)
                covered = new bool[n];
            else
                System.Array.Clear(covered, 0, n);

            int frame = Time.frameCount;
            // Better Trees can change tree altitudes from its settings, so refresh the def list too.
            if (canopyDefs == null || frame - canopyDefsFrame >= RebuildFrames)
            {
                canopyDefs = DefDatabase<ThingDef>.AllDefsListForReading.Where(CanopyCache.IsCanopyTree).ToList();
                canopyDefsFrame = frame;
            }

            foreach (ThingDef def in canopyDefs)
            {
                foreach (Thing thing in map.listerThings.ThingsOfDef(def))
                {
                    if (!(thing is Plant plant))
                        continue;
                    CanopyCache.TreeQuad(plant, out float cx, out float cz, out float half);
                    // Cells whose central item area overlaps the canopy square.
                    int minX = Mathf.FloorToInt(cx - half - ItemHalf - 0.5f) + 1;
                    int maxX = Mathf.CeilToInt(cx + half + ItemHalf - 0.5f) - 1;
                    int minZ = Mathf.FloorToInt(cz - half - ItemHalf - 0.5f) + 1;
                    int maxZ = Mathf.CeilToInt(cz + half + ItemHalf - 0.5f) - 1;
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            var c = new IntVec3(x, 0, z);
                            if (c.InBounds(map))
                                covered[map.cellIndices.CellToIndex(c)] = true;
                        }
                    }
                }
            }
        }
    }
}
