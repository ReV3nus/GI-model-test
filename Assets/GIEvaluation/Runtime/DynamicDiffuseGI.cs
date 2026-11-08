using System;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace GIEvaluation
{
    [DefaultExecutionOrder(300)]
    public sealed class DynamicDiffuseGI : MonoBehaviour
    {
        [Range(1, 16)] public int pathsPerFrame = 16;
        [Range(16, 1024)] public int targetPaths = 1024;
        [Range(.2f, 1f)] public float spacing = .4f;
        public bool showGI = true;
        public bool Converged => geometry != null && Paths >= targetPaths;
        public int Paths { get; private set; }
        public int TriangleCount => geometry?.TriangleCount ?? 0;
        public int NodeCount => geometry?.NodeCount ?? 0;
        public int SampleCount => receivers?.SampleCount ?? 0;
        public long DisplayMeshBytes => receivers?.DisplayMeshBytes ?? 0;
        public double LastRebuildMs { get; private set; }
        public double LastSubmitMs { get; private set; }
        public string Status { get; private set; } = "Waiting";
        public ComputeBuffer Irradiance => irradiance;
        public ComputeBuffer SampleBuffer => receivers?.Samples;
#if UNITY_EDITOR
        public GISurfaceReceivers Receivers => receivers;
#endif

        GIBreachEnvironment environmentInput;
        GITraceGeometry geometry;
        GISurfaceReceivers receivers;
        ComputeBuffer irradiance;
        ComputeShader transport;
        Cubemap black;
        float builtSpacing;
        int lastTarget;
        Light lastSun;
        Vector3 lastDirection;
        Color lastSunlight;
        Cubemap lastSky;
        uint lastSkyVersion;
        float lastSkyIntensity;
        int kernel;
        CommandBuffer commands;
        public CustomSampler TransportMarker { get; private set; }
        bool rebuild = true, restart = true;
        readonly List<Renderer> moved = new List<Renderer>();

        void OnEnable()
        {
            environmentInput = GetComponent<GIBreachEnvironment>();
            if (!environmentInput) environmentInput = FindAnyObjectByType<GIBreachEnvironment>();
            if (!environmentInput || !SystemInfo.supportsComputeShaders)
            {
                Status = "Missing lighting input or compute support";
                enabled = false;
                return;
            }
            transport = Resources.Load<ComputeShader>("GISurfaceTransport");
            if (!transport)
            {
                Status = "Missing transport shader";
                enabled = false;
                return;
            }
            transport = Instantiate(transport);
            kernel = transport.FindKernel("Solve");
            TransportMarker = CustomSampler.Create("GI diffuse rays", true);
            commands = new CommandBuffer { name = "Diffuse transport" };
            black = new Cubemap(1, TextureFormat.RGBA32, false);
            for (int i = 0; i < 6; ++i) black.SetPixel((CubemapFace)i, 0, 0, Color.black);
            black.Apply();
            rebuild = restart = true;
            GeometryChanges.Changed += GeometryChanged;
            environmentInput.Changed += LightingChanged;
        }

        void GeometryChanged(GeometryChange change)
        {
            if (change.Kind == GeometryChangeKind.TransformChanged && change.Renderer)
            {
                if (!moved.Contains(change.Renderer)) moved.Add(change.Renderer);
            }
            else rebuild = true;
        }
        void LightingChanged(GIBreachEnvironment input) => restart = true;

        void LateUpdate()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.gKey.wasPressedThisFrame) showGI = !showGI;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.G)) showGI = !showGI;
