using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GIEvaluation;
using UnityEngine;

namespace DynamicGI.Evaluation
{
    // Automated evaluation of the dynamic GI system on the existing fixture (development / reporting tool).
    // Drives the fixture through the TASK.md test table, captures fixed-resolution images and samples
    // the probe irradiance at the fixture receivers every frame to measure response and settling.
    public sealed class DynamicGIEvaluationRunner : MonoBehaviour
    {
        public static DynamicGIEvaluationRunner Current;
        public static string Status = "idle";
        public string outputFolder;
        public int width = 1920, height = 1080;
        public float settleSeconds = 4f;
        public bool quick;

        private GIBreachScenario fixture;
        private DynamicGISystem gi;
        private readonly List<string> receiverNames = new List<string>();
        private readonly List<Vector3> receiverPos = new List<Vector3>(), receiverNormal = new List<Vector3>();
        private readonly StringBuilder log = new StringBuilder();
        private readonly List<string> jsonRecords = new List<string>();
        private readonly List<string> seriesCsv = new List<string>();

        public static string Run(string args)
        {
            if (!Application.isPlaying) return "ERROR enter play mode first";
            if (Current) return "ERROR already running: " + Status;
            var go = new GameObject("DynamicGI Evaluation Runner");
            Current = go.AddComponent<DynamicGIEvaluationRunner>();
            Current.quick = args == "quick";
            Current.outputFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "../Captures/DynamicGI/eval"));
            Current.StartCoroutine(Current.Main());
            return "started -> " + Current.outputFolder;
        }

        public static string GetStatus() => Status;

        private IEnumerator Main()
        {
            Status = "setup";
            Directory.CreateDirectory(outputFolder);
            fixture = FindAnyObjectByType<GIBreachScenario>();
            gi = DynamicGISystem.Active;
            SetupReceivers();
            gi.SetSamplePoints(receiverPos, receiverNormal);
            gi.showPanel = false;
            fixture.StopTimeline();
            fixture.SetPreview(false);
            fixture.SetView(0);
            Log($"Unity {Application.unityVersion} | {SystemInfo.graphicsDeviceName} | {SystemInfo.graphicsDeviceType} | CPU {SystemInfo.processorType}");
            Log($"Capture {width}x{height}, settle {settleSeconds}s, targetFrameRate {Application.targetFrameRate}, vSync {QualitySettings.vSyncCount}");
            Log($"GI probes {gi.StatProbes}, rays/probe {gi.raysPerProbe}, fixed rays {gi.fixedRays}, cascades {string.Join(" | ", gi.cascades.Select(c => $"{c.counts.x}x{c.counts.y}x{c.counts.z}@{c.spacing}m"))}");
            yield return Wait(1f);

            // ---------- A: seven steps, fixed camera, sun+sky, interior lamps OFF ----------
            fixture.environment.SetInteriorLights(false);
            fixture.environment.Apply(BreachLighting.SunAndSky);
            fixture.ApplyState(BreachState.Sealed);
            yield return Wait(settleSeconds);
            for (int step = 1; step <= 7; step++)
            {
                Status = "steps " + step;
                yield return ChangeAndRecord($"A_step{step}", () => fixture.ApplyState((BreachState)(step - 1)), true);
            }

            // ---------- A2: seven steps with interior lamps ON (baseline lighting of the fixture) ----------
            if (!quick)
            {
                fixture.environment.SetInteriorLights(true);
                fixture.ApplyState(BreachState.Sealed);
                yield return Wait(settleSeconds);
                for (int step = 1; step <= 7; step++)
                {
                    Status = "steps lamps on " + step;
                    yield return ChangeAndRecord($"A2_lamps_step{step}", () => fixture.ApplyState((BreachState)(step - 1)), step == 1 || step == 4 || step == 7);
                }
                fixture.environment.SetInteriorLights(false);
            }

            // ---------- B: outside blocker in steps 2-4 (roof intact) ----------
            foreach (int step in new[] { 2, 3, 4 })
            {
                Status = "blocker " + step;
                fixture.ApplyState((BreachState)(step - 1));
                yield return Wait(settleSeconds);
                yield return ChangeAndRecord($"B_step{step}_blocked", () => fixture.SetShutter(true), true);
                yield return ChangeAndRecord($"B_step{step}_unblocked", () => fixture.SetShutter(false), true);
            }

            // ---------- C: lighting modes (E) x interior lamps (I) in step 4 ----------
            fixture.ApplyState(BreachState.ConnectedRooms);
            yield return Wait(settleSeconds);
            foreach (var mode in new[] { BreachLighting.SunOnly, BreachLighting.SkyOnly, BreachLighting.SunAndSky })
                foreach (bool lamps in new[] { false, true })
                {
                    Status = "lighting " + mode + " lamps " + lamps;
                    yield return ChangeAndRecord($"C_step4_{mode}_lamps{(lamps ? "On" : "Off")}", () => { fixture.environment.SetInteriorLights(lamps); fixture.environment.Apply(mode); }, true);
                }
            fixture.environment.SetInteriorLights(false);
            fixture.environment.Apply(BreachLighting.SunAndSky);

            // ---------- D: camera views (V) - world-space consistency and off-screen contribution ----------
            fixture.ApplyState(BreachState.LargeRoofBreach);
            yield return Wait(settleSeconds);
            var reference = SampleNow();
            for (int v = 0; v < fixture.viewPositions.Length; v++)
            {
                Status = "view " + v;
                fixture.SetView(v);
                yield return null; yield return null;
                var s0 = SampleNow();
                yield return Wait(1f);
                var s1 = SampleNow();
                yield return CaptureSet($"D_step6_view{v}", true, true);
                Record($"D_step6_view{v}", new Dictionary<string, string>
                {
                    ["view"] = Q(fixture.viewNames[v]),
                    ["receiversRefBeforeViewChange"] = Vec(reference),
                    ["receiversAfter2Frames"] = Vec(s0), ["receiversAfter1s"] = Vec(s1),
                    ["maxRelDiffVsReference"] = F(MaxRelDiff(reference, s1)),
                });
            }
            fixture.SetView(0);

            // Probe temporal noise at convergence (per-frame variation of receiver irradiance, static scene).
            Status = "noise";
            fixture.ApplyState(BreachState.LargeBreach);
            yield return Wait(settleSeconds);
            var noise = new List<float[]>();
            for (int i = 0; i < 90; i++) { yield return null; noise.Add(SampleNow()); }
            var noiseStats = new StringBuilder();
            for (int r = 0; r < receiverNames.Count; r++)
            {
                var vals = noise.Select(x => x[r]).ToArray();
                float mean = vals.Average(), sd = Mathf.Sqrt(vals.Select(x => (x - mean) * (x - mean)).Average());
                noiseStats.Append($"{receiverNames[r]}: mean {mean:F4} sd {sd:F5} ({(mean > 1e-5f ? sd / mean * 100 : 0):F2}%); ");
            }
            Log("Temporal noise step3 (90 frames): " + noiseStats);

            // ---------- E: restore comparison: 1 vs 7 after convergence ----------
            Status = "restore";
            fixture.environment.SetInteriorLights(false);
            fixture.ApplyState(BreachState.Sealed);
            yield return Wait(settleSeconds);
            var sealedVals = SampleNow();
            yield return CaptureSet("E_sealed_reference", true, false);
            fixture.ApplyState(BreachState.LargeRoofBreach);
            yield return Wait(settleSeconds);
            fixture.ApplyState(BreachState.Restored);
            yield return Wait(settleSeconds);
            var restoredVals = SampleNow();
            yield return CaptureSet("E_restored_after_step6", true, false);
            Record("E_restore", new Dictionary<string, string>
            {
                ["sealed"] = Vec(sealedVals), ["restored"] = Vec(restoredVals), ["maxAbsDiff"] = F(MaxAbsDiff(sealedVals, restoredVals)),
                ["imageMeanAbsDiff8bit"] = F(ImageDiff(Path.Combine(outputFolder, "E_sealed_reference_gi.png"), Path.Combine(outputFolder, "E_restored_after_step6_gi.png"))),
            });

            // ---------- G: generic geometry edits outside the fixture's seven steps ----------
            yield return GenericGeometry();

            // ---------- F: performance (frame limit removed while measuring) ----------
            Status = "performance";
            yield return Performance();

            WriteOutputs();
            Status = "done";
            gi.showPanel = true;
            Current = null;
            Destroy(gameObject);
        }

        // Arbitrary meshes / transforms that the fixture never touches: verifies that the GI reacts to
        // plain renderer input (polling for unpublished transform edits, rescans for new objects,
        // MeshChanged for in-place vertex edits) and not to fixture state numbers.
        private IEnumerator GenericGeometry()
        {
            fixture.environment.SetInteriorLights(true);
            fixture.environment.Apply(BreachLighting.SunAndSky);
            fixture.ApplyState(BreachState.ConnectedRooms);
            fixture.SetView(4);   // "Indirect receiver B"
            yield return Wait(settleSeconds);

            // G1: move an existing occluder without publishing an event.
            var occluder = GameObject.Find("Moving Occluder");
            if (occluder)
            {
                var start = occluder.transform.position;
                Status = "generic move";
                yield return ChangeAndRecord("G1_move_occluder_unpublished", () => occluder.transform.position = start + new Vector3(-1.6f, 0, 1.4f), true);
                yield return ChangeAndRecord("G1_move_occluder_back", () => occluder.transform.position = start, false);
            }

            // G2: add a new coloured object at runtime (no event; found by the periodic rescan), then destroy it.
            Status = "generic add";
            GameObject added = null;
            Material addedMat = null;
            yield return ChangeAndRecord("G2_add_red_panel", () =>
            {
                added = GameObject.CreatePrimitive(PrimitiveType.Cube);
                added.name = "Runtime Added Panel (test)";
                Destroy(added.GetComponent<Collider>());
                added.transform.position = new Vector3(3.65f, 1.2f, 6.9f);
                added.transform.localScale = new Vector3(1.6f, 1.8f, 0.08f);
                addedMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                addedMat.SetColor("_BaseColor", new Color(0.85f, 0.1f, 0.05f));
                added.GetComponent<MeshRenderer>().sharedMaterial = addedMat;
            }, true);
            yield return ChangeAndRecord("G2_remove_red_panel", () => Destroy(added), true);

            // G3: in-place vertex edit of a runtime mesh copy, announced with MeshChanged.
            var deform = GameObject.Find("Runtime Deformable Mesh");
            if (deform)
            {
                Status = "generic deform";
                var filter = deform.GetComponent<MeshFilter>();
                var original = filter.sharedMesh;
                var copy = Instantiate(original); copy.name = original.name + " (runtime copy)";
                filter.sharedMesh = copy;
                yield return Wait(1f);
                var verts = copy.vertices;
                yield return ChangeAndRecord("G3_deform_mesh_in_place", () =>
                {
                    var r = deform.GetComponent<Renderer>(); var before = r.bounds;
                    for (int i = 0; i < verts.Length; i++) verts[i] = new Vector3(verts[i].x * 1.9f, verts[i].y * 1.25f, verts[i].z * 1.9f);
                    copy.vertices = verts; copy.RecalculateBounds(); copy.RecalculateNormals();
                    GeometryChanges.Publish(GeometryChangeKind.MeshChanged, r, before);
                }, true);
                yield return ChangeAndRecord("G3_restore_original_mesh", () =>
                {
                    var r = deform.GetComponent<Renderer>(); var before = r.bounds;
                    filter.sharedMesh = original;
                    GeometryChanges.Publish(GeometryChangeKind.MeshChanged, r, before);
                }, false);
                Destroy(copy);
            }
            if (addedMat) Destroy(addedMat);
            fixture.SetView(0);
            fixture.environment.SetInteriorLights(false);
        }

        // ------------------------------------------------------------------------------------------
        private void SetupReceivers()
        {
            foreach (var r in fixture.receivers)
            {
                receiverNames.Add(r.name); receiverPos.Add(r.point.position + r.normal * 0.02f); receiverNormal.Add(r.normal);
            }
            // Extra sample points present in the scene (named sample transforms; test tooling only, not used by the GI).
            AddSample("GI Sample - Rear Alcove", Vector3.back);
            AddSample("GI Sample - Under Bench", Vector3.up);
        }

        private void AddSample(string objectName, Vector3 normal)
        {
            var t = GameObject.Find(objectName);
            if (!t) return;
            receiverNames.Add(objectName.Replace("GI Sample - ", "").Replace(" ", "_"));
            receiverPos.Add(t.transform.position + normal * 0.02f); receiverNormal.Add(normal);
        }

        private float[] SampleNow()
        {
            var res = gi.GetSampleResults(out _);
            var v = new float[receiverNames.Count];
            for (int i = 0; i < v.Length && i < res.Length; i++) v[i] = 0.2126f * res[i].x + 0.7152f * res[i].y + 0.0722f * res[i].z;
            return v;
        }

        private IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        // Applies a change, records receiver irradiance every frame for settleSeconds, captures first/converged frames.
        private IEnumerator ChangeAndRecord(string name, Action change, bool captureFirst)
        {
            var before = SampleNow();
            int startFrame = Time.frameCount; double startTime = Time.realtimeSinceStartupAsDouble;
            change();
            var series = new List<(int frame, double t, float[] v)>();
            yield return null;   // the GI system rebuilds and re-traces in this frame's LateUpdate
            if (captureFirst) yield return CaptureSet(name + "_first", false, false);
            while (Time.realtimeSinceStartupAsDouble - startTime < settleSeconds)
            {
                int f; gi.GetSampleResults(out f);
                series.Add((Time.frameCount - startFrame, Time.realtimeSinceStartupAsDouble - startTime, SampleNow()));
                yield return null;
            }
            var final = SampleNow();
            yield return CaptureSet(name, true, true);

            // Response metrics per receiver: first frame that moved >5% of the total change, frame reaching within 5% of final.
            var resp = new StringBuilder(); var settle = new StringBuilder();
            for (int r = 0; r < receiverNames.Count; r++)
            {
                float delta = final[r] - before[r];
                float tol = Mathf.Max(Mathf.Abs(delta) * 0.05f, 2e-4f);
                int firstMove = -1, settled = -1; double settledT = -1;
                for (int i = 0; i < series.Count; i++)
                    if (firstMove < 0 && Mathf.Abs(series[i].v[r] - before[r]) > tol) firstMove = series[i].frame;
                for (int i = series.Count - 1; i >= 0; i--)
                {
                    if (Mathf.Abs(series[i].v[r] - final[r]) > tol) break;
                    settled = series[i].frame; settledT = series[i].t;
                }
                resp.Append(receiverNames[r]).Append(':').Append(firstMove).Append(' ');
                settle.Append(receiverNames[r]).Append(':').Append(settled).Append('/').Append(settledT.ToString("F2", CultureInfo.InvariantCulture)).Append("s ");
            }
            foreach (var s in series)
                seriesCsv.Add(name + "," + s.frame + "," + s.t.ToString("F4", CultureInfo.InvariantCulture) + "," + string.Join(",", s.v.Select(x => x.ToString("F5", CultureInfo.InvariantCulture))));
            Record(name, new Dictionary<string, string>
            {
                ["state"] = Q(fixture.state.ToString()), ["lighting"] = Q(fixture.environment.lighting.ToString()),
                ["interiorLamps"] = fixture.environment.interiorLightsEnabled ? "true" : "false", ["shutterClosed"] = fixture.ShutterClosed ? "true" : "false",
                ["roofOpening"] = fixture.RoofOpening.ToString(), ["view"] = Q(fixture.viewNames[fixture.View]),
                ["receiversBefore"] = Vec(before), ["receiversFinal"] = Vec(final),
                ["framesToFirstResponse"] = Q(resp.ToString().Trim()), ["framesSecondsToSettle5pct"] = Q(settle.ToString().Trim()),
                ["rebuildMs"] = F((float)gi.LastRebuildMs), ["framesRecorded"] = series.Count.ToString(),
                ["meanFrameMs"] = F(series.Count > 1 ? (float)(series[series.Count - 1].t * 1000.0 / series[series.Count - 1].frame) : 0),
            });
            Log($"{name}: final {Vec(final)} | first response {resp} | settle {settle}");
        }

        private IEnumerator CaptureSet(string name, bool withOff, bool withIndirect)
        {
            yield return new WaitForEndOfFrame();
            bool was = gi.giEnabled; var view = gi.debugView;
            gi.giEnabled = true; gi.debugView = GIDebugView.Final;
            float lumOn = Capture(name + "_gi", true);
            float lumOff = -1, lumInd = -1;
            if (withOff) { gi.giEnabled = false; lumOff = Capture(name + "_off", false); gi.giEnabled = true; }
            if (withIndirect) { gi.debugView = GIDebugView.IndirectOnly; lumInd = Capture(name + "_indirect", false); }
            gi.giEnabled = was; gi.debugView = view;
            Log($"  capture {name}: mean8bit GI {lumOn:F2} off {lumOff:F2} indirect {lumInd:F2} (frame {Time.frameCount})");
        }

        // Renders the main camera at a fixed resolution. PNG to Captures, JPG copy for the report.
        private float Capture(string name, bool keepPng)
        {
            var cam = fixture.mainCamera;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = prevTarget;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0); tex.Apply();
            RenderTexture.active = prev; rt.Release(); Destroy(rt);
            var px = tex.GetPixels32(); double sum = 0;
            for (int i = 0; i < px.Length; i += 7) sum += 0.2126 * px[i].r + 0.7152 * px[i].g + 0.0722 * px[i].b;
            File.WriteAllBytes(Path.Combine(outputFolder, name + ".png"), tex.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(outputFolder, name + ".jpg"), tex.EncodeToJPG(88));
            Destroy(tex);
            return (float)(sum / (px.Length / 7.0));
        }

        private static float ImageDiff(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) return -1;
            var ta = new Texture2D(2, 2); ta.LoadImage(File.ReadAllBytes(a));
            var tb = new Texture2D(2, 2); tb.LoadImage(File.ReadAllBytes(b));
            var pa = ta.GetPixels32(); var pb = tb.GetPixels32(); double sum = 0;
            for (int i = 0; i < pa.Length && i < pb.Length; i++) sum += (Math.Abs(pa[i].r - pb[i].r) + Math.Abs(pa[i].g - pb[i].g) + Math.Abs(pa[i].b - pb[i].b)) / 3.0;
            Destroy(ta); Destroy(tb);
            return (float)(sum / pa.Length);
        }

        private IEnumerator Performance()
        {
            int prevRate = Application.targetFrameRate, prevVSync = QualitySettings.vSyncCount;
            Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            FrameTimingManager.CaptureFrameTimings();
            fixture.SetView(0);
            // Render the main camera at the fixed report resolution while measuring.
            var perfTarget = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var cam = fixture.mainCamera; var prevCamTarget = cam.targetTexture; cam.targetTexture = perfTarget;
            Log($"Performance: main camera rendered to {width}x{height} RT, targetFrameRate -1, vSync 0 (editor Play mode, Game view also drawn)");
            foreach (int step in new[] { 1, 4, 6 })
            {
                fixture.ApplyState((BreachState)(step - 1));
                yield return Wait(2f);
                foreach (bool on in new[] { true, false, true })
                {
                    gi.giEnabled = on;
                    yield return Wait(1f);
                    var dt = new List<float>(); var gpu = new List<float>(); var cpu = new List<float>();
                    var probe = new List<float>(); var comp = new List<float>(); var albedo = new List<float>(); var giCpu = new List<float>();
                    var timings = new FrameTiming[1];
                    float end = Time.realtimeSinceStartup + 3f;
                    while (Time.realtimeSinceStartup < end)
                    {
                        yield return null;
                        dt.Add(Time.unscaledDeltaTime * 1000f);
                        FrameTimingManager.CaptureFrameTimings();
                        if (FrameTimingManager.GetLatestTimings(1, timings) > 0) { gpu.Add((float)timings[0].gpuFrameTime); cpu.Add((float)timings[0].cpuFrameTime); }
                        if (on) { probe.Add(gi.GpuProbeMs); comp.Add(gi.GpuCompositeMs); albedo.Add(gi.GpuAlbedoMs); giCpu.Add((float)gi.CpuUpdateMs); }
                    }
                    Record($"F_perf_step{step}_gi{(on ? "On" : "Off")}", new Dictionary<string, string>
                    {
                        ["frames"] = dt.Count.ToString(), ["frameMsMean"] = F(Avg(dt)), ["frameMsP95"] = F(P95(dt)),
                        ["ftmGpuFrameMsMean"] = F(Avg(gpu)), ["ftmCpuFrameMsMean"] = F(Avg(cpu)),
                        ["giProbeGpuMs"] = F(Avg(probe)), ["giAlbedoGpuMs"] = F(Avg(albedo)), ["giCompositeGpuMs"] = F(Avg(comp)), ["giCpuMs"] = F(Avg(giCpu)),
                    });
                    Log($"perf step{step} GI {(on ? "ON " : "OFF")}: frame {Avg(dt):F2} ms (p95 {P95(dt):F2}) | FTM gpu {Avg(gpu):F2} cpu {Avg(cpu):F2} | GI gpu probe {Avg(probe):F2} albedo {Avg(albedo):F2} composite {Avg(comp):F2} | GI cpu {Avg(giCpu):F2}");
                }
            }
            gi.giEnabled = true;
            cam.targetTexture = prevCamTarget; perfTarget.Release(); Destroy(perfTarget);
            // Change cost: rebuild time for each state switch (TLAS rebuild + new BLAS).
            var rebuild = new StringBuilder();
            for (int step = 1; step <= 7; step++) { fixture.ApplyState((BreachState)(step - 1)); yield return null; rebuild.Append($"step{step}:{gi.LastRebuildMs:F2}ms "); }
            fixture.SetShutter(true); yield return null; rebuild.Append($"blockerOn:{gi.LastRebuildMs:F2}ms "); fixture.SetShutter(false); yield return null;
            Log("Scene rebuild cost on change: " + rebuild);
            Log($"Scene: instances {gi.StatInstances}, scene triangles {gi.StatSceneTriangles}, unique BLAS {gi.StatBlas} ({gi.StatTriangles} tris), BVH nodes {gi.StatBvhNodes}, probe GPU memory {gi.GpuMemoryBytes / (1024f * 1024f):F1} MiB");
            Application.targetFrameRate = prevRate; QualitySettings.vSyncCount = prevVSync;
        }

        // ------------------------------------------------------------------------------------------
        private void Record(string name, Dictionary<string, string> fields)
        {
            var sb = new StringBuilder("  {\"name\": ").Append(Q(name)).Append(", \"frame\": ").Append(Time.frameCount)
                .Append(", \"resolution\": ").Append(Q(width + "x" + height));
            foreach (var kv in fields) sb.Append(", ").Append(Q(kv.Key)).Append(": ").Append(kv.Value);
            jsonRecords.Add(sb.Append('}').ToString());
        }

        private void Log(string line) { log.AppendLine(line); Debug.Log("[DynamicGI Eval] " + line); }

        private void WriteOutputs()
        {
            File.WriteAllText(Path.Combine(outputFolder, "results.json"), "{\n \"receivers\": [" + string.Join(", ", receiverNames.Select(Q)) + "],\n \"records\": [\n" + string.Join(",\n", jsonRecords) + "\n ]\n}\n");
            File.WriteAllText(Path.Combine(outputFolder, "log.txt"), log.ToString());
            File.WriteAllText(Path.Combine(outputFolder, "series.csv"), "record,frame,seconds," + string.Join(",", receiverNames) + "\n" + string.Join("\n", seriesCsv) + "\n");
        }

        private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);
        private static string Vec(float[] v) => "[" + string.Join(", ", v.Select(x => x.ToString("F5", CultureInfo.InvariantCulture))) + "]";
        private static float Avg(List<float> v) => v.Count == 0 ? 0 : v.Average();
        private static float P95(List<float> v) { if (v.Count == 0) return 0; var s = v.OrderBy(x => x).ToList(); return s[Mathf.Min(s.Count - 1, (int)(s.Count * 0.95f))]; }
        private static float MaxAbsDiff(float[] a, float[] b) { float m = 0; for (int i = 0; i < a.Length; i++) m = Mathf.Max(m, Mathf.Abs(a[i] - b[i])); return m; }
        private static float MaxRelDiff(float[] a, float[] b) { float m = 0; for (int i = 0; i < a.Length; i++) m = Mathf.Max(m, Mathf.Abs(a[i] - b[i]) / Mathf.Max(1e-3f, Mathf.Abs(a[i]))); return m; }
    }
}
