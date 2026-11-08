using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GIEvaluation.Editor
{
    // Paired transport measurement. The neutral comparison changes only the reflectance
    // of traced donor surfaces on a private buffer. No scene material or light is edited.
    public static class GIColorTransfer
    {
        const int Paths = 16384;
        const int Batch = 128;

        [StructLayout(LayoutKind.Sequential)]
        struct Triangle
        {
            public Vector3 origin;
            public float r;
            public Vector3 edge1;
            public float g;
            public Vector3 edge2;
            public float b;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Sample
        {
            public Vector3 position;
            public float pad0;
            public Vector3 normal;
            public float pad1;
        }

        [Serializable]
        class Comparison
        {
            public string step, lighting, view, unity, gpu, graphics;
            public bool preview, interiorLights, shutter;
            public Vector3 sunColorLinear, sunDirection;
            public int pathsPerProbe, triangleCount, donorTrianglesWithChroma;
            public string method = "Same production compute kernel and deterministic directions at each exact fixture receiver. Neutral donors use Rec.709 luminance (linear RGB) in every channel. Results are incident irradiance / pi, before multiplying by receiver albedo.";
            public Probe[] probes;
        }

        [Serializable]
        class Probe
        {
            public string name;
            public Vector3 position, normal, actual, neutral, difference;
            public bool sunVisible;
        }

        [MenuItem("GI Evaluation/Measure donor color transfer (Play mode)")]
        public static void Run()
        {
            var fixture = Object.FindAnyObjectByType<GIBreachScenario>();
            if (!Application.isPlaying || !fixture || fixture.TimelineRunning)
                throw new InvalidOperationException("Enter Play mode in the fixture and stop the timeline first.");

            var environment = fixture.environment;
            var previousState = fixture.state;
            var previousLighting = environment.lighting;
            int previousRoof = fixture.RoofOpening, previousView = fixture.View;
            bool previousPreview = fixture.preview, previousLamps = environment.interiorLightsEnabled;
            bool previousBlock = fixture.ShutterClosed;
            try
            {
                fixture.SetView(0);
                fixture.SetPreview(false);
                environment.SetInteriorLights(false);
                environment.Apply(BreachLighting.SunOnly);
                fixture.ApplyState(BreachState.ConnectedRooms);
                Record(fixture);
            }
            finally
            {
                fixture.ApplyState(previousState);
                fixture.SetRoofOpening(previousRoof);
                fixture.SetShutter(previousBlock);
                fixture.SetPreview(previousPreview);
                environment.SetInteriorLights(previousLamps);
                environment.Apply(previousLighting);
                fixture.SetView(previousView);
            }
        }

        static void Record(GIBreachScenario fixture)
        {
            var environment = fixture.environment;
            var samples = new Sample[fixture.receivers.Length];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = new Sample { position = fixture.receivers[i].point.position, normal = fixture.receivers[i].normal.normalized };
            var sun = environment.sun;
            var sunColor = sun.color.linear * sun.intensity;
            if (sun.useColorTemperature) sunColor *= Mathf.CorrelatedColorTemperatureToRGB(sun.colorTemperature);
            using var geometry = GITraceGeometry.Build();
            using var sampleBuffer = new ComputeBuffer(samples.Length, 32);
            using var output = new ComputeBuffer(samples.Length, 16);
            using var grayDonors = new ComputeBuffer(geometry.TriangleCount, 48);
            sampleBuffer.SetData(samples);
            var tris = new Triangle[geometry.TriangleCount];
            geometry.Triangles.GetData(tris);
            int chromatic = 0;
            for (int i = 0; i < tris.Length; i++)
            {
                ref var tri = ref tris[i];
                if (Mathf.Abs(tri.r - tri.g) > 0.001f || Mathf.Abs(tri.g - tri.b) > 0.001f) chromatic++;
                float y = .2126f * tri.r + .7152f * tri.g + .0722f * tri.b;
                tri.r = tri.g = tri.b = y;
            }
            grayDonors.SetData(tris);

            var cs = Object.Instantiate(Resources.Load<ComputeShader>("GISurfaceTransport"));
            try
            {
                int kernel = cs.FindKernel("Solve");
                cs.SetInt("_TriangleCount", geometry.TriangleCount);
                cs.SetInt("_SampleCount", samples.Length);
                cs.SetInt("_ReceiverOffset", 0);
                cs.SetInt("_ReceiverEnd", samples.Length);
                cs.SetInt("_RaysPerSample", Batch);
                cs.SetVector("_SunDirection", -sun.transform.forward);
                cs.SetVector("_SunColor", sunColor);
                cs.SetFloat("_SkyIntensity", 0);
                cs.SetTexture(kernel, "_Sky", environment.skyRadiance);
                cs.SetBuffer(kernel, "_Nodes", geometry.Nodes);
                cs.SetBuffer(kernel, "_Samples", sampleBuffer);
                cs.SetBuffer(kernel, "_Irradiance", output);

                Vector4[] Solve(ComputeBuffer donors)
                {
                    cs.SetBuffer(kernel, "_Triangles", donors);
                    for (int start = 0; start < Paths; start += Batch)
                    {
                        cs.SetInt("_StartSample", start);
                        cs.Dispatch(kernel, (samples.Length + 63) / 64, 1, 1);
                    }
                    var values = new Vector4[samples.Length];
                    output.GetData(values);
                    return values;
                }

                var actual = Solve(geometry.Triangles);
                var neutral = Solve(grayDonors);
                var result = new Comparison {
                    step = fixture.state.ToString(), lighting = environment.lighting.ToString(), view = fixture.viewNames[fixture.View],
                    unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName, graphics = SystemInfo.graphicsDeviceType.ToString(),
                    preview = fixture.preview, interiorLights = environment.interiorLightsEnabled, shutter = fixture.ShutterClosed,
                    sunDirection = -sun.transform.forward, sunColorLinear = new Vector3(sunColor.r, sunColor.g, sunColor.b),
                    triangleCount = geometry.TriangleCount, donorTrianglesWithChroma = chromatic,
                    pathsPerProbe = Paths, probes = new Probe[samples.Length]
                };
                for (int i = 0; i < samples.Length; i++)
                {
                    string name = fixture.receivers[i].name;
                    result.probes[i] = new Probe { name = name, position = samples[i].position, normal = samples[i].normal,
                        sunVisible = fixture.SunVisibleAt(name), actual = actual[i], neutral = neutral[i], difference = actual[i] - neutral[i] };
                }
                Directory.CreateDirectory("GIResults");
                File.WriteAllText(Path.Combine("GIResults", "color-transfer.json"), JsonUtility.ToJson(result, true));
                Debug.Log("[GI comparison] Donor color transfer recorded.");
            }
            finally
            {
                Object.Destroy(cs);
            }
        }
    }
}
