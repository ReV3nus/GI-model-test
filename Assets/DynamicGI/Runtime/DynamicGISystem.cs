using System;
using System.Collections.Generic;
using System.Diagnostics;
using GIEvaluation;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace DynamicGI
{
    public enum GIDebugView { Final = 0, IndirectOnly = 1, IrradianceOnly = 2 }

    [Serializable]
    public sealed class GIProbeCascade
    {
        public Vector3 origin;
        [Min(0.05f)] public float spacing = 0.5f;
        public Vector3Int counts = new Vector3Int(8, 4, 8);
        public int ProbeCount => counts.x * counts.y * counts.z;
    }

    // Real-time diffuse GI: world-space irradiance probes updated every frame by software ray tracing
    // (two-level BVH over the scene's opaque triangle meshes). See IMPLEMENTATION.md.
    [DefaultExecutionOrder(1000)]
    public sealed class DynamicGISystem : MonoBehaviour
    {
        public static DynamicGISystem Active { get; private set; }

        [Header("Toggle / view")]
        public bool giEnabled = true;
        public GIDebugView debugView = GIDebugView.Final;
        [Min(0)] public float indirectIntensity = 1f;
        public bool showPanel = true;

        [Header("Probe volume (finest first)")]
        public List<GIProbeCascade> cascades = new List<GIProbeCascade>();

        [Header("Ray tracing")]
        [Range(32, 192)] public int raysPerProbe = 128;
        [Range(8, 48)] public int fixedRays = 32;
        [Range(0.05f, 1f)] public float probeUpdateFraction = 1f;
        [Tooltip("Extra update fraction for coarse cascades (index >= 1); they cover the courtyard and far field.")]
        [Range(0.05f, 1f)] public float coarseUpdateFraction = 0.25f;
        public bool probeRelocation = true;
        [Range(0, 3)] public float skyMip = 1f;
        [Range(0, 1)] public float bounceEnergy = 1f;

        [Header("Temporal history")]
        [Range(0, 0.995f)] public float hysteresis = 0.97f;
        [Range(0, 0.99f)] public float largeChangeHysteresis = 0.55f;
        [Range(0, 0.99f)] public float globalBoostHysteresis = 0.85f;
        [Range(0, 0.99f)] public float dirtyRegionHysteresis = 0.3f;
        [Min(1)] public int boostFrames = 45;
        [Min(0)] public float dirtyRegionPadding = 2.5f;
        [Range(0.05f, 1f)] public float changeThreshold = 0.3f;
        public bool clearHistoryOnSceneReset = false;

        [Header("Sampling bias (spacing units)")]
        [Range(0, 1)] public float normalBias = 0.25f;
        [Range(0, 1)] public float viewBias = 0.1f;
        [Range(1, 8)] public float chebyshevPower = 3f;

        [Header("Scene input")]
        public LayerMask layers = ~0;
        [Min(0.1f)] public float rescanInterval = 0.5f;
        public Cubemap fallbackSky;
        [Min(0)] public float fallbackSkyIntensity = 0;

        // ---- statistics (read by the panel and the evaluation runner) ----
        public int StatInstances { get; private set; }
        public int StatTriangles { get; private set; }
        public int StatBlas { get; private set; }
        public int StatBvhNodes { get; private set; }
        public int StatSceneTriangles { get; private set; }
        public int StatProbes { get; private set; }
        public int StatProbesUpdated { get; private set; }
        public int StatRebuilds { get; private set; }
        public int StatLights { get; private set; }
        public double LastRebuildMs { get; private set; }
        public double CpuUpdateMs { get; private set; }
        public float GpuProbeMs { get; private set; }
        public float GpuAlbedoMs { get; private set; }
        public float GpuCompositeMs { get; private set; }
        public int LastSceneChangeFrame { get; private set; } = -1;
        public double LastSceneChangeTime { get; private set; }
        public long GpuMemoryBytes { get; private set; }
        public int FramesSinceChange => LastSceneChangeFrame < 0 ? -1 : Time.frameCount - LastSceneChangeFrame;
        public bool ShouldComposite => giEnabled && probesReady && isActiveAndEnabled;

        // ---------------------------------------------------------------------------------------
        private sealed class Tracked
        {
            public MeshRenderer renderer; public MeshFilter filter; public Mesh mesh;
            public Matrix4x4 matrix; public Material[] materials; public bool visible;
        }

        private sealed class BlasEntry
        {
            public readonly List<GIBvhNode> nodes = new List<GIBvhNode>();
            public GITriangle[] triangles;   // object space, material = submesh index, BVH order
            public int subMeshes;
            public int nodeOffset, triOffset;   // offsets inside the BLAS pool
        }

        private ComputeShader probeShader;
        private Shader gbufferShader, compositeShader;
        private Material compositeMaterial;
        private DynamicGIRenderPass renderPass;

        private readonly List<Tracked> tracked = new List<Tracked>();
        private readonly Dictionary<MeshRenderer, Tracked> trackedLookup = new Dictionary<MeshRenderer, Tracked>();
        private readonly Dictionary<Mesh, BlasEntry> blasCache = new Dictionary<Mesh, BlasEntry>();
        private readonly Dictionary<Texture, Color> textureAverage = new Dictionary<Texture, Color>();
        private readonly List<Light> lights = new List<Light>();
        private readonly List<Bounds> pendingDirty = new List<Bounds>();
        private readonly List<(Bounds bounds, int until)> dirtyRegions = new List<(Bounds, int)>();
        private bool sceneDirty = true, fullReset = true;
        private float nextRescan;
        private int globalBoostUntil;
        private bool wasEnabled = true;
        private int lightHash;
        private bool probesReady;
        private readonly int[] cascadeCursor = new int[4];
        private readonly List<Vector2Int> ranges = new List<Vector2Int>();
        private System.Random random = new System.Random(1234);

        private GraphicsBuffer nodeBuffer, instanceBuffer, triangleBuffer, materialBuffer;
        private readonly List<GIBvhNode> blasNodes = new List<GIBvhNode>();
        private bool blasPoolDirty = true;
        private GraphicsBuffer rayBuffer, probeDataBuffer, samplePosBuffer, sampleNormalBuffer, sampleOutBuffer;
        private RenderTexture irradianceAtlas, distanceAtlas;
        private int tlasNodeCount, probeTotal, tilesPerRow;
        private CommandBuffer cmd;

        private int kTrace, kRelocate, kBlendIrr, kBlendDist, kBorderIrr, kBorderDist, kReset, kSample;
        private readonly Vector4[] cascadeOrigin = new Vector4[4], cascadeCounts = new Vector4[4];
        private readonly Vector4[] lightPos = new Vector4[8], lightColor = new Vector4[8], lightSpot = new Vector4[8], lightSpotParams = new Vector4[8];
        private readonly Vector4[] dirtyMin = new Vector4[16], dirtyMax = new Vector4[16];
        private GIBreachEnvironment environment;

        private CustomSampler probeSampler;
        private readonly CustomSampler[] kernelSamplers = new CustomSampler[4];
        public readonly float[] KernelGpuMs = new float[4];   // trace, relocate, blend, border
        private Recorder probeRecorder, albedoRecorder, compositeRecorder;
        private readonly List<Vector4> samplePoints = new List<Vector4>(), sampleNormals = new List<Vector4>();
        private Vector4[] sampleResults = Array.Empty<Vector4>();
        private int sampleResultFrame = -1;
        private bool sampleRequestPending;
        private GUIStyle panelStyle;

        internal Shader GBufferShader => gbufferShader;
        internal Material CompositeMaterial => compositeMaterial;

        // ---------------------------------------------------------------------------------------
        private void Reset() => cascades = DefaultCascades();

        public static List<GIProbeCascade> DefaultCascades() => new List<GIProbeCascade>
        {
            new GIProbeCascade { origin = new Vector3(-6.3f, -0.05f, -0.45f), spacing = 0.4f, counts = new Vector3Int(33, 11, 28) },
            new GIProbeCascade { origin = new Vector3(-14f, -0.6f, -13f), spacing = 1.0f, counts = new Vector3Int(29, 8, 26) },
        };

        private void OnEnable()
        {
            if (Active && Active != this) { Debug.LogWarning("[DynamicGI] Only one DynamicGISystem can be active."); enabled = false; return; }
            Active = this;
            if (cascades == null || cascades.Count == 0) cascades = DefaultCascades();
            probeShader = Resources.Load<ComputeShader>("DynamicGIProbes");
            gbufferShader = Shader.Find("Hidden/DynamicGI/GBuffer");
            compositeShader = Shader.Find("Hidden/DynamicGI/Composite");
            if (!probeShader || !gbufferShader || !compositeShader) { Debug.LogError("[DynamicGI] Missing shaders."); enabled = false; return; }
            compositeMaterial = CoreUtils.CreateEngineMaterial(compositeShader);
            renderPass = new DynamicGIRenderPass(this);
            kTrace = probeShader.FindKernel("TraceProbes"); kRelocate = probeShader.FindKernel("RelocateProbes");
            kBlendIrr = probeShader.FindKernel("BlendIrradiance"); kBlendDist = probeShader.FindKernel("BlendDistance");
            kBorderIrr = probeShader.FindKernel("BorderIrradiance"); kBorderDist = probeShader.FindKernel("BorderDistance");
            kReset = probeShader.FindKernel("ResetProbes"); kSample = probeShader.FindKernel("SamplePoints");
            cmd = new CommandBuffer { name = "DynamicGI" };
            probeSampler = CustomSampler.Create("DynamicGI.Probes", true);
            string[] kn = { "DynamicGI.Trace", "DynamicGI.Relocate", "DynamicGI.Blend", "DynamicGI.Border" };
            for (int i = 0; i < 4; i++) { kernelSamplers[i] = CustomSampler.Create(kn[i], true); kernelSamplers[i].GetRecorder().enabled = true; } probeRecorder = probeSampler.GetRecorder(); probeRecorder.enabled = true;
            albedoRecorder = Recorder.Get("DynamicGI.Albedo"); albedoRecorder.enabled = true;
            compositeRecorder = Recorder.Get("DynamicGI.Composite"); compositeRecorder.enabled = true;
            AllocateProbes();
            GeometryChanges.Changed += OnGeometryChanged;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            environment = FindAnyObjectByType<GIBreachEnvironment>();
            if (environment) environment.Changed += OnEnvironmentChanged;
            sceneDirty = fullReset = true;
        }

        private void OnDisable()
        {
            if (Active != this) return;
            GeometryChanges.Changed -= OnGeometryChanged;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            if (environment) environment.Changed -= OnEnvironmentChanged;
            ReleaseAll();
            CoreUtils.Destroy(compositeMaterial);
            cmd?.Release(); cmd = null;
            Active = null;
            probesReady = false;
        }

        private void ReleaseAll()
        {
            void R(ref GraphicsBuffer b) { b?.Release(); b = null; }
            R(ref nodeBuffer); R(ref instanceBuffer); R(ref triangleBuffer); R(ref materialBuffer);
            R(ref rayBuffer); R(ref probeDataBuffer); R(ref samplePosBuffer); R(ref sampleNormalBuffer); R(ref sampleOutBuffer);
            if (irradianceAtlas) { irradianceAtlas.Release(); DestroyImmediate(irradianceAtlas); }
            if (distanceAtlas) { distanceAtlas.Release(); DestroyImmediate(distanceAtlas); }
        }

        // ---------------------------------------------------------------------------------------
        // Change notifications
        // ---------------------------------------------------------------------------------------
        private void OnGeometryChanged(GeometryChange change)
        {
            sceneDirty = true;
            if (change.Kind == GeometryChangeKind.SceneReset)
            {
                blasCache.Clear(); blasPoolDirty = true;
                if (clearHistoryOnSceneReset) fullReset = true;
                globalBoostUntil = Time.frameCount + boostFrames;
                return;
            }
            if (change.PreviousBounds.size.sqrMagnitude > 0) pendingDirty.Add(change.PreviousBounds);
            if (change.Renderer)
            {
                if (change.Kind == GeometryChangeKind.MeshChanged)
                {
                    var filter = change.Renderer.GetComponent<MeshFilter>();
                    // The mesh object may have been edited in place: drop its cached BLAS.
                    if (filter && filter.sharedMesh) { if (blasCache.Remove(filter.sharedMesh)) blasPoolDirty = true; }
                }
                if (change.Renderer.enabled && change.Renderer.gameObject.activeInHierarchy) pendingDirty.Add(change.Renderer.bounds);
            }
        }

        private void OnEnvironmentChanged(GIBreachEnvironment env) => globalBoostUntil = Time.frameCount + boostFrames;

        /// <summary>Forces a full rescan and history reset (for manual use / tests).</summary>
        public void ForceFullReset() { blasCache.Clear(); blasPoolDirty = true; sceneDirty = fullReset = true; }

        // ---------------------------------------------------------------------------------------
        // Scene capture
        // ---------------------------------------------------------------------------------------
        private static bool Include(MeshRenderer r, int layerMask, out MeshFilter filter)
        {
            filter = null;
            if (!r || !r.enabled || !r.gameObject.activeInHierarchy) return false;
            if (((1 << r.gameObject.layer) & layerMask) == 0) return false;
            if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return false;
            filter = r.GetComponent<MeshFilter>();
            if (!filter || !filter.sharedMesh) return false;
            foreach (var m in r.sharedMaterials)
                if (m && m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) return false;   // transparent: not part of the opaque GI scene
            return true;
        }

        private void Rescan()
        {
            var all = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude);
            int found = 0;
            foreach (var r in all)
            {
                if (!Include(r, layers, out var filter)) continue;
                found++;
                if (!trackedLookup.ContainsKey(r))
                {
                    var t = new Tracked { renderer = r, filter = filter };
                    tracked.Add(t); trackedLookup.Add(r, t);
                    sceneDirty = true; pendingDirty.Add(r.bounds);
                }
            }
            if (found != CountVisible()) sceneDirty = true;
            lights.Clear();
            lights.AddRange(FindObjectsByType<Light>(FindObjectsInactive.Include));
        }

        private int CountVisible() { int c = 0; foreach (var t in tracked) if (t.visible) c++; return c; }

        // Polling safety net: catches transform / mesh / material / visibility edits that were not published.
        private void Poll()
        {
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                var t = tracked[i];
                if (!t.renderer)
                {
                    tracked.RemoveAt(i); sceneDirty = true;
                    continue;
                }
                bool visible = Include(t.renderer, layers, out var filter);
                if (visible != t.visible) { sceneDirty = true; pendingDirty.Add(t.renderer.bounds); continue; }
                if (!visible) continue;
                if (filter.sharedMesh != t.mesh || t.renderer.localToWorldMatrix != t.matrix || !SameMaterials(t.materials, t.renderer.sharedMaterials))
                {
                    sceneDirty = true; pendingDirty.Add(t.renderer.bounds);
                    if (t.mesh) pendingDirty.Add(TransformBounds(t.mesh.bounds, t.matrix));
                }
            }
        }

        private static bool SameMaterials(Material[] a, Material[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var c = m.MultiplyPoint3x4(b.center); var e = b.extents;
            var ax = m.MultiplyVector(new Vector3(e.x, 0, 0)); var ay = m.MultiplyVector(new Vector3(0, e.y, 0)); var az = m.MultiplyVector(new Vector3(0, 0, e.z));
            var ext = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x), Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y), Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, ext * 2);
        }

        private BlasEntry GetBlas(Mesh mesh)
        {
            if (blasCache.TryGetValue(mesh, out var e)) return e;
            e = new BlasEntry { subMeshes = mesh.subMeshCount };
            var verts = mesh.vertices;
            var tris = new List<GITriangle>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var idx = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    Vector3 a = verts[idx[i]], b = verts[idx[i + 1]], c = verts[idx[i + 2]];
                    if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-16f) continue;
                    tris.Add(new GITriangle { v0 = a, e1 = b - a, e2 = c - a, material = s });
                }
            }
            var mn = new Vector3[tris.Count]; var mx = new Vector3[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i]; Vector3 b = t.v0 + t.e1, c = t.v0 + t.e2;
                mn[i] = Vector3.Min(t.v0, Vector3.Min(b, c)) - Vector3.one * 1e-4f;
                mx[i] = Vector3.Max(t.v0, Vector3.Max(b, c)) + Vector3.one * 1e-4f;
            }
            GIBvhBuilder.Build(mn, mx, 2, e.nodes, out var order);
            e.triangles = new GITriangle[tris.Count];
            for (int i = 0; i < order.Length; i++) e.triangles[i] = tris[order[i]];
            blasPoolDirty = true;
            blasCache[mesh] = e;
            return e;
        }

        private Color MaterialAlbedo(Material m)
        {
            if (!m) return new Color(0.5f, 0.5f, 0.5f);
            Color baseColor = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            Color c = baseColor.linear;
            Texture tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            if (tex) c *= TextureAverage(tex);
            float metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
            float diffuse = 0.96f * (1f - metallic);   // URP: oneMinusReflectivity = 0.96 * (1 - metallic)
            return new Color(Mathf.Min(c.r * diffuse, 0.95f), Mathf.Min(c.g * diffuse, 0.95f), Mathf.Min(c.b * diffuse, 0.95f), 1);
        }

        // Average linear colour of a texture (GPU downsample + one readback, cached per texture).
        private Color TextureAverage(Texture tex)
        {
            if (textureAverage.TryGetValue(tex, out var c)) return c;
            var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var read = new Texture2D(64, 64, TextureFormat.RGBAHalf, false, true);
            read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); read.Apply(false);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = read.GetPixels(); DestroyImmediate(read);
            Color sum = Color.black; foreach (var p in px) sum += p;
            c = sum / px.Length; c.a = 1;
            textureAverage[tex] = c;
            return c;
        }

        // Rebuilds the TLAS over all visible instances. BLAS (per mesh) are cached and only built for new meshes.
        private void RebuildScene()
        {
            var sw = Stopwatch.StartNew();
            var instances = new List<(Tracked t, BlasEntry blas)>();
            var usedMeshes = new HashSet<Mesh>();
            foreach (var t in tracked)
            {
                t.visible = t.renderer && Include(t.renderer, layers, out t.filter);
                if (!t.visible) { t.mesh = null; continue; }
                t.mesh = t.filter.sharedMesh; t.matrix = t.renderer.localToWorldMatrix; t.materials = t.renderer.sharedMaterials;
                var blas = GetBlas(t.mesh);
                usedMeshes.Add(t.mesh);
                if (blas.triangles.Length > 0) instances.Add((t, blas));
            }
            var stale = new List<Mesh>(); foreach (var kv in blasCache) if (!kv.Key || !usedMeshes.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var m in stale) { blasCache.Remove(m); blasPoolDirty = true; }

            // BLAS pool: node lists and triangles of every cached mesh (triangles only re-uploaded when the set changes).
            if (blasPoolDirty || triangleBuffer == null)
            {
                blasNodes.Clear(); var tris = new List<GITriangle>();
                foreach (var kv in blasCache) { kv.Value.nodeOffset = blasNodes.Count; kv.Value.triOffset = tris.Count; blasNodes.AddRange(kv.Value.nodes); tris.AddRange(kv.Value.triangles); }
                if (tris.Count == 0) tris.Add(default);
                Upload(ref triangleBuffer, tris.ToArray(), GITriangle.Stride);
                StatTriangles = tris.Count; StatBlas = blasCache.Count;
                blasPoolDirty = false;
            }

            // TLAS over instance world bounds.
            int n = instances.Count;
            var mn = new Vector3[n]; var mx = new Vector3[n];
            for (int i = 0; i < n; i++) { var b = TransformBounds(instances[i].t.mesh.bounds, instances[i].t.matrix); mn[i] = b.min - Vector3.one * 1e-3f; mx[i] = b.max + Vector3.one * 1e-3f; }
            var tlas = new List<GIBvhNode>();
            GIBvhBuilder.Build(mn, mx, 1, tlas, out var order);
            int tlasCount = tlas.Count;
            var nodes = new GIBvhNode[tlasCount + Mathf.Max(1, blasNodes.Count)];
            tlas.CopyTo(nodes, 0); blasNodes.CopyTo(nodes, tlasCount);

            var gpuInstances = new GIInstanceData[Math.Max(1, n)];
            var materials = new List<Vector4>();
            var materialColor = new Dictionary<Material, Color>();
            int sceneTriangles = 0;
            for (int i = 0; i < n; i++)
            {
                var (t, blas) = instances[order[i]];
                var w2o = t.matrix.inverse;
                var mats = t.materials;
                int matOffset = materials.Count;
                for (int s = 0; s < blas.subMeshes; s++)
                {
                    var m = mats.Length == 0 ? null : mats[Mathf.Min(s, mats.Length - 1)];
                    Color a;
                    if (m == null || !materialColor.TryGetValue(m, out a)) { a = MaterialAlbedo(m); if (m != null) materialColor[m] = a; }
                    materials.Add(new Vector4(a.r, a.g, a.b, 1));
                }
                gpuInstances[i] = new GIInstanceData
                {
                    w2o0 = w2o.GetRow(0), w2o1 = w2o.GetRow(1), w2o2 = w2o.GetRow(2),
                    nodeOffset = tlasCount + blas.nodeOffset, triOffset = blas.triOffset, materialOffset = matOffset,
                    winding = t.matrix.determinant < 0 ? -1f : 1f
                };
                sceneTriangles += blas.triangles.Length;
            }
            if (materials.Count == 0) materials.Add(Vector4.zero);
            tlasNodeCount = n == 0 ? 0 : tlasCount;
            Upload(ref nodeBuffer, nodes, GIBvhNode.Stride);
            Upload(ref instanceBuffer, gpuInstances, GIInstanceData.Stride);
            Upload(ref materialBuffer, materials.ToArray(), 16);
            StatInstances = n; StatSceneTriangles = sceneTriangles; StatBvhNodes = nodes.Length;
            StatRebuilds++;
            LastRebuildMs = sw.Elapsed.TotalMilliseconds;
            sceneDirty = false;
        }

        private static void Upload<T>(ref GraphicsBuffer buffer, T[] data, int stride) where T : struct
        {
            if (buffer == null || buffer.count < data.Length || buffer.stride != stride)
            {
                buffer?.Release();
                buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, Mathf.NextPowerOfTwo(data.Length)), stride);
            }
            buffer.SetData(data);
        }

        // ---------------------------------------------------------------------------------------
        // Probe resources
        // ---------------------------------------------------------------------------------------
        private void AllocateProbes()
        {
            probeTotal = 0;
            for (int c = 0; c < Mathf.Min(4, cascades.Count); c++) probeTotal += cascades[c].ProbeCount;
            tilesPerRow = 128;
            int rows = (probeTotal + tilesPerRow - 1) / tilesPerRow;
            irradianceAtlas = CreateAtlas(tilesPerRow * 8, rows * 8, RenderTextureFormat.ARGBHalf, "DynamicGI Irradiance");
            distanceAtlas = CreateAtlas(tilesPerRow * 12, rows * 12, RenderTextureFormat.RGHalf, "DynamicGI Distance");
            rayBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, probeTotal * 192, 16);
            probeDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, probeTotal, 16);
            StatProbes = probeTotal;
            GpuMemoryBytes = (long)irradianceAtlas.width * irradianceAtlas.height * 8 + (long)distanceAtlas.width * distanceAtlas.height * 4 +
                             (long)rayBuffer.count * 16 + probeTotal * 16L;
            fullReset = true;
        }

        private static RenderTexture CreateAtlas(int w, int h, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear)
            { enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = name, useMipMap = false };
            rt.Create();
            return rt;
        }

        // ---------------------------------------------------------------------------------------
        // Per-frame update
        // ---------------------------------------------------------------------------------------
        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current; if (k == null) return;
            if (k.gKey.wasPressedThisFrame) giEnabled = !giEnabled;
            if (k.hKey.wasPressedThisFrame) debugView = (GIDebugView)(((int)debugView + 1) % 3);
            if (k.jKey.wasPressedThisFrame) showPanel = !showPanel;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.G)) giEnabled = !giEnabled;
            if (Input.GetKeyDown(KeyCode.H)) debugView = (GIDebugView)(((int)debugView + 1) % 3);
            if (Input.GetKeyDown(KeyCode.J)) showPanel = !showPanel;
