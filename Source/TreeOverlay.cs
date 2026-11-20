using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Translucent canopies. Single-mesh trees that another mod (Better Trees' "Render trees over colonists")
    /// lifts above pawns are moved back below them, so pawns, weapons, carried things, animations, items and
    /// corpses under a tree are drawn by vanilla in full. While something stands under a canopy in view
    /// (CanopyOverlay decides), every canopy tree in view is drawn again, solid, into an offscreen picture, and
    /// that picture is laid over the map above pawns at (1 - alpha) opacity. Where nothing is under a tree the
    /// picture matches the tree below it; things under any number of canopies show through the same (alpha).
    /// The planes of each canopy tree are recorded while vanilla prints it (Plant.Print: random offset, flip,
    /// size, snow overlay, wind payload), with the tree's own material rather than the texture atlas one, so the
    /// picture sways with the trees.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TreeOverlay
    {
        // Where the canopy trees go back to (vanilla trees), and where the picture is laid (above where Better
        // Trees puts them, above pawns).
        private const AltitudeLayer TreeLayer = AltitudeLayer.Building;
        private const AltitudeLayer OverlayLayer = AltitudeLayer.PawnUnused;
        // In the picture a tree further south (in front) sits a little higher, so it wins the depth test.
        private const float SortSpan = 0.03f;
        // The picture covers the view plus this margin, so it still covers it if the camera moves before the
        // frame is rendered.
        private const float Margin = 1.1f;

        /// <summary>
        /// Resolution of the picture relative to the screen. Half is enough: it is laid translucent over the
        /// sharp trees, and it costs about half as much as full resolution (measured in a big forest battle).
        /// </summary>
        public static float Scale = 0.5f;

        private static readonly HashSet<ThingDef> canopyDefs = new HashSet<ThingDef>();

        public static IEnumerable<ThingDef> CanopyDefs => canopyDefs;

        public static bool IsCanopyTree(ThingDef def) => canopyDefs.Contains(def);

        static TreeOverlay()
        {
            var harmony = new Harmony("ifchen0.canopyghost.overlay");
            harmony.Patch(AccessTools.Method(typeof(Plant), nameof(Plant.Print)),
                prefix: new HarmonyMethod(typeof(TreeOverlay), nameof(PrintPrefix)),
                postfix: new HarmonyMethod(typeof(TreeOverlay), nameof(PrintPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Graphic), nameof(Graphic.TryGetTextureAtlasReplacementInfo)),
                prefix: new HarmonyMethod(typeof(TreeOverlay), nameof(AtlasPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Printer_Plane), nameof(Printer_Plane.PrintPlane)),
                prefix: new HarmonyMethod(typeof(TreeOverlay), nameof(PrintPlanePrefix)));
            // Better Trees sets every tree's altitude again whenever its settings are applied.
            MethodInfo betterTrees = AccessTools.Method("BetterTrees.TextureAndSizeApplier:ApplyAltitudeLayerOverrides");
            if (betterTrees != null)
                harmony.Patch(betterTrees, postfix: new HarmonyMethod(typeof(TreeOverlay), nameof(BetterTreesPostfix)));
            Register(false);
        }

        public static void BetterTreesPostfix() => Register(true);

        // Takes over the single-mesh trees currently drawn above pawns. clear: the altitudes were just reset
        // (Better Trees with its setting off puts trees back below pawns, and they are no longer canopy trees).
        private static void Register(bool clear)
        {
            if (clear)
                canopyDefs.Clear();
            bool changed = false;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.plant == null || !def.plant.IsTree || def.plant.maxMeshCount != 1 || def.graphicData == null
                    || def.altitudeLayer <= AltitudeLayer.Pawn)
                    continue;
                def.altitudeLayer = TreeLayer;
                canopyDefs.Add(def);
                changed = true;
            }
            if ((changed || clear) && Current.ProgramState == ProgramState.Playing && Find.Maps != null)
            {
                foreach (Map map in Find.Maps)
                    map.mapDrawer.RegenerateEverythingNow();
            }
        }

        // ---- Recording the printed planes ----

        private struct Plane
        {
            public Vector3 center;
            public Vector2 size;
            public Material mat;
            public bool flipUv;
            public Color32[] colors;
            public float topBias;
            public float uvz;
        }

        private class Record
        {
            public Plant tree;
            public readonly List<Plane> planes = new List<Plane>();
            // Built on first use: each plane's corners (lifted above pawns) and uvs with the wind payload.
            public Vector3[][] verts;
            public Vector3[][] uvs;
        }

        private static readonly Dictionary<int, Record> records = new Dictionary<int, Record>();
        private static Record printing;
        private static Material atlasSource;
        // Bumped whenever a tree's planes change, so cached batch meshes are redone.
        private static int quadsVersion;

        public static void PrintPrefix(Plant __instance)
        {
            if (!canopyDefs.Contains(__instance.def))
                return;
            int id = __instance.thingIDNumber;
            if (!records.TryGetValue(id, out Record r))
                records[id] = r = new Record();
            r.tree = __instance;
            r.planes.Clear();
            r.verts = null;
            quadsVersion++;
            printing = r;
        }

        public static void PrintPostfix()
        {
            printing = null;
            atlasSource = null;
        }

        // Plant.Print swaps each material for its texture atlas version just before printing the plane.
        public static void AtlasPrefix(Material mat)
        {
            if (printing != null)
                atlasSource = mat;
        }

        public static void PrintPlanePrefix(Vector3 center, Vector2 size, Material mat, bool flipUv, Color32[] colors,
            float topVerticesAltitudeBias, float uvzPayload)
        {
            if (printing == null || mat == null)
                return;
            Material own = atlasSource ?? mat;
            atlasSource = null;
            printing.planes.Add(new Plane
            {
                center = center, size = size, mat = own, flipUv = flipUv,
                colors = colors != null ? (Color32[])colors.Clone() : null,
                topBias = topVerticesAltitudeBias, uvz = uvzPayload,
            });
        }

        internal static void ForgetDespawned()
        {
            var dead = new List<int>();
            foreach (KeyValuePair<int, Record> kv in records)
            {
                if (kv.Value.tree == null || kv.Value.tree.Destroyed)
                    dead.Add(kv.Key);
            }
            foreach (int id in dead)
                records.Remove(id);
        }

        // ---- Batches: the trees of one frame, merged into one mesh per material ----

        private static readonly Vector2[] Uvs = { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
        private static readonly Vector2[] UvsFlipped = { new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f) };
        private static readonly Color32[] DefaultColors = new Color32[4];

        private class Batch
        {
            public readonly List<(Record r, int plane)> quads = new List<(Record, int)>();
            public Mesh mesh;
            public readonly List<Vector3> verts = new List<Vector3>();
            public readonly List<Vector3> uvs = new List<Vector3>();
            public readonly List<Color32> colors = new List<Color32>();
            public readonly List<int> tris = new List<int>();
            // What the mesh was last built from: unchanged trees keep it (the wind only moves a shader value).
            public long signature;
            public int count = -1;
            public int version = -1;
        }

        private static readonly Dictionary<Material, Batch> batches = new Dictionary<Material, Batch>();
        private static readonly List<(Mesh mesh, Material mat)> toDraw = new List<(Mesh, Material)>();

        /// <summary>Trees in the picture in the latest frame that drew one, if that was this or the previous frame (tests).</summary>
        public static int DrawnLastFrame => drawnFrame >= Time.frameCount - 1 ? drawnCount : 0;
        private static int drawnCount;
        private static int drawnFrame = -1;

        /// <summary>Draws every canopy tree of the map within the view rect into the picture and lays it over the map.</summary>
        internal static void DrawInView(Map map, CellRect view)
        {
            int count = 0;
            foreach (Record r in records.Values)
            {
                Plant t = r.tree;
                if (t != null && t.Spawned && t.Map == map && view.Contains(t.Position) && Add(r))
                    count++;
            }
            drawnFrame = Time.frameCount;
            drawnCount = count;
            BuildBatches();
            if (toDraw.Count > 0)
                DrawPicture();
            toDraw.Clear();
        }

        private static bool Add(Record r)
        {
            if (r.planes.Count == 0)
                return false;
            if (r.verts == null)
                Build(r);
            for (int i = 0; i < r.planes.Count; i++)
            {
                Material m = r.planes[i].mat;
                // Better Trees' cursor transparency: already see-through, keep it out of the picture.
                if (m.shader == ShaderDatabase.Mote)
                    continue;
                if (!batches.TryGetValue(m, out Batch b))
                    batches[m] = b = new Batch();
                b.quads.Add((r, i));
            }
            return true;
        }

        private static void BuildBatches()
        {
            foreach (KeyValuePair<Material, Batch> kv in batches)
            {
                Batch b = kv.Value;
                if (b.quads.Count == 0)
                    continue;
                long signature = 0;
                foreach ((Record r, int i) in b.quads)
                {
                    long h = r.tree.thingIDNumber * 8L + i;
                    signature += h * 2654435761L ^ (h << 17);
                }
                if (b.mesh == null || b.count != b.quads.Count || b.signature != signature || b.version != quadsVersion)
                {
                    b.count = b.quads.Count;
                    b.signature = signature;
                    b.version = quadsVersion;
                    Fill(b);
                }
                b.quads.Clear();
                toDraw.Add((b.mesh, kv.Key));
            }
        }

        private static void Fill(Batch b)
        {
            b.verts.Clear();
            b.uvs.Clear();
            b.colors.Clear();
            b.tris.Clear();
            foreach ((Record r, int i) in b.quads)
            {
                int v = b.verts.Count;
                b.verts.AddRange(r.verts[i]);
                b.uvs.AddRange(r.uvs[i]);
                b.colors.AddRange(r.planes[i].colors ?? DefaultColors);
                b.tris.Add(v);
                b.tris.Add(v + 1);
                b.tris.Add(v + 2);
                b.tris.Add(v);
                b.tris.Add(v + 2);
                b.tris.Add(v + 3);
            }
            if (b.mesh == null)
                b.mesh = new Mesh { name = "CanopyPicture", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            b.mesh.Clear();
            b.mesh.SetVertices(b.verts);
            b.mesh.SetUVs(0, b.uvs);
            b.mesh.SetColors(b.colors);
            b.mesh.SetTriangles(b.tris, 0);
            b.mesh.RecalculateBounds();
        }

        private static void Build(Record r)
        {
            int n = r.planes.Count;
            r.verts = new Vector3[n][];
            r.uvs = new Vector3[n][];
            float lift = OverlayLayer.AltitudeFor() - TreeLayer.AltitudeFor();
            float sort = SortSpan * (1f - r.tree.Position.z / (float)Mathf.Max(1, r.tree.Map.Size.z));
            for (int i = 0; i < n; i++)
            {
                Plane p = r.planes[i];
                Vector3 c = p.center;
                c.y += lift + sort;
                r.verts[i] = new[]
                {
                    c + new Vector3(-0.5f * p.size.x, 0f, -0.5f * p.size.y),
                    c + new Vector3(-0.5f * p.size.x, p.topBias, 0.5f * p.size.y),
                    c + new Vector3(0.5f * p.size.x, p.topBias, 0.5f * p.size.y),
                    c + new Vector3(0.5f * p.size.x, 0f, -0.5f * p.size.y),
                };
                Vector2[] src = p.flipUv ? UvsFlipped : Uvs;
                r.uvs[i] = new Vector3[4];
                for (int j = 0; j < 4; j++)
                    r.uvs[i][j] = new Vector3(src[j].x, src[j].y, p.uvz);
            }
            quadsVersion++;
        }

        // ---- The picture ----

        private static RenderTexture picture;
        private static Mesh quad;
        private static Material quadMat;

        /// <summary>The offscreen picture (tests read it).</summary>
        public static RenderTexture Picture => picture;

        // Renders the batches (the trees' own materials, solid, depth-tested so the nearer tree wins) into the
        // picture from the camera as it is now, then lays it on a world-space quad over the same area above
        // pawns. Both use the same camera state, so the picture stays on the trees even if the camera moves
        // before the frame is rendered.
        private static void DrawPicture()
        {
            Camera cam = Find.Camera;
            if (cam == null || !cam.orthographic)
                return;
            int w = Mathf.Max(16, Mathf.CeilToInt(cam.pixelWidth * Margin * Scale));
            int h = Mathf.Max(16, Mathf.CeilToInt(cam.pixelHeight * Margin * Scale));
            if (picture == null || picture.width != w || picture.height != h)
            {
                if (picture != null)
                    picture.Release();
                picture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "CanopyPicture" };
                picture.Create();
            }
            float halfH = cam.orthographicSize * Margin;
            float halfW = halfH * cam.aspect;

            // Drawn from a camera's OnPostRender like vanilla's pawn cache: immediate drawing into a texture
            // outside a camera render does nothing on 1.7.
            Camera offscreen = PictureCamera.Get();
            offscreen.transform.position = cam.transform.position;
            offscreen.transform.rotation = cam.transform.rotation;
            offscreen.orthographicSize = halfH;
            offscreen.aspect = cam.aspect;
            offscreen.nearClipPlane = cam.nearClipPlane;
            offscreen.farClipPlane = cam.farClipPlane;
            PictureCamera.items = toDraw;
            offscreen.SetTargetBuffers(picture.colorBuffer, picture.depthBuffer);
            offscreen.Render();
            offscreen.targetTexture = null;
            PictureCamera.items = null;

            Vector3 pos = cam.transform.position;
            float y = OverlayLayer.AltitudeFor() + SortSpan + 0.15f;
            if (quad == null)
            {
                quad = new Mesh { name = "CanopyPictureQuad" };
                quad.SetVertices(new List<Vector3> { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero });
                quad.SetUVs(0, new List<Vector2> { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
                quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            }
            quad.SetVertices(new List<Vector3>
            {
                new Vector3(pos.x - halfW, y, pos.z - halfH), new Vector3(pos.x - halfW, y, pos.z + halfH),
                new Vector3(pos.x + halfW, y, pos.z + halfH), new Vector3(pos.x + halfW, y, pos.z - halfH),
            });
            quad.RecalculateBounds();
            if (quadMat == null)
                quadMat = new Material(ShaderDatabase.Mote) { name = "CanopyPicture" };
            quadMat.mainTexture = picture;
            quadMat.color = new Color(1f, 1f, 1f, 1f - CanopyGhostMod.Settings.alpha);
            Graphics.DrawMesh(quad, Matrix4x4.identity, quadMat, 0);
        }
    }

    /// <summary>Inactive camera that draws the canopy batches into its target texture when rendered.</summary>
    public class PictureCamera : MonoBehaviour
    {
        internal static List<(Mesh mesh, Material mat)> items;
        private static Camera camera;

        internal static Camera Get()
        {
            if (camera != null)
                return camera;
            var go = new GameObject("CanopyPictureCamera", typeof(Camera));
            go.SetActive(false);
            go.AddComponent<PictureCamera>();
            DontDestroyOnLoad(go);
            camera = go.GetComponent<Camera>();
            camera.orthographic = true;
            camera.cullingMask = 0;
            camera.clearFlags = CameraClearFlags.Color;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.useOcclusionCulling = false;
            camera.renderingPath = RenderingPath.Forward;
            return camera;
        }

        public void OnPostRender()
        {
            if (items == null)
                return;
            foreach ((Mesh mesh, Material mat) in items)
            {
                if (mat.SetPass(0))
                    Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            }
        }
    }
}
