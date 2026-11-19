using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Draws a translucent copy of a pawn above the tree layer while the pawn stands under a canopy,
    /// so the covered part shows through and the visible part looks unchanged.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
    public static class GhostDrawer
    {
        private delegate void ReadResultsFn(PawnRenderer renderer, out bool valid, out bool draw, out bool showBody,
            out Vector3 bodyPos, out float bodyAngle, out Rot4 facing);

        private static readonly AccessTools.FieldRef<PawnRenderer, Pawn> PawnRef =
            AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn");

        private static readonly Func<PawnRenderer, PawnTextureAtlasFrameSet, Rot4, PawnDrawMode, Mesh> BlitMesh =
            AccessTools.MethodDelegate<Func<PawnRenderer, PawnTextureAtlasFrameSet, Rot4, PawnDrawMode, Mesh>>(
                AccessTools.Method(typeof(PawnRenderer), "GetBlitMeshUpdatedFrame"));

        private static readonly ReadResultsFn ReadResults = BuildReadResults();

        // One layer above trees that Better Trees lifts to PawnUnused, still below lighting and fog.
        private static float GhostAltitude => AltitudeLayer.PawnState.AltitudeFor();

        public struct Snapshot
        {
            public bool ok;
            public bool showBody;
            public Vector3 bodyPos;
            public float bodyAngle;
            public Rot4 facing;
        }

        // RenderPawnAt clears its pre-render results at the end, so capture them before it runs.
        // Results computed inside RenderPawnAt itself (not pre-rendered in parallel) are missed; that is rare.
        public static void Prefix(PawnRenderer __instance, out Snapshot __state)
        {
            __state = default;
            if (CanopyGhostMod.Settings.alpha <= 0f)
                return;
            ReadResults(__instance, out bool valid, out bool draw, out __state.showBody, out __state.bodyPos,
                out __state.bodyAngle, out __state.facing);
            __state.ok = valid && draw;
        }

        public static void Postfix(PawnRenderer __instance, Snapshot __state)
        {
            if (!__state.ok)
                return;
            var settings = CanopyGhostMod.Settings;
            Pawn pawn = PawnRef(__instance);
            if (pawn == null || !pawn.Spawned || pawn.Map != Find.CurrentMap)
                return;
            bool humanlike = pawn.RaceProps.Humanlike;
            if (!humanlike && !settings.includeAnimals)
                return;
            bool showBody = __state.showBody;
            Vector3 bodyPos = __state.bodyPos;
            float bodyAngle = __state.bodyAngle;
            Rot4 facing = __state.facing;
            if (!CanopyCache.IsUnderCanopy(pawn))
                return;

            Mesh mesh;
            Texture texture;
            if (humanlike)
            {
                // Animated or swimming pawns do not match their atlas frame, so skip them.
                if (__instance.renderTree?.currentAnimation != null || pawn.Swimming)
                    return;
                if (!GlobalTextureAtlasManager.TryGetPawnFrameSet(pawn, out var frameSet, out _))
                    return;
                mesh = BlitMesh(__instance, frameSet, facing, showBody ? PawnDrawMode.BodyAndHead : PawnDrawMode.HeadOnly);
                texture = frameSet.atlas;
            }
            else
            {
                Graphic body = __instance.BodyGraphic;
                if (body == null)
                    return;
                mesh = body.MeshAt(facing);
                texture = body.MatAt(facing)?.mainTexture;
            }
            if (mesh == null || texture == null)
                return;

            var mat = MaterialPool.MatFrom(new MaterialRequest(texture, ShaderDatabase.Mote, new Color(1f, 1f, 1f, settings.alpha)));
            Vector3 pos = bodyPos;
            pos.y = GhostAltitude;
            Graphics.DrawMesh(mesh, pos, Quaternion.AngleAxis(bodyAngle, Vector3.up), mat, 0);
        }

        // PawnRenderer.results is a private nested struct; read its fields in place without boxing.
        private static ReadResultsFn BuildReadResults()
        {
            FieldInfo results = AccessTools.Field(typeof(PawnRenderer), "results");
            Type resultsType = results.FieldType;
            FieldInfo parms = AccessTools.Field(resultsType, "parms");
            var dm = new DynamicMethod("CanopyGhost_ReadResults", typeof(void),
                new[]
                {
                    typeof(PawnRenderer), typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType(),
                    typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType(), typeof(Rot4).MakeByRefType()
                },
                typeof(PawnRenderer), true);
            ILGenerator il = dm.GetILGenerator();

            void Store(int arg, Type type, params FieldInfo[] path)
            {
                il.Emit(OpCodes.Ldarg, arg);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, results);
                for (int i = 0; i < path.Length - 1; i++)
                    il.Emit(OpCodes.Ldflda, path[i]);
                il.Emit(OpCodes.Ldfld, path[path.Length - 1]);
                il.Emit(OpCodes.Stobj, type);
            }

            Store(1, typeof(bool), AccessTools.Field(resultsType, "valid"));
            Store(2, typeof(bool), AccessTools.Field(resultsType, "draw"));
            Store(3, typeof(bool), AccessTools.Field(resultsType, "showBody"));
            Store(4, typeof(Vector3), AccessTools.Field(resultsType, "bodyPos"));
            Store(5, typeof(float), AccessTools.Field(resultsType, "bodyAngle"));
            Store(6, typeof(Rot4), parms, AccessTools.Field(typeof(PawnDrawParms), "facing"));
            il.Emit(OpCodes.Ret);
            return (ReadResultsFn)dm.CreateDelegate(typeof(ReadResultsFn));
        }
    }
}
