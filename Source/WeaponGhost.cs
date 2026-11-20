using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Ghost of the weapon in a pawn's hands (aimed or carried openly), drawn with the pawn's ghost. Vanilla draws
    /// the weapon inside RenderPawnAt at the pawn's own altitude, under the canopy; this repeats that draw with the
    /// ghost material at the ghost altitude.
    /// Same geometry as PawnRenderUtility.DrawEquipmentAiming (mesh flip, angle offset, recoil); mods that change
    /// how weapons are drawn are not followed. Items carried in hand are not drawn.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    public static class WeaponGhost
    {
        public static void Postfix(Thing eq, Vector3 drawLoc, float aimAngle)
        {
            PawnRenderer renderer = GhostDrawer.Drawing;
            if (renderer == null || !(eq.ParentHolder is Pawn_EquipmentTracker tracker))
                return;
            Pawn pawn = tracker.pawn;
            // Only the weapon of the pawn being rendered, and only where its body gets a ghost too.
            if (pawn == null || pawn.Drawer?.renderer != renderer || !pawn.Spawned || pawn.Map != Find.CurrentMap)
                return;
            if (pawn.RaceProps.Humanlike ? !GhostDrawer.AtlasSafe(renderer, pawn) : !CanopyGhostMod.Settings.includeAnimals)
                return;
            if (!CanopyCache.IsUnderCanopy(pawn))
                return;

            float angle = aimAngle - 90f;
            Mesh mesh;
            if (aimAngle > 200f && aimAngle < 340f)
            {
                mesh = MeshPool.plane10Flip;
                angle -= 180f;
                angle -= eq.def.equippedAngleOffset;
            }
            else
            {
                mesh = MeshPool.plane10;
                angle += eq.def.equippedAngleOffset;
            }
            angle %= 360f;
            CompEquippable equippable = eq.TryGetComp<CompEquippable>();
            if (equippable != null)
            {
                EquipmentUtility.Recoil(eq.def, EquipmentUtility.GetRecoilVerb(equippable.AllVerbs), out Vector3 drawOffset,
                    out float angleOffset, aimAngle);
                drawLoc += drawOffset;
                angle += angleOffset;
            }
            Material src = eq.Graphic is Graphic_StackCount stackCount
                ? stackCount.SubGraphicForStackCount(1, eq.def).MatSingleFor(eq)
                : eq.Graphic.MatSingleFor(eq);
            Texture texture = src?.mainTexture;
            if (texture == null)
                return;
            Color tint = src.color;
            tint.a = CanopyGhostMod.Settings.alpha;
            Material mat = MaterialPool.MatFrom(new MaterialRequest(texture, ShaderDatabase.Mote, tint));
            // Keep vanilla's small offset from the body (above it, or below when facing north).
            drawLoc.y = GhostDrawer.GhostAltitude + (drawLoc.y - GhostDrawer.DrawingBodyY);
            Matrix4x4 matrix = Matrix4x4.TRS(drawLoc, Quaternion.AngleAxis(angle, Vector3.up),
                new Vector3(eq.Graphic.drawSize.x, 0f, eq.Graphic.drawSize.y));
            Graphics.DrawMesh(mesh, matrix, mat, 0);
        }
    }
}
