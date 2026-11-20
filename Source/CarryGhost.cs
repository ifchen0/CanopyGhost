using HarmonyLib;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Ghost of the thing a pawn carries, drawn with the pawn's ghost. Vanilla draws it inside RenderPawnAt through
    /// PawnRenderUtility.DrawCarriedThing at the pawn's own altitude, under the canopy. While that call runs for a
    /// pawn with a ghost, each base Graphic.DrawWorker draw (where single, stack-count, random and random-rotated
    /// graphics end up, with the final position and angle) is repeated with the ghost material at the ghost altitude.
    /// The carry position is not computed again: CalculateCarriedDrawPos moves pushed things on every call.
    /// Not drawn: carried corpses and pawns (drawn as pawns, not by DrawWorker) and graphics with their own
    /// DrawWorker that never reach the base one.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawCarriedThing))]
    public static class CarryGhost
    {
        // The renderer whose carried thing is being drawn and gets a ghost.
        private static PawnRenderer active;

        public static void Prefix(Pawn pawn, out PawnRenderer __state)
        {
            __state = active;
            active = GhostDrawer.HeldThingsGetGhost(pawn) ? GhostDrawer.Drawing : null;
        }

        public static void Postfix(PawnRenderer __state)
        {
            active = __state;
        }

        [HarmonyPatch(typeof(Graphic), nameof(Graphic.DrawWorker))]
        public static class DrawWorkerPatch
        {
            // Same mesh, rotation and offset as Graphic.DrawWorker.
            public static void Postfix(Graphic __instance, Vector3 loc, Rot4 rot, Thing thing, float extraRotation)
            {
                // A pawn rendered inside the carried thing (a corpse) changes Drawing, which stops the ghost here.
                if (active == null || active != GhostDrawer.Drawing)
                    return;
                Mesh mesh = __instance.MeshAt(rot);
                Quaternion quat = __instance.QuatFromRot(rot);
                if (extraRotation != 0f)
                    quat *= Quaternion.Euler(Vector3.up * extraRotation);
                if (__instance.data != null && __instance.data.addTopAltitudeBias)
                    quat *= Quaternion.Euler(Vector3.left * 2f);
                loc += __instance.DrawOffset(rot);
                Material src = __instance.MatAt(rot, thing);
                Texture texture = src?.mainTexture;
                if (mesh == null || texture == null)
                    return;
                Color tint = src.color;
                tint.a = CanopyGhostMod.Settings.alpha;
                Material mat = MaterialPool.MatFrom(new MaterialRequest(texture, ShaderDatabase.Mote, tint));
                // Keep vanilla's small offset from the body (above it, or below when facing north).
                loc.y = GhostDrawer.GhostAltitude + (loc.y - GhostDrawer.DrawingBodyY);
                Graphics.DrawMesh(mesh, loc, quat, mat, 0);
            }
        }
    }
}