#endif
        }

        private void LateUpdate()
        {
            var sw = Stopwatch.StartNew();
            if (Time.unscaledTime >= nextRescan) { Rescan(); nextRescan = Time.unscaledTime + rescanInterval; }
            Poll();
            if (sceneDirty)
            {
                RebuildScene();
                foreach (var b in pendingDirty) { var e = b; e.Expand(dirtyRegionPadding * 2); dirtyRegions.Add((e, Time.frameCount + boostFrames)); }
                pendingDirty.Clear();
                globalBoostUntil = Mathf.Max(globalBoostUntil, Time.frameCount + boostFrames);
                LastSceneChangeFrame = Time.frameCount; LastSceneChangeTime = Time.realtimeSinceStartupAsDouble;
            }
            dirtyRegions.RemoveAll(d => d.until < Time.frameCount);
            if (dirtyRegions.Count > 16) dirtyRegions.RemoveRange(0, dirtyRegions.Count - 16);

            int newLightHash = PackLights();
            if (newLightHash != lightHash) { lightHash = newLightHash; globalBoostUntil = Mathf.Max(globalBoostUntil, Time.frameCount + boostFrames); }

            // History may be stale after GI was switched off for a while: re-converge quickly.
            if (giEnabled && !wasEnabled) globalBoostUntil = Mathf.Max(globalBoostUntil, Time.frameCount + boostFrames);
            wasEnabled = giEnabled;
            if (giEnabled) DispatchProbeUpdate();
            ReadTimings();
            CpuUpdateMs = sw.Elapsed.TotalMilliseconds;
        }

        private int PackLights()
        {
            int count = 0; int hash = 17;
            foreach (var l in lights)
            {
                if (count >= 8) break;
                if (!l || !l.isActiveAndEnabled || l.intensity <= 0 || l.bakingOutput.lightmapBakeType == LightmapBakeType.Baked) continue;
                if (l.type != LightType.Directional && l.type != LightType.Point && l.type != LightType.Spot) continue;
                Color c = l.color.linear * l.intensity;
                lightColor[count] = new Vector4(c.r, c.g, c.b, l.type == LightType.Directional ? 0 : 1f / Mathf.Max(1e-4f, l.range * l.range));
                if (l.type == LightType.Directional) lightPos[count] = -l.transform.forward;
                else lightPos[count] = l.transform.position;
                lightPos[count].w = l.type == LightType.Directional ? 0 : l.type == LightType.Point ? 1 : 2;
                if (l.type == LightType.Spot)
                {
                    float cosOuter = Mathf.Cos(Mathf.Deg2Rad * l.spotAngle * 0.5f), cosInner = Mathf.Cos(Mathf.Deg2Rad * l.innerSpotAngle * 0.5f);
                    float inv = 1f / Mathf.Max(0.001f, cosInner - cosOuter);
                    lightSpot[count] = l.transform.forward; lightSpotParams[count] = new Vector4(inv, -cosOuter * inv, 0, 0);
                }
                hash = hash * 31 + lightPos[count].GetHashCode(); hash = hash * 31 + lightColor[count].GetHashCode();
                count++;
            }
            StatLights = count;
            for (int i = count; i < 8; i++) { lightPos[i] = Vector4.zero; lightColor[i] = Vector4.zero; }
            var sky = SkyInput(out float skyIntensity);
            hash = hash * 31 + skyIntensity.GetHashCode() + (sky ? sky.GetHashCode() : 0);
            return hash * 31 + count;
        }

        private Cubemap SkyInput(out float intensity)
        {
            if (environment && environment.skyRadiance) { intensity = environment.EffectiveSkyIntensity; return environment.skyRadiance; }
            intensity = fallbackSky ? fallbackSkyIntensity : 0f;
            return fallbackSky;
        }

        private void SetCommonParams(CommandBuffer c)
        {
            int first = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i < cascades.Count)
                {
                    var cs = cascades[i];
                    cascadeOrigin[i] = new Vector4(cs.origin.x, cs.origin.y, cs.origin.z, cs.spacing);
                    cascadeCounts[i] = new Vector4(cs.counts.x, cs.counts.y, cs.counts.z, first);
                    first += cs.ProbeCount;
                }
                else { cascadeOrigin[i] = new Vector4(0, 0, 0, 1); cascadeCounts[i] = new Vector4(1, 1, 1, first); }
            }
            c.SetGlobalVectorArray("_GICascadeOrigin", cascadeOrigin);
            c.SetGlobalVectorArray("_GICascadeCounts", cascadeCounts);
            c.SetGlobalInt("_GICascadeCount", Mathf.Min(4, cascades.Count));
            c.SetGlobalInt("_GIProbeTotal", probeTotal);
            c.SetGlobalInt("_GITilesPerRow", tilesPerRow);
            c.SetGlobalVector("_GIIrradianceAtlasSize", new Vector4(irradianceAtlas.width, irradianceAtlas.height, 1f / irradianceAtlas.width, 1f / irradianceAtlas.height));
            c.SetGlobalVector("_GIDistanceAtlasSize", new Vector4(distanceAtlas.width, distanceAtlas.height, 1f / distanceAtlas.width, 1f / distanceAtlas.height));
            c.SetGlobalVector("_GIBias", new Vector4(normalBias, viewBias, chebyshevPower, bounceEnergy));
            c.SetGlobalBuffer("_GIProbeData", probeDataBuffer);
            c.SetGlobalTexture("_GIIrradianceAtlas", irradianceAtlas);
            c.SetGlobalTexture("_GIDistanceAtlas", distanceAtlas);
            c.SetGlobalFloat("_GIIndirectIntensity", indirectIntensity);
            c.SetGlobalInt("_GIDebugView", (int)debugView);
        }

        private void BindCompute(CommandBuffer c, int k)
        {
            c.SetComputeVectorArrayParam(probeShader, "_GICascadeOrigin", cascadeOrigin);
            c.SetComputeVectorArrayParam(probeShader, "_GICascadeCounts", cascadeCounts);
            c.SetComputeBufferParam(probeShader, k, "_GIProbeData", probeDataBuffer);
            c.SetComputeTextureParam(probeShader, k, "_GIIrradianceAtlas", irradianceAtlas);
            c.SetComputeTextureParam(probeShader, k, "_GIDistanceAtlas", distanceAtlas);
        }

        private void DispatchProbeUpdate()
        {
            if (nodeBuffer == null) return;
            cmd.Clear();
            cmd.BeginSample(probeSampler);
            SetCommonParams(cmd);
            var cs = probeShader;
            int rays = Mathf.Clamp(raysPerProbe, fixedRays + 8, 192);
            int frame = Time.frameCount;

            if (fullReset)
            {
                cmd.SetComputeIntParam(cs, "_GIProbeTotal", probeTotal);
                cmd.SetComputeBufferParam(cs, kReset, "_GIProbeDataRW", probeDataBuffer);
                cmd.DispatchCompute(cs, kReset, (probeTotal + 63) / 64, 1, 1);
                cmd.SetRenderTarget(irradianceAtlas); cmd.ClearRenderTarget(false, true, Color.clear);
                cmd.SetRenderTarget(distanceAtlas); cmd.ClearRenderTarget(false, true, Color.clear);
            }

            // Probe ranges updated this frame: round-robin window per cascade (coarse cascades update less often).
            ranges.Clear();
            int firstProbe = 0, updated = 0;
            for (int c = 0; c < Mathf.Min(4, cascades.Count); c++)
            {
                int total = cascades[c].ProbeCount;
                float fraction = probeUpdateFraction * (c == 0 ? 1f : coarseUpdateFraction);
                int count = fullReset ? total : Mathf.Clamp(Mathf.CeilToInt(total * fraction), 1, total);
                int off = fullReset ? 0 : cascadeCursor[c] % total;
                if (off + count > total) { ranges.Add(new Vector2Int(firstProbe + off, total - off)); ranges.Add(new Vector2Int(firstProbe, count - (total - off))); }
                else ranges.Add(new Vector2Int(firstProbe + off, count));
                cascadeCursor[c] = (off + count) % total;
                firstProbe += total; updated += count;
            }
            StatProbesUpdated = updated;

            // Shared parameters
            cmd.SetComputeIntParam(cs, "_GICascadeCount", Mathf.Min(4, cascades.Count));
            cmd.SetComputeIntParam(cs, "_GIProbeTotal", probeTotal);
            cmd.SetComputeIntParam(cs, "_GITilesPerRow", tilesPerRow);
            cmd.SetComputeVectorParam(cs, "_GIIrradianceAtlasSize", new Vector4(irradianceAtlas.width, irradianceAtlas.height, 1f / irradianceAtlas.width, 1f / irradianceAtlas.height));
            cmd.SetComputeVectorParam(cs, "_GIDistanceAtlasSize", new Vector4(distanceAtlas.width, distanceAtlas.height, 1f / distanceAtlas.width, 1f / distanceAtlas.height));
            cmd.SetComputeVectorParam(cs, "_GIBias", new Vector4(normalBias, viewBias, chebyshevPower, bounceEnergy));
            cmd.SetComputeIntParam(cs, "_GIRaysPerProbe", rays);
            cmd.SetComputeIntParam(cs, "_GIFixedRays", fixedRays);
            cmd.SetComputeIntParam(cs, "_GITlasNodeCount", tlasNodeCount);
            cmd.SetComputeMatrixParam(cs, "_GIRayRotation", Matrix4x4.Rotate(RandomRotation()));
            cmd.SetComputeIntParam(cs, "_GILightCount", StatLights);
            cmd.SetComputeVectorArrayParam(cs, "_GILightPosType", lightPos);
            cmd.SetComputeVectorArrayParam(cs, "_GILightColor", lightColor);
            cmd.SetComputeVectorArrayParam(cs, "_GILightSpot", lightSpot);
            cmd.SetComputeVectorArrayParam(cs, "_GILightSpotParams", lightSpotParams);
            var sky = SkyInput(out float skyIntensity);
            cmd.SetComputeFloatParam(cs, "_GISkyIntensity", sky ? skyIntensity : 0f);
            cmd.SetComputeFloatParam(cs, "_GISkyMip", skyMip);
            float globalH = frame <= globalBoostUntil ? globalBoostHysteresis : 1f;
            cmd.SetComputeVectorParam(cs, "_GIHysteresis", new Vector4(hysteresis, largeChangeHysteresis, globalH, dirtyRegionHysteresis));
            cmd.SetComputeVectorParam(cs, "_GIChangeThreshold", new Vector4(frame <= globalBoostUntil ? changeThreshold : 1e6f, 0.002f, 50f, 1.5f));
            int dc = Mathf.Min(16, dirtyRegions.Count);
            for (int i = 0; i < 16; i++)
            {
                if (i < dc) { dirtyMin[i] = dirtyRegions[i].bounds.min; dirtyMax[i] = dirtyRegions[i].bounds.max; }
                else { dirtyMin[i] = Vector4.zero; dirtyMax[i] = Vector4.zero; }
            }
            cmd.SetComputeIntParam(cs, "_GIDirtyCount", dc);
            cmd.SetComputeVectorArrayParam(cs, "_GIDirtyMin", dirtyMin);
            cmd.SetComputeVectorArrayParam(cs, "_GIDirtyMax", dirtyMax);
            cmd.SetComputeFloatParam(cs, "_GIRelocationEnabled", probeRelocation ? 1 : 0);
            cmd.SetComputeFloatParam(cs, "_GIFirstFrame", fullReset ? 1 : 0);

            // 1. Trace
            BindCompute(cmd, kTrace);
            cmd.SetComputeBufferParam(cs, kTrace, "_GINodes", nodeBuffer);
            cmd.SetComputeBufferParam(cs, kTrace, "_GIInstances", instanceBuffer);
            cmd.SetComputeBufferParam(cs, kTrace, "_GITriangles", triangleBuffer);
            cmd.SetComputeBufferParam(cs, kTrace, "_GIMaterials", materialBuffer);
            cmd.SetComputeBufferParam(cs, kTrace, "_GIRayResults", rayBuffer);
            cmd.SetComputeTextureParam(cs, kTrace, "_GISkyCube", sky ? (Texture)sky : Texture2D.blackTexture);
            cmd.BeginSample(kernelSamplers[0]);
            foreach (var r in ranges) { SetRange(r); cmd.DispatchCompute(cs, kTrace, (r.y * rays + 63) / 64, 1, 1); }
            cmd.EndSample(kernelSamplers[0]);

            // 2. Relocate / classify
            cmd.SetComputeBufferParam(cs, kRelocate, "_GIRayResults", rayBuffer);
            cmd.SetComputeBufferParam(cs, kRelocate, "_GIProbeDataRW", probeDataBuffer);
            cmd.BeginSample(kernelSamplers[1]);
            foreach (var r in ranges) { SetRange(r); cmd.DispatchCompute(cs, kRelocate, (r.y + 63) / 64, 1, 1); }
            cmd.EndSample(kernelSamplers[1]);

            // 3. Blend irradiance / distance, 4. borders
            cmd.SetComputeBufferParam(cs, kBlendIrr, "_GIRayResults", rayBuffer);
            cmd.SetComputeBufferParam(cs, kBlendIrr, "_GIProbeDataRW", probeDataBuffer);
            cmd.SetComputeTextureParam(cs, kBlendIrr, "_GIIrradianceRW", irradianceAtlas);
            cmd.BeginSample(kernelSamplers[2]);
            foreach (var r in ranges) { SetRange(r); cmd.DispatchCompute(cs, kBlendIrr, r.y, 1, 1); }
            cmd.SetComputeBufferParam(cs, kBlendDist, "_GIRayResults", rayBuffer);
            cmd.SetComputeBufferParam(cs, kBlendDist, "_GIProbeDataRW", probeDataBuffer);
            cmd.SetComputeTextureParam(cs, kBlendDist, "_GIDistanceRW", distanceAtlas);
            foreach (var r in ranges) { SetRange(r); cmd.DispatchCompute(cs, kBlendDist, r.y, 1, 1); }
            cmd.EndSample(kernelSamplers[2]);
            cmd.BeginSample(kernelSamplers[3]);
            cmd.SetComputeTextureParam(cs, kBorderIrr, "_GIIrradianceRW", irradianceAtlas);
            foreach (var r in ranges) { SetRange(r); cmd.DispatchCompute(cs, kBorderIrr, r.y, 1, 1); }
            cmd.SetComputeTextureParam(cs, kBorderDist, "_GIDistanceRW", distanceAtlas);
            foreach (var r in ranges) { SetRange(r); cmd.DispatchCompute(cs, kBorderDist, r.y, 1, 1); }
            cmd.EndSample(kernelSamplers[3]);

            // Optional diagnostics sampling
            if (samplePoints.Count > 0 && !sampleRequestPending)
            {
                EnsureSampleBuffers();
                samplePosBuffer.SetData(samplePoints); sampleNormalBuffer.SetData(sampleNormals);
                BindCompute(cmd, kSample);
                cmd.SetComputeBufferParam(cs, kSample, "_GISamplePos", samplePosBuffer);
                cmd.SetComputeBufferParam(cs, kSample, "_GISampleNormal", sampleNormalBuffer);
                cmd.SetComputeBufferParam(cs, kSample, "_GISampleOut", sampleOutBuffer);
                cmd.SetComputeIntParam(cs, "_GISampleCount", samplePoints.Count);
                cmd.DispatchCompute(cs, kSample, (samplePoints.Count + 63) / 64, 1, 1);
                int requestFrame = frame;
                sampleRequestPending = true;
                cmd.RequestAsyncReadback(sampleOutBuffer, req =>
                {
                    sampleRequestPending = false;
                    if (req.hasError) return;
                    var data = req.GetData<Vector4>();
                    if (sampleResults.Length != data.Length) sampleResults = new Vector4[data.Length];
                    data.CopyTo(sampleResults); sampleResultFrame = requestFrame;
                });
            }
            cmd.EndSample(probeSampler);
            Graphics.ExecuteCommandBuffer(cmd);
            fullReset = false;
            probesReady = true;
        }

        private void SetRange(Vector2Int r)
        {
            cmd.SetComputeIntParam(probeShader, "_GIProbeOffset", r.x);
            cmd.SetComputeIntParam(probeShader, "_GIProbeUpdateCount", r.y);
        }

        private void EnsureSampleBuffers()
        {
            int n = Mathf.Max(1, samplePoints.Count);
            if (samplePosBuffer != null && samplePosBuffer.count == n) return;
            samplePosBuffer?.Release(); sampleNormalBuffer?.Release(); sampleOutBuffer?.Release();
            samplePosBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            sampleNormalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            sampleOutBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
        }

        private Quaternion RandomRotation()
        {
            // Uniform random rotation (Shoemake).
            double u1 = random.NextDouble(), u2 = random.NextDouble() * 2 * Math.PI, u3 = random.NextDouble() * 2 * Math.PI;
            double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
            return new Quaternion((float)(a * Math.Sin(u2)), (float)(a * Math.Cos(u2)), (float)(b * Math.Sin(u3)), (float)(b * Math.Cos(u3)));
        }

        private void ReadTimings()
        {
            for (int i = 0; i < 4; i++) { var r = kernelSamplers[i].GetRecorder(); if (r.isValid) KernelGpuMs[i] = r.gpuElapsedNanoseconds / 1e6f; }
            if (probeRecorder.isValid) GpuProbeMs = probeRecorder.gpuElapsedNanoseconds / 1e6f;
            if (albedoRecorder.isValid) GpuAlbedoMs = albedoRecorder.gpuElapsedNanoseconds / 1e6f;
            if (compositeRecorder.isValid) GpuCompositeMs = compositeRecorder.gpuElapsedNanoseconds / 1e6f;
        }

        // ---------------------------------------------------------------------------------------
        // Diagnostics API (receiver sampling)
        // ---------------------------------------------------------------------------------------
        public void SetSamplePoints(IList<Vector3> positions, IList<Vector3> normals)
        {
            samplePoints.Clear(); sampleNormals.Clear();
            for (int i = 0; i < positions.Count; i++) { samplePoints.Add(positions[i]); sampleNormals.Add(normals[i]); }
            sampleResultFrame = -1;
        }

        /// <summary>Latest probe irradiance (URP units, = E/pi) at the sample points and the frame it was computed.</summary>
        public Vector4[] GetSampleResults(out int frame) { frame = sampleResultFrame; return sampleResults; }

        // ---------------------------------------------------------------------------------------
        // Rendering integration
        // ---------------------------------------------------------------------------------------
        private void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (!ShouldComposite) return;
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null) return;
            data.scriptableRenderer.EnqueuePass(renderPass);
        }

        private void OnGUI()
        {
            if (!showPanel) return;
            panelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            GUILayout.BeginArea(new Rect(Screen.width - 344, 14, 330, 250), GUI.skin.box);
            GUILayout.Label("DYNAMIC GI: " + (giEnabled ? "ON" : "OFF") + "  |  view: " + debugView, new GUIStyle(panelStyle) { fontStyle = FontStyle.Bold, fontSize = 15 });
            if (GUILayout.Button("G  |  GI " + (giEnabled ? "ON" : "OFF"), GUILayout.Height(26))) giEnabled = !giEnabled;
            if (GUILayout.Button("H  |  Debug view: " + debugView, GUILayout.Height(26))) debugView = (GIDebugView)(((int)debugView + 1) % 3);
            GUILayout.Label($"Probes {StatProbes}  rays/probe {raysPerProbe}  update {probeUpdateFraction:P0}", panelStyle);
            GUILayout.Label($"Instances {StatInstances}  tris {StatTriangles}  BLAS {StatBlas}  lights {StatLights}", panelStyle);
            GUILayout.Label($"GPU probes {GpuProbeMs:F2} ms  albedo {GpuAlbedoMs:F2}  composite {GpuCompositeMs:F2}", panelStyle);
            GUILayout.Label($"CPU GI {CpuUpdateMs:F2} ms  last rebuild {LastRebuildMs:F2} ms  (#{StatRebuilds})", panelStyle);
            GUILayout.Label($"Frames since geometry change: {FramesSinceChange}", panelStyle);
            GUILayout.Label("J hides this panel.", panelStyle);
            GUILayout.EndArea();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct GIInstanceData
    {
        public Vector4 w2o0, w2o1, w2o2;
        public int nodeOffset, triOffset, materialOffset;
        public float winding;
        public const int Stride = 64;
    }
}
