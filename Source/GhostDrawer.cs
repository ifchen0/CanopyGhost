using System;
using System.Collections.Generic;
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
    /// Supported: humanlikes (from vanilla's pawn atlas, at any zoom), animals and mechanoids (body graphic
    /// only), and corpses on the ground (with the item setting).
    /// Not supported, no ghost: pawns drawn without a body this frame (NoBody, e.g. swimmers on the swimming
    /// joy job showing only the head), humanlikes in an animation, being carried or with a hediff material
    /// (e.g. invisibility), corpses on shelves or other buildings.
    /// Not drawn: extra render nodes of animals and mechanoids, items carried in hand. Pawns wading in water
    /// get a whole-body ghost: the waterline cut is done by the pawn shaders reading the water depth map,
    /// which the ghost's Mote shader does not.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
    public static class GhostDrawer
    {
        private delegate void ReadResultsFn(PawnRenderer renderer, out bool valid, out bool draw, out bool showBody,
            out Vector3 bodyPos, out float bodyAngle, out Rot4 facing, out PawnRenderFlags flags);

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
            if (crawlFrames.Count > 0)
                RefreshAfterCrawl(PawnRef(__instance));
            if (CanopyGhostMod.Settings.alpha <= 0f)
                return;
            ReadResults(__instance, out bool valid, out bool draw, out __state.showBody, out __state.bodyPos,
                out __state.bodyAngle, out __state.facing, out PawnRenderFlags flags);
            // NoBody: this frame shows only the head (vanilla swimmers), so a whole-body ghost would not match.
            __state.ok = valid && draw && (flags & PawnRenderFlags.NoBody) == 0;
        }

        public static void Postfix(PawnRenderer __instance, Snapshot __state)
        {
            if (!__state.ok)
                return;
            var settings = CanopyGhostMod.Settings;
            Pawn pawn = PawnRef(__instance);
            if (pawn == null)
                return;
            // A corpse draws its inner pawn, which is not spawned itself; the corpse stands for it on the map.
            Thing anchor = pawn;
            if (!pawn.Spawned)
            {
                if (!(pawn.ParentHolder is Corpse corpse) || !corpse.Spawned || !settings.itemGhosts)
                    return;
                // Like items: only corpses on the ground, not on shelves or other buildings.
                if (corpse.Position.GetEdifice(corpse.Map) != null)
                    return;
                anchor = corpse;
            }
            else if (!pawn.RaceProps.Humanlike && !settings.includeAnimals)
                return;
            if (anchor.Map != Find.CurrentMap)
                return;
            bool humanlike = pawn.RaceProps.Humanlike;
            bool showBody = __state.showBody;
            Vector3 bodyPos = __state.bodyPos;
            float bodyAngle = __state.bodyAngle;
            Rot4 facing = __state.facing;
            if (!CanopyCache.IsUnderCanopy(anchor))
                return;

            Mesh mesh;
            Texture texture;
            Color tint = Color.white;
            if (humanlike)
            {
                // The atlas frame is the pawn's normal look; skip states where the pawn draws differently
                // (see AtlasSafe), as the ghost would not match or would show an invisible pawn.
                if (!AtlasSafe(__instance, pawn))
                    return;
                if (pawn.Crawling && crawlFrames.Add(pawn.thingIDNumber))
                    GlobalTextureAtlasManager.TryMarkPawnFrameSetDirty(pawn);
                if (!GlobalTextureAtlasManager.TryGetPawnFrameSet(pawn, out var frameSet, out _))
                    return;
                mesh = BlitMesh(__instance, frameSet, facing, showBody ? PawnDrawMode.BodyAndHead : PawnDrawMode.HeadOnly);
                texture = frameSet.atlas;
            }
            else
            {
                // Read-only: the body node's current graphic (already the rotting or dessicated one for corpses).
                Graphic body = __instance.BodyGraphic;
                if (body == null)
                    return;
                mesh = body.MeshAt(facing);
                Material src = body.MatAt(facing);
                texture = src?.mainTexture;
                if (src != null)
                    tint = src.color;
            }
            if (mesh == null || texture == null)
                return;

            tint.a = settings.alpha;
            var mat = MaterialPool.MatFrom(new MaterialRequest(texture, ShaderDatabase.Mote, tint));
            Vector3 pos = bodyPos;
            pos.y = GhostAltitude;
            Graphics.DrawMesh(mesh, pos, Quaternion.AngleAxis(bodyAngle, Vector3.up), mat, 0);
        }

        private delegate bool NeedsHediffMaterialFn(PawnRenderer renderer, out PawnRenderFlags flags);

        private static readonly NeedsHediffMaterialFn NeedsHediffMaterial =
            AccessTools.MethodDelegate<NeedsHediffMaterialFn>(AccessTools.Method(typeof(PawnRenderer), "PawnNeedsHediffMaterial"));

        // Pawns (by thingIDNumber, so no old game is kept alive) whose atlas frame was redrawn for the ghost while
        // they crawled. The crawl head tilt (and flip) is baked into the frame, and vanilla marks it dirty when the
        // pawn stands up but not when it stops crawling while still downed; it would then draw the lying pawn
        // with the tilted frame when zoomed out (checked in game). The frame is also marked dirty when crawling
        // starts, so the ghost does not show an older lying or standing frame.
        private static readonly HashSet<int> crawlFrames = new HashSet<int>();

        // Runs before vanilla reads the frame in RenderPawnAt, so the redraw happens in the same frame.
        private static void RefreshAfterCrawl(Pawn pawn)
        {
            if (pawn == null || pawn.Crawling || !crawlFrames.Remove(pawn.thingIDNumber))
                return;
            GlobalTextureAtlasManager.TryMarkPawnFrameSetDirty(pawn);
        }

        // Part of vanilla's useCached test in PawnRenderer.ParallelGetPreRenderResults (camera zoom left out).
        // - Animation: the frame holds the plain pose, not the animated one.
        // - Hediff material (e.g. invisibility): the ghost would show a pawn meant to be hidden.
        // Not needed here: Dessicated (rot stage changes mark the frame dirty; checked in game), crawling
        // (crawlFrames refreshes the frame when crawling starts and stops), carrying (the ghost only lacks the
        // item in hand), swimming (Prefix's NoBody check) and being carried (the pawn is unspawned and not in a
        // corpse, so Postfix returns earlier).
        private static bool AtlasSafe(PawnRenderer renderer, Pawn pawn)
        {
            return renderer.renderTree?.currentAnimation == null
                && !NeedsHediffMaterial(renderer, out _);
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
                    typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType(), typeof(Rot4).MakeByRefType(),
                    typeof(PawnRenderFlags).MakeByRefType()
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
            Store(7, typeof(PawnRenderFlags), parms, AccessTools.Field(typeof(PawnDrawParms), "flags"));
            il.Emit(OpCodes.Ret);
            return (ReadResultsFn)dm.CreateDelegate(typeof(ReadResultsFn));
        }
    }
}