#endif
            if (!showGI) return;
            long start = Stopwatch.GetTimestamp();
            var sun = environmentInput.sun;
            var direction = sun ? -sun.transform.forward : Vector3.up;
            Color sunlight = Color.black;
            if (sun && sun.isActiveAndEnabled)
            {
                sunlight = sun.color.linear * sun.intensity;
                if (sun.useColorTemperature) sunlight *= Mathf.CorrelatedColorTemperatureToRGB(sun.colorTemperature);
            }
            var sky = environmentInput.skyRadiance;
            uint version = sky ? sky.updateCount : 0;
            if (direction != lastDirection || sunlight != lastSunlight || lastSun != sun || sky != lastSky ||
                version != lastSkyVersion || environmentInput.EffectiveSkyIntensity != lastSkyIntensity || targetPaths < lastTarget)
                restart = true;
            lastSun = sun; lastDirection = direction; lastSunlight = sunlight;
            lastSky = sky; lastSkyVersion = version; lastSkyIntensity = environmentInput.EffectiveSkyIntensity;
            lastTarget = targetPaths;
            if (spacing != builtSpacing) rebuild = true;
            if (!rebuild && moved.Count != 0)
            {
                rebuild = !geometry.TryRefitTransforms(moved) || !receivers.TryRefitTransforms(moved);
                restart = true;
                LastRebuildMs = Elapsed(start);
            }
            moved.Clear();
            if (rebuild)
            {
                Profiler.BeginSample("Dynamic GI / rebuild");
                try
                {
                    ReleaseGeometry();
                    geometry = GITraceGeometry.Build();
                    receivers = GISurfaceReceivers.Build(spacing);
                    builtSpacing = spacing;
                    irradiance = new ComputeBuffer(Math.Max(1, SampleCount), 16);
                    rebuild = false;
                    restart = true;
                    LastRebuildMs = Elapsed(start);
                }
                catch (Exception ex)
                {
                    Status = "Rebuild failed";
                    UnityEngine.Debug.LogException(ex, this);
                    enabled = false;
                    return;
                }
                finally { Profiler.EndSample(); }
            }
            if (restart) { Paths = 0; restart = false; }
            if (SampleCount > 0 && Paths < targetPaths)
            {
                int count = Math.Min(pathsPerFrame, targetPaths - Paths);
                Profiler.BeginSample("Dynamic GI / submit transport");
                try
                {
                    transport.SetVector("_SunDirection", direction);
                    transport.SetVector("_SunColor", sunlight);
                    transport.SetFloat("_SkyIntensity", environmentInput.EffectiveSkyIntensity);
                    transport.SetTexture(kernel, "_Sky", environmentInput.skyRadiance ? environmentInput.skyRadiance : black);
                    transport.SetInt("_TriangleCount", TriangleCount);
                    transport.SetInt("_SampleCount", SampleCount);
                    transport.SetInt("_StartSample", Paths);
                    transport.SetInt("_RaysPerSample", count);
                    transport.SetBuffer(kernel, "_Nodes", geometry.Nodes);
                    transport.SetBuffer(kernel, "_Triangles", geometry.Triangles);
                    transport.SetBuffer(kernel, "_Samples", receivers.Samples);
                    transport.SetBuffer(kernel, "_Irradiance", irradiance);
                    commands.Clear();
                    commands.BeginSample(TransportMarker);
                    const int groupLimit = 65535;
                    for (int offset = 0; offset < SampleCount; offset += groupLimit * 64)
                    {
                        int groups = Math.Min(groupLimit, (SampleCount - offset + 63) / 64);
                        commands.SetComputeIntParam(transport, "_ReceiverOffset", offset);
                        commands.DispatchCompute(transport, kernel, groups, 1, 1);
                    }
                    commands.EndSample(TransportMarker);
                    Graphics.ExecuteCommandBuffer(commands);
                    Paths += count;
                    Status = Paths >= targetPaths ? "Stable" : "Sampling";
                }
                finally { Profiler.EndSample(); }
            }
            receivers.Display(irradiance, true);
            LastSubmitMs = Elapsed(start);
        }

        static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        void ReleaseGeometry()
        {
            receivers?.Dispose();
            receivers = null;
            geometry?.Dispose();
            geometry = null;
            irradiance?.Release();
            irradiance = null;
        }

        void OnDisable()
        {
            GeometryChanges.Changed -= GeometryChanged;
            if (environmentInput) environmentInput.Changed -= LightingChanged;
            ReleaseGeometry();
            commands?.Release();
            commands = null;
            GITraceGeometry.ClearTextureCache();
            if (black) Destroy(black);
            if (transport) Destroy(transport);
        }
    }
}
