using System;
using System.Diagnostics;
using System.Collections.Generic;
using GIEvaluation;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DynamicGI
{
    /// <summary>World-space triangle path tracing. Screen coordinates only select the receiving surface.</summary>
    [DefaultExecutionOrder(500)]
    public sealed class DynamicGIController : MonoBehaviour
    {
        public static DynamicGIController Instance { get; private set; }
        public bool GIEnabled = true, IndirectOnly;
        [Range(96, 1920)] public int Width = 480;
        [Range(54, 1080)] public int Height = 270;
        [Range(1, 8)] public int SamplesPerPixel = 4;
        [Range(1, 4)] public int BounceCount = 2;
        [Range(0, 3)] public float Intensity = 1;
        [Min(.0001f)] public float RayBias = .003f;
        [Min(1)] public float MaxDistance = 200;
        public GIBreachEnvironment Environment;
        public Camera TargetCamera;
        public RenderTexture Output { get; private set; }
        public int SampleIndex { get; private set; }
        public double LastBuildMilliseconds { get; private set; }
        public double LastDispatchMilliseconds { get; private set; }
        public double LastInvalidationTime { get; private set; }
        public int LastInvalidationFrame { get; private set; }
        public string LastInvalidationReason { get; private set; }
        public int TriangleCount => geometry?.TriangleCount ?? 0;
        public int NodeCount => geometry?.NodeCount ?? 0;
        public long BufferBytes => (geometry?.BufferBytes ?? 0) + (long)Width * Height * 16 * 2;
        public int BuildCount { get; private set; }
        public bool IsReady => trace && Output && TriangleCount > 0;
        GIGeometry geometry;
        ComputeShader trace;
        RenderTexture spare;
        Cubemap blackSky;
        bool geometryDirty = true;
        int signature, lightSignature, qualitySignature;
        float nextScan;
        Matrix4x4 previousView, previousProjection;
        readonly Stopwatch timer = new Stopwatch();
        readonly HashSet<int> pendingRemovals = new HashSet<int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindAnyObjectByType<DynamicGIController>()) return;
            var input = FindAnyObjectByType<GIBreachEnvironment>();
            if (input) input.gameObject.AddComponent<DynamicGIController>();
        }
        void OnEnable()
        {
            Instance = this;
            Environment = Environment ? Environment : FindAnyObjectByType<GIBreachEnvironment>();
            var fixture = FindAnyObjectByType<GIBreachScenario>();
            TargetCamera = TargetCamera ? TargetCamera : fixture ? fixture.mainCamera : Camera.main;
            trace = Resources.Load<ComputeShader>("DynamicGITrace");
            if (!SystemInfo.supportsComputeShaders || !trace)
            {
                UnityEngine.Debug.LogError("Dynamic GI requires compute shaders and DynamicGITrace resource.");
                enabled = false; return;
            }
            geometry = new GIGeometry();
            blackSky = new Cubemap(1, TextureFormat.RGBAFloat, false);
            for (int f = 0; f < 6; f++) blackSky.SetPixel((CubemapFace)f, 0, 0, Color.black);
            blackSky.Apply();
            GeometryChanges.Changed += GeometryChanged;
            if (Environment) Environment.Changed += LightChanged;
            geometryDirty = true;
            Invalidate("startup");
        }
        void GeometryChanged(GeometryChange c)
        {
            if (c.Kind == GeometryChangeKind.SceneReset) pendingRemovals.Clear();
            else if (c.Renderer)
            {
                int id = c.Renderer.GetEntityId().GetHashCode();
                if (c.Kind == GeometryChangeKind.Removed) pendingRemovals.Add(id);
                if (c.Kind == GeometryChangeKind.Added) pendingRemovals.Remove(id);
            }
            geometryDirty = true; Invalidate(c.Kind.ToString());
        }
        void LightChanged(GIBreachEnvironment e) => Invalidate("lighting event");
        public void ResetHistory() => Invalidate("explicit reset");
        void Invalidate(string reason)
        {
            SampleIndex = 0;
            LastInvalidationTime = Time.realtimeSinceStartupAsDouble;
            LastInvalidationFrame = Time.frameCount;
            LastInvalidationReason = reason;
        }
        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null && k.gKey.wasPressedThisFrame) GIEnabled = !GIEnabled;
            if (k != null && k.jKey.wasPressedThisFrame) IndirectOnly = !IndirectOnly;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.G)) GIEnabled = !GIEnabled;
            if (Input.GetKeyDown(KeyCode.J)) IndirectOnly = !IndirectOnly;
