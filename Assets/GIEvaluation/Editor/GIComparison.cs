using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GIEvaluation.Editor
{
    // Runs against the normal Play mode camera and the fixture's public transitions.
    public static class GIComparison
    {
        const string Folder = "GIResults";
        static bool running;
        [MenuItem("GI Evaluation/Record GI comparisons (Play mode)")]
        public static void Run()
        {
            if (!Application.isPlaying || running) throw new InvalidOperationException("Enter Play mode and run one recording at a time.");
            var fixture = UnityEngine.Object.FindAnyObjectByType<GIBreachScenario>();
            fixture.StartCoroutine(Record(fixture));
        }

        [Serializable]
        class Row
        {
            public string image, phase, lighting, state, view, graphics, device, cpu, unity;
            public bool gi, lamps, blocker, preview;
            public int width, height, step, frame, framesSinceChange, paths, triangles, nodes, receivers;
            public double readbackSecondsSinceChange, secondsSinceChange, lastRebuildMs, submitMs;
            public long displayMeshBytes, structuredBufferBytes;
            public Probe[] probes;
        }
        [Serializable]
        class Probe
        {
            public string name;
            public bool sunVisible, surfaceHit;
            public Vector3 surfacePosition, surfaceNormal;
            public Vector3 normalizedIrradiance;
            public float distance;
            public int localSamples;
            public Vector3 localMean;
        }
        struct Sample { public Vector3 position; public float pad; public Vector3 normal; public float pad2; }

        static IEnumerator Record(GIBreachScenario f)
        {
            running = true;
            Directory.CreateDirectory(Folder);
            var g = f.Transport;
            var cam = f.mainCamera;
            var prevTarget = cam.targetTexture;
            int prevRate = Application.targetFrameRate, prevSync = QualitySettings.vSyncCount, prevView = f.View;
            var prevMode = f.environment.lighting;
            var prevState = f.state;
            bool prevLamp = f.environment.interiorLightsEnabled, prevPreview = f.preview, prevBlock = f.ShutterClosed, prevGI = g.showGI;
            var target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            target.Create();
            cam.targetTexture = target;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            var end = new WaitForEndOfFrame();
            var recorder = g.TransportMarker.GetRecorder();
            recorder.enabled = true;
            try
            {
                f.StopTimeline();
                f.SetPreview(false);
                f.SetView(0);
                f.environment.SetInteriorLights(false);
                f.environment.Apply(BreachLighting.SunOnly);
                File.WriteAllText(Path.Combine(Folder, "frames.csv"), "case,mode,count,median_ms,p95_ms,max_ms,mean_cpu_submit_ms,median_gpu_dispatch_ms,gpu_samples\n");
                for (int index = 0; index < 25; index++)
                {
                    g.showGI = true;
                    yield return null;
                    string id;
                    if (index < 7) { f.ApplyState((BreachState)index); id = "step" + (index + 1); }
                    else if (index == 7) { f.ApplyState(BreachState.ConnectedRooms); id = "before-block"; }
                    else if (index == 8) { f.SetShutter(true); id = "blocked"; }
                    else if (index == 9) { f.SetShutter(false); id = "unblocked"; }
                    else if (index == 10) { f.environment.Apply(BreachLighting.SkyOnly); id = "sky"; }
                    else if (index == 11) { f.environment.Apply(BreachLighting.SunAndSky); id = "sun-sky"; }
                    else if (index == 12) { f.SetView(4); id = "view-b"; }
                    else if (index == 13) { f.SetView(0); id = "view-return"; }
                    else if (index == 14) { f.environment.SetInteriorLights(true); id = "lamps"; }
                    else if (index == 15) { f.environment.SetInteriorLights(false); f.SetView(1); f.environment.Apply(BreachLighting.SunOnly); f.ApplyState(BreachState.LargeBreach); id = "b-closed-partition"; }
                    else if (index == 16) { f.ApplyState(BreachState.ConnectedRooms); id = "b-open-partition"; }
                    else if (index == 17) { f.SetView(0); f.ApplyState(BreachState.Sealed); f.environment.Apply(BreachLighting.SunAndSky); id = "sealed-sun-sky"; }
                    else if (index == 18) { f.environment.Apply(BreachLighting.SunOnly); f.ApplyState(BreachState.SmallBreach); id = "small-before-block"; }
                    else if (index == 19) { f.SetShutter(true); id = "small-blocked"; }
                    else if (index == 20) { f.SetShutter(false); id = "small-unblocked"; }
                    else if (index == 21) { f.ApplyState(BreachState.LargeBreach); id = "large-before-block"; }
                    else if (index == 22) { f.SetShutter(true); id = "large-blocked"; }
                    else if (index == 23) { f.SetShutter(false); id = "large-unblocked"; }
                    else { f.ResetFixture(); id = "reset"; }
                    var start = Time.realtimeSinceStartupAsDouble;
                    int frame = Time.frameCount;
                    yield return end;
                    Save(f, target, id, "first", start, frame, false);
                    var costs = new List<double>();
                    var cpu = new List<double>();
                    var gpu = new List<double>();
                    while (!g.Converged)
                    {
                        if (!g.enabled || Time.frameCount - frame > 4096) throw new Exception("Transport did not converge: " + g.Status);
                        yield return null;
                        yield return end;
                        if (Time.frameCount - frame > 2)
                        {
                            costs.Add(Time.unscaledDeltaTime * 1000.0);
                            cpu.Add(g.LastSubmitMs);
                            if (recorder.gpuSampleBlockCount > 0 && recorder.gpuElapsedNanoseconds > 0)
                                gpu.Add(recorder.gpuElapsedNanoseconds / 1000000.0);
                        }
                    }
                    Stats(id, "sampling", costs, cpu, gpu);
                    Save(f, target, id, "on", start, frame, true);
                    if (index == 3 || index == 5)
                        yield return Measure(id, "stable", g);
                    g.showGI = false;
                    yield return null;
                    yield return end;
                    Save(f, target, id, "off", start, frame, false);
                    if (index == 3 || index == 5)
                        yield return Measure(id, "off", g);
                    Debug.Log("[GI comparison] " + id + " recorded");
                }
                Debug.Log("[GI comparison] Completed all cases.");
            }
            finally
            {
                recorder.enabled = false;
                cam.targetTexture = prevTarget;
                target.Release(); UnityEngine.Object.Destroy(target);
                Application.targetFrameRate = prevRate;
                QualitySettings.vSyncCount = prevSync;
                f.SetPreview(prevPreview);
                f.environment.SetInteriorLights(prevLamp); f.environment.Apply(prevMode);
                f.ApplyState(prevState); f.SetShutter(prevBlock); f.SetView(prevView);
                g.showGI = prevGI; running = false;
            }
        }

        static IEnumerator Measure(string id, string mode, DynamicDiffuseGI g)
        {
            for (int i = 0; i < 10; i++) yield return null;
            var wall = new List<double>(); var cpu = new List<double>();
            for (int i = 0; i < 45; i++)
            {
                yield return null;
                wall.Add(Time.unscaledDeltaTime * 1000.0);
                cpu.Add(g.showGI ? g.LastSubmitMs : 0);
            }
            Stats(id, mode, wall, cpu);
        }

        static void Stats(string id, string mode, List<double> wall, List<double> cpu, List<double> gpu = null)
        {
            if (wall.Count == 0) return;
            wall.Sort();
            gpu?.Sort();
            File.AppendAllText(Path.Combine(Folder, "frames.csv"), string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3:F3},{4:F3},{5:F3},{6:F3},{7},{8}\n", id, mode, wall.Count, wall[wall.Count / 2],
                wall[Math.Min(wall.Count - 1, (int)(wall.Count * .95))], wall.Last(), cpu.Average(), gpu?.Count > 0 ? gpu[gpu.Count / 2].ToString("F3", CultureInfo.InvariantCulture) : "", gpu?.Count ?? 0));
        }

        static void Save(GIBreachScenario f, RenderTexture target, string id, string phase, double changeTime, int changeFrame, bool probes)
        {
            var g = f.Transport;
            var row = new Row {
                image = id + "-" + phase + ".png", phase = phase, state = f.state.ToString(),
                step = (int)f.state + 1, lighting = f.environment.lighting.ToString(), preview = f.preview,
                lamps = f.environment.interiorLightsEnabled, view = f.viewNames[f.View], blocker = f.ShutterClosed,
                width = target.width, height = target.height, gi = g.showGI, frame = Time.frameCount,
                framesSinceChange = Time.frameCount - changeFrame, secondsSinceChange = Time.realtimeSinceStartupAsDouble - changeTime,
                paths = g.Paths, triangles = g.TriangleCount, receivers = g.SampleCount, nodes = g.NodeCount,
                displayMeshBytes = g.DisplayMeshBytes, structuredBufferBytes = 32L*g.NodeCount + 48L*g.TriangleCount + 48L*g.SampleCount,
                lastRebuildMs = g.LastRebuildMs, submitMs = g.showGI ? g.LastSubmitMs : 0, graphics = SystemInfo.graphicsDeviceType.ToString(),
                device = SystemInfo.graphicsDeviceName, cpu = SystemInfo.processorType, unity = Application.unityVersion
            };
            var old = RenderTexture.active;
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            row.readbackSecondsSinceChange = Time.realtimeSinceStartupAsDouble - changeTime;
            RenderTexture.active = old;
            File.WriteAllBytes(Path.Combine(Folder,row.image), image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
            if (probes)
            {
                var samples = new Sample[g.SampleCount];
                g.SampleBuffer.GetData(samples);
                var lighting = new Vector4[g.SampleCount]; g.Irradiance.GetData(lighting);
                row.probes = f.receivers.Select(p => {
                    bool hit = g.Receivers.MeasureSurface(p.point.position, p.normal, lighting,
                        out var position, out var normal, out var irradiance);
                    int localCount = 0; Vector3 sum = Vector3.zero;
                    for (int i=0; i<samples.Length; i++)
                    {
                        float dist = Vector3.Distance(samples[i].position, p.point.position);
                        if (Vector3.Dot(samples[i].normal,p.normal) < .9f) continue;
                        if (dist <= .5f && Mathf.Abs(Vector3.Dot(samples[i].position - p.point.position, p.normal)) < .035f)
                        { localCount++; sum += (Vector3)lighting[i]; }
                    }
                    return new Probe { name=p.name, surfaceHit=hit, surfacePosition=position, surfaceNormal=normal,
                        distance=hit ? Vector3.Distance(position,p.point.position) : -1f, localSamples=localCount, localMean=sum/Mathf.Max(1,localCount), sunVisible=f.SunVisibleAt(p.name),
                        normalizedIrradiance=irradiance };
                }).ToArray();
            }
            File.WriteAllText(Path.Combine(Folder,id+"-"+phase+".json"),JsonUtility.ToJson(row,true));
        }
    }
}