#endif
        }
        int LightingSignature()
        {
            if (!Environment) return 0;
            unchecked
            {
                int h = Environment.EffectiveSkyIntensity.GetHashCode();
                h = h * 31 + (Environment.skyRadiance ? Environment.skyRadiance.GetEntityId().GetHashCode() : 0);
                var sun = Environment.sun;
                if (sun) { h = h * 31 + sun.transform.rotation.GetHashCode(); h = h * 31 + sun.color.GetHashCode(); h = h * 31 + sun.intensity.GetHashCode(); h = h * 31 + sun.isActiveAndEnabled.GetHashCode(); }
                return h;
            }
        }
        void LateUpdate()
        {
            if (!TargetCamera || !Environment || geometry == null || !trace) return;
            Width = Mathf.Max(8, Width); Height = Mathf.Max(8, Height);
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + .5f;
                int current = GIGeometry.ComputeSceneSignature();
                if (current != signature) { signature = current; geometryDirty = true; Invalidate("scene polling"); }
            }
            if (geometryDirty)
            {
                geometry.Rebuild(pendingRemovals); pendingRemovals.Clear(); geometryDirty = false; BuildCount++;
                LastBuildMilliseconds = geometry.BuildMilliseconds;
                signature = GIGeometry.ComputeSceneSignature();
                Invalidate("geometry rebuilt");
            }
            int light = LightingSignature();
            if (light != lightSignature) { lightSignature = light; Invalidate("lighting changed"); }
            var view = TargetCamera.cameraToWorldMatrix;
            var projection = TargetCamera.projectionMatrix;
            if (view != previousView || projection != previousProjection)
            { previousView = view; previousProjection = projection; Invalidate("camera changed"); }
            int quality = SamplesPerPixel * 17 + BounceCount * 123 + RayBias.GetHashCode() + MaxDistance.GetHashCode();
            if (quality != qualitySignature) { qualitySignature = quality; Invalidate("quality changed"); }
            if (!Output || Output.width != Width || Output.height != Height)
            {
                ReleaseTextures(); Output = MakeTexture("Dynamic GI A"); spare = MakeTexture("Dynamic GI B"); Invalidate("resolution changed");
            }
            if (!GIEnabled || TriangleCount == 0) { LastDispatchMilliseconds = 0; return; }
            timer.Restart();
            const int kernel = 0;
            trace.SetBuffer(kernel, "_Triangles", geometry.Triangles);
            trace.SetBuffer(kernel, "_Nodes", geometry.Nodes);
            trace.SetInt("_TriangleCount", TriangleCount); trace.SetInt("_NodeCount", NodeCount);
            trace.SetInts("_Size", Width, Height);
            trace.SetInt("_SampleIndex", SampleIndex);
            trace.SetInt("_SamplesPerPixel", Mathf.Clamp(SamplesPerPixel, 1, 8));
            trace.SetInt("_BounceCount", Mathf.Clamp(BounceCount, 1, 4));
            trace.SetMatrix("_CameraToWorld", view); trace.SetMatrix("_CameraInverseProjection", projection.inverse);
            trace.SetFloat("_MaxDistance", MaxDistance); trace.SetFloat("_RayBias", RayBias);
            var sun = Environment.sun;
            Color radiance = sun && sun.isActiveAndEnabled ? sun.color.linear * sun.intensity : Color.black;
            if (sun && sun.useColorTemperature) radiance *= Mathf.CorrelatedColorTemperatureToRGB(sun.colorTemperature);
            trace.SetVector("_SunDirection", sun ? -sun.transform.forward : Vector3.up);
            trace.SetVector("_SunRadiance", new Vector4(radiance.r, radiance.g, radiance.b, 0));
            trace.SetFloat("_SkyIntensity", Environment.EffectiveSkyIntensity);
            trace.SetTexture(kernel, "_Sky", Environment.skyRadiance ? Environment.skyRadiance : blackSky);
            trace.SetTexture(kernel, "_History", Output); trace.SetTexture(kernel, "_Output", spare);
            trace.Dispatch(kernel, (Width + 7) / 8, (Height + 7) / 8, 1);
            var old = Output; Output = spare; spare = old;
            SampleIndex++;
            timer.Stop(); LastDispatchMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
        RenderTexture MakeTexture(string label)
        {
            var rt = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { name = label, enableRandomWrite = true, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            rt.Create(); return rt;
        }
        /// <summary>Diagnostic only: white Lambertian receivers evaluated against the
        /// same world BVH, independent of the camera. Readback synchronizes the GPU.</summary>
        public Color[] SampleProbes(Vector3[] positions, Vector3[] normals, int samples = 8192, bool bounceOnly = false)
        {
            if (positions == null || normals == null || positions.Length != normals.Length)
                throw new ArgumentException("Probe positions and normals must have equal length.");
            var result = new Color[positions.Length];
            if (positions.Length == 0 || !IsReady) return result;
            int kernel = trace.FindKernel("CSProbe");
            var points = new Vector4[positions.Length]; var directions = new Vector4[positions.Length];
            for (int i = 0; i < points.Length; i++) { points[i] = positions[i]; directions[i] = normals[i].normalized; }
            using (var p = new ComputeBuffer(points.Length, 16))
            using (var n = new ComputeBuffer(points.Length, 16))
            using (var r = new ComputeBuffer(points.Length, 16))
            {
                p.SetData(points); n.SetData(directions);
                trace.SetBuffer(kernel, "_Triangles", geometry.Triangles); trace.SetBuffer(kernel, "_Nodes", geometry.Nodes);
                trace.SetTexture(kernel, "_Sky", Environment.skyRadiance ? Environment.skyRadiance : blackSky);
                trace.SetBuffer(kernel, "_ProbePositions", p); trace.SetBuffer(kernel, "_ProbeNormals", n);
                trace.SetBuffer(kernel, "_ProbeResults", r);
                trace.SetInt("_ProbeCount", points.Length); trace.SetInt("_ProbeSamples", Mathf.Clamp(samples, 1, 262144));
                trace.SetInt("_ProbeBounceOnly", bounceOnly ? 1 : 0);
                trace.Dispatch(kernel, (points.Length + 7) / 8, 1, 1); r.GetData(result);
            }
            return result;
        }
        void ReleaseTextures()
        {
            if (Output) { Output.Release(); Destroy(Output); } if (spare) { spare.Release(); Destroy(spare); }
            Output = null; spare = null;
        }
        void OnDisable()
        {
            GeometryChanges.Changed -= GeometryChanged;
            if (Environment) Environment.Changed -= LightChanged;
            geometry?.Dispose(); geometry = null; ReleaseTextures();
            if (blackSky) Destroy(blackSky);
            if (Instance == this) Instance = null;
        }
        void OnGUI()
        {
            var area = new Rect(Screen.width - 285, 12, 275, 130);
            GUILayout.BeginArea(area, GUI.skin.box);
            GIEnabled = GUILayout.Toggle(GIEnabled, "G  Dynamic diffuse GI");
            IndirectOnly = GUILayout.Toggle(IndirectOnly, "J  Indirect / sky diffuse only");
            GUILayout.Label($"{Width} x {Height} | {SampleIndex * SamplesPerPixel} spp | {BounceCount} bounces");
            GUILayout.Label($"Triangles {TriangleCount:N0} | BVH {NodeCount:N0}");
            GUILayout.Label($"Build {LastBuildMilliseconds:F1} ms | submit {LastDispatchMilliseconds:F2} ms");
            GUILayout.EndArea();
        }
    }
}

