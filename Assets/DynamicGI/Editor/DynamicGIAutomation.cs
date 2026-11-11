using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GIEvaluation;
using GIEvaluation.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI.Editor
{
    /// <summary>
    /// Development / verification harness. It can be started from the menu or
    /// from Unity batch mode with -executeMethod. It enters play mode, drives the
    /// seven existing fixture states, captures fixed 1920x1080 frames and writes
    /// a JSON report. It is deliberately outside the runtime assembly.
    /// </summary>
    public static class DynamicGIAutomation
    {
        private const int CaptureWidth = 1920;
        private const int CaptureHeight = 1080;
        private static string s_OutputDir;
        private static bool s_Batch;
        private static bool s_Full;
        private static bool s_Running;
        private static readonly Stack<IEnumerator> s_Stack = new Stack<IEnumerator>();
        private static readonly List<CaptureRecord> s_Records = new List<CaptureRecord>();
        private static readonly List<string> s_Log = new List<string>();
        private static Summary s_Summary = new Summary();

        [MenuItem("GI Evaluation/Dynamic GI/Quick Capture (Play)")]
        public static void MenuQuick() => Begin(false, false);

        [MenuItem("GI Evaluation/Dynamic GI/Run Full Verification (Play)")]
        public static void MenuFull() => Begin(false, true);

        public static void BatchSmoke() => Begin(true, false);
        public static void BatchEntry() => Begin(true, true);

        private static bool s_Dev;
        private static bool s_Leak;
        private static bool s_Quick;

        public static void BatchDev() { s_Dev = true; Begin(true, false); }
        public static void BatchLeak() { s_Leak = true; Begin(true, false); }
        public static void BatchQuick() { s_Quick = true; Begin(true, false); }

        private static void Begin(bool batch, bool full)
        {
            if (s_Running)
            {
                Debug.LogWarning("[DynamicGI] Verification already running.");
                return;
            }
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[DynamicGI] Stop play mode before starting the harness.");
                if (batch) EditorApplication.Exit(1);
                return;
            }
            s_Batch = batch;
            s_Full = full;
            s_Running = true;
            s_Records.Clear();
            s_Log.Clear();
            s_Summary = new Summary();
            s_OutputDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports/DynamicGI_Final"));
            Directory.CreateDirectory(s_OutputDir);

            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != GITestSceneTools.MainScenePath)
                EditorSceneManager.OpenScene(GITestSceneTools.MainScenePath);

            s_Stack.Clear();
            s_Stack.Push(Main());
            EditorApplication.update += Tick;
            EditorApplication.isPlaying = true;
            Debug.Log("[DynamicGI] Verification starting, full=" + full + ", output=" + s_OutputDir);
        }

        private static void Tick()
        {
            if (!s_Running) return;
            try
            {
                if (s_Stack.Count == 0) return;
                var top = s_Stack.Peek();
                if (!top.MoveNext())
                {
                    s_Stack.Pop();
                    if (s_Stack.Count == 0) Complete();
                    return;
                }
                if (top.Current is IEnumerator nested) s_Stack.Push(nested);
            }
            catch (Exception e)
            {
                Debug.LogError("[DynamicGI] Verification exception: " + e);
                WriteError(e.ToString());
                Finish(1);
            }
        }

        private static IEnumerator Main()
        {
            while (!EditorApplication.isPlaying) yield return null;
            yield return WaitFrames(5);

            var scenario = UnityEngine.Object.FindAnyObjectByType<GIBreachScenario>();
            if (!scenario)
            {
                Debug.LogError("[DynamicGI] GIBreachScenario not found.");
                Finish(1);
                yield break;
            }
            var controller = UnityEngine.Object.FindAnyObjectByType<DynamicGIController>();
            if (!controller)
            {
                controller = scenario.gameObject.AddComponent<DynamicGIController>();
                yield return WaitFrames(2);
            }
            var env = scenario.environment;
            scenario.StopTimeline();
            scenario.SetPreview(false);
            scenario.SetView(0);
            env.SetInteriorLights(false);
            env.Apply(BreachLighting.SunAndSky);
            controller.enableGI = true;
            controller.viewMode = GIViewMode.Composite;
            controller.bounceIterations = 5;
            controller.bouncesPerFrame = 2;

            yield return WaitFrames(5);
            Debug.Log($"[DynamicGI] ready={controller.IsReady} dims={controller.VolumeDimX}x{controller.VolumeDimY}x{controller.VolumeDimZ} voxel={controller.ActualVoxelSize.x:F3} tris={controller.TriangleCount} mem={controller.VolumeMemoryBytes / 1048576.0:F1}MB cmds={controller.LastRebuildCpuMs:F1}ms maskRead={controller.LastMaskReadbackMs:F1}ms maskBfs={controller.LastMaskBfsMs:F1}ms");
            yield return WaitConverged(controller, 600);
            Debug.Log($"[DynamicGI] converged={controller.IsConverged} bounces={controller.BouncePass} gpu={controller.LastConvergeGpuMs:F1}ms");

            SetPerformanceSettings();
            yield return WaitFrames(2);

            if (s_Dev)
            {
                yield return DevSuite(scenario, env, controller);
                yield return WriteSummary(scenario, env, controller);
                Finish(0);
                yield break;
            }
            if (s_Leak)
            {
                yield return LeakSuite(scenario, env, controller);
                yield return WriteSummary(scenario, env, controller);
                Finish(0);
                yield break;
            }
            if (s_Quick)
            {
                scenario.ApplyState(BreachState.Sealed);
                yield return WaitConverged(controller, 600);
                s_Records.Add(CaptureNow(scenario, env, controller, "quick_sealed_bothgates", "sealed, both gates"));
                controller.screenGateEnabled = false;
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, "quick_sealed_sourcegate", "sealed, source gate only"));
                controller.screenGateEnabled = true;

                scenario.ApplyState(BreachState.LargeBreach);
                yield return WaitConverged(controller, 600);
                s_Records.Add(CaptureNow(scenario, env, controller, "quick_step3_on", "quick step3 GI on"));
                controller.viewMode = (GIViewMode)4;
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, "quick_step3_mask", "quick step3 outside mask"));
                controller.viewMode = GIViewMode.Composite;
                controller.screenGateEnabled = false;
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, "quick_step3_noscreengate", "quick step3 without screen gate"));
                controller.screenGateEnabled = true;
                controller.SetGIEnabled(false);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, "quick_step3_off", "quick step3 GI off"));
                controller.SetGIEnabled(true);
                yield return WaitConverged(controller, 600);
                yield return WriteSummary(scenario, env, controller);
                Finish(0);
                yield break;
            }

            yield return Capture(scenario, env, controller, "smoke_state1_on", "Smoke: sealed, GI on");
            controller.SetGIEnabled(false);
            yield return WaitFrames(2);
            yield return Capture(scenario, env, controller, "smoke_state1_off", "Smoke: sealed, GI off");
            controller.SetGIEnabled(true);
            yield return WaitFrames(1);
            yield return WaitConverged(controller, 600);
            controller.viewMode = GIViewMode.BouncesOnly;
            yield return WaitFrames(2);
            yield return Capture(scenario, env, controller, "smoke_state1_bounces", "Smoke: sealed, bounces only");
            controller.viewMode = GIViewMode.Composite;

            if (s_Full)
            {
                yield return FullSuite(scenario, env, controller);
            }

            yield return WriteSummary(scenario, env, controller);
            Finish(0);
        }

        private static IEnumerator LeakSuite(GIBreachScenario scenario, GIBreachEnvironment env, DynamicGIController controller)
        {
            env.SetInteriorLights(false);
            controller.SetGIEnabled(true);
            controller.viewMode = GIViewMode.Composite;
            foreach (BreachState state in new[] { BreachState.Sealed, BreachState.Restored })
            {
                scenario.ApplyState(state);
                foreach (BreachLighting mode in new[] { BreachLighting.SunOnly, BreachLighting.SkyOnly, BreachLighting.SunAndSky })
                {
                    env.Apply(mode);
                    yield return WaitConverged(controller, 600);
                    s_Records.Add(CaptureNow(scenario, env, controller, $"leak_{state}_{mode}_on", "sealed leak " + mode));
                    controller.viewMode = GIViewMode.BouncesOnly;
                    yield return WaitFrames(2);
                    s_Records.Add(CaptureNow(scenario, env, controller, $"leak_{state}_{mode}_bounces", "sealed leak bounces " + mode));
                    controller.viewMode = GIViewMode.Composite;
                }
            }
            env.Apply(BreachLighting.SunAndSky);
        }

        private static IEnumerator DevSuite(GIBreachScenario scenario, GIBreachEnvironment env, DynamicGIController controller)
        {
            env.SetInteriorLights(false);
            env.Apply(BreachLighting.SunAndSky);
            controller.SetGIEnabled(true);
            controller.viewMode = GIViewMode.Composite;

            for (int step = 1; step <= 7; step++)
            {
                scenario.ApplyState((BreachState)(step - 1));
                yield return WaitConverged(controller, 600);
                s_Records.Add(CaptureNow(scenario, env, controller, $"dev_step{step}_on", "dev GI on"));
                controller.viewMode = GIViewMode.AllGI;
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"dev_step{step}_allgi", "dev all GI"));
                controller.viewMode = GIViewMode.BouncesOnly;
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"dev_step{step}_bounces", "dev bounces only"));
                controller.viewMode = GIViewMode.Composite;
                controller.SetGIEnabled(false);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"dev_step{step}_off", "dev GI off"));
                controller.SetGIEnabled(true);
                yield return WaitConverged(controller, 600);
            }

            // Isolate the outdoor sources in the sealed state to identify leaks.
            scenario.ApplyState(BreachState.Sealed);
            env.SetInteriorLights(false);
            env.Apply(BreachLighting.SunOnly);
            yield return WaitConverged(controller, 600);
            s_Records.Add(CaptureNow(scenario, env, controller, "dev_sealed_sun_on", "sealed, sun only"));
            env.Apply(BreachLighting.SkyOnly);
            yield return WaitConverged(controller, 600);
            s_Records.Add(CaptureNow(scenario, env, controller, "dev_sealed_sky_on", "sealed, sky only"));
            env.Apply(BreachLighting.SunAndSky);
            yield return WaitConverged(controller, 600);

            scenario.ApplyState(BreachState.LargeBreach);
            yield return WaitConverged(controller, 600);
            for (int v = 0; v < scenario.viewPositions.Length; v++)
            {
                scenario.SetView(v);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"dev_view{v}_on", "dev view " + scenario.viewNames[v]));
            }
            scenario.SetView(0);
        }

        private static IEnumerator FullSuite(GIBreachScenario scenario, GIBreachEnvironment env, DynamicGIController controller)
        {
            env.SetInteriorLights(false);
            env.Apply(BreachLighting.SunAndSky);
            controller.viewMode = GIViewMode.Composite;
            controller.SetGIEnabled(true);

            // --- seven fixture states, fixed indoor camera ---
            for (int step = 1; step <= 7; step++)
            {
                scenario.ApplyState((BreachState)(step - 1));
                int changeFrame = Time.frameCount;
                double changeTime = Time.realtimeSinceStartupAsDouble;
                yield return WaitFrames(1);
                CaptureRecord first = CaptureNow(scenario, env, controller, $"step{step}_first", "first frame after change");
                first.changeFrame = changeFrame;
                first.secondsSinceChange = Time.realtimeSinceStartupAsDouble - changeTime;
                first.bouncePass = controller.BouncePass;
                first.rebuildCpuMs = controller.LastRebuildCpuMs;
                s_Records.Add(first);

                yield return WaitConverged(controller, 600);
                double convergedAt = Time.realtimeSinceStartupAsDouble;
                CaptureRecord on = CaptureNow(scenario, env, controller, $"step{step}_on", "GI on, converged");
                on.changeFrame = changeFrame;
                on.framesToConverge = Time.frameCount - changeFrame;
                on.secondsToConverge = convergedAt - changeTime;
                on.gpuConvergeMs = controller.LastConvergeGpuMs;
                on.rebuildFrame = controller.LastRebuildFrame;
                on.rebuildCpuMs = controller.LastRebuildCpuMs;
                on.maskReadMs = controller.LastMaskReadbackMs;
                on.maskBfsMs = controller.LastMaskBfsMs;
                s_Records.Add(on);
                s_Log.Add($"step{step}: frames to converge={on.framesToConverge}, seconds={on.secondsToConverge:F3}, rebuildCPU={on.rebuildCpuMs:F2}ms, gpuWait={on.gpuConvergeMs:F2}ms");

                controller.viewMode = GIViewMode.BouncesOnly;
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"step{step}_bounces", "indirect bounces only"));
                if (step == 3 || step == 6)
                {
                    controller.viewMode = GIViewMode.FalseColor;
                    yield return WaitFrames(2);
                    s_Records.Add(CaptureNow(scenario, env, controller, $"step{step}_falsecolor", "indirect false colour"));
                }
                controller.viewMode = GIViewMode.Composite;

                controller.SetGIEnabled(false);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"step{step}_off", "GI off"));
                controller.SetGIEnabled(true);
                yield return WaitConverged(controller, 600);
            }

            // --- sealed-state source isolation (leak check) ---
            scenario.ApplyState(BreachState.Sealed);
            env.SetInteriorLights(false);
            foreach (BreachLighting mode in new[] { BreachLighting.SunOnly, BreachLighting.SkyOnly })
            {
                env.Apply(mode);
                yield return WaitConverged(controller, 600);
                s_Records.Add(CaptureNow(scenario, env, controller, $"sealed_{mode}_on", "sealed " + mode));
                controller.SetGIEnabled(false);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"sealed_{mode}_off", "sealed " + mode + ", GI off"));
                controller.SetGIEnabled(true);
                yield return WaitConverged(controller, 600);
            }
            env.Apply(BreachLighting.SunAndSky);

            // --- outdoor blocker B (steps 2..4, roof intact) ---
            for (int step = 2; step <= 4; step++)
            {
                scenario.ApplyState((BreachState)(step - 1));
                yield return WaitFrames(2);
                scenario.SetShutter(false, true);
                yield return WaitConverged(controller, 600);
                s_Records.Add(CaptureNow(scenario, env, controller, $"step{step}_shutter_open_on", "outside blocker open"));
                scenario.SetShutter(true, true);
                yield return WaitConverged(controller, 600);
                s_Records.Add(CaptureNow(scenario, env, controller, $"step{step}_shutter_closed_on", "outside blocker closed"));
                controller.SetGIEnabled(false);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"step{step}_shutter_closed_off", "outside blocker closed, GI off"));
                controller.SetGIEnabled(true);
                scenario.SetShutter(false, true);
                yield return WaitConverged(controller, 600);
            }

            // --- E: outdoor lighting modes at step 3 ---
            scenario.ApplyState(BreachState.LargeBreach);
            foreach (BreachLighting mode in new[] { BreachLighting.SunOnly, BreachLighting.SkyOnly, BreachLighting.SunAndSky })
            {
                env.Apply(mode);
                yield return WaitConverged(controller, 600);
                s_Records.Add(CaptureNow(scenario, env, controller, $"light_{mode}_on", "outdoor " + mode));
                controller.SetGIEnabled(false);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"light_{mode}_off", "outdoor " + mode + ", GI off"));
                controller.SetGIEnabled(true);
                yield return WaitConverged(controller, 600);
            }
            env.Apply(BreachLighting.SunAndSky);

            // --- I: interior lamps on / off in the sealed room ---
            scenario.ApplyState(BreachState.Sealed);
            env.Apply(BreachLighting.SunAndSky);
            env.SetInteriorLights(true);
            yield return WaitConverged(controller, 600);
            s_Records.Add(CaptureNow(scenario, env, controller, "lamps_on_gi_on", "interior lamps on"));
            env.SetInteriorLights(false);
            yield return WaitConverged(controller, 600);
            s_Records.Add(CaptureNow(scenario, env, controller, "lamps_off_gi_on", "interior lamps off"));

            // --- V: all six camera views at step 3, then step 4 ---
            scenario.ApplyState(BreachState.ConnectedRooms);
            yield return WaitConverged(controller, 600);
            for (int v = 0; v < scenario.viewPositions.Length; v++)
            {
                scenario.SetView(v);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"view_{v}_on", scenario.viewNames[v]));
                if (v == 1 || v == 4)
                {
                    controller.viewMode = GIViewMode.BouncesOnly;
                    yield return WaitFrames(2);
                    s_Records.Add(CaptureNow(scenario, env, controller, $"view_{v}_bounces", "bounces only from this view"));
                    controller.viewMode = GIViewMode.Composite;
                }
                controller.SetGIEnabled(false);
                yield return WaitFrames(2);
                s_Records.Add(CaptureNow(scenario, env, controller, $"view_{v}_off", scenario.viewNames[v] + ", GI off"));
                controller.SetGIEnabled(true);
                yield return WaitConverged(controller, 600);
            }
            scenario.SetView(0);
            yield return WaitConverged(controller, 600);

            // --- performance at step 1 (sealed) ---
            scenario.ApplyState(BreachState.Sealed);
            yield return WaitConverged(controller, 600);
            yield return MeasurePerformance(scenario, env, controller, "sealed");

            scenario.ApplyState(BreachState.LargeRoofBreach);
            yield return WaitConverged(controller, 600);
            yield return MeasurePerformance(scenario, env, controller, "large_roof_breach");

            scenario.ApplyState(BreachState.Restored);
            yield return WaitConverged(controller, 600);
            s_Records.Add(CaptureNow(scenario, env, controller, "restored_on", "restored, GI on"));
            controller.SetGIEnabled(false);
            yield return WaitFrames(2);
            s_Records.Add(CaptureNow(scenario, env, controller, "restored_off", "restored, GI off"));
            controller.SetGIEnabled(true);
            yield return WaitConverged(controller, 600);
        }

        private static IEnumerator MeasurePerformance(GIBreachScenario scenario, GIBreachEnvironment env, DynamicGIController controller, string tag)
        {
            SetPerformanceSettings();
            yield return WaitFrames(10);

            // A synchronous 1920x1080 offscreen render + 1 pixel GPU readback.
            // The editor play-mode frame counter in batch mode reports ~0.06 ms,
            // which is not a real frame time, so it is not used here. This
            // measures CPU submission + GPU completion per rendered frame and is
            // a conservative (readback-inclusive) comparison of GI on / off.
            var on = new List<float>(40);
            var off = new List<float>(40);
            MeasureRenderSamplesPaired(scenario.mainCamera, controller, 40, 5, on, off);
            yield return WaitConverged(controller, 600);

            on.Sort(); off.Sort();
            var measurement = new PerformanceMeasurement
            {
                tag = tag,
                giOnMsAvg = Average(on), giOffMsAvg = Average(off),
                giOnMsMedian = Percentile(on, 0.5f), giOffMsMedian = Percentile(off, 0.5f),
                giOnMsP95 = Percentile(on, 0.95f), giOffMsP95 = Percentile(off, 0.95f),
                frames = on.Count,
                targetFrameRate = Application.targetFrameRate,
                vSync = QualitySettings.vSyncCount,
                note = "Synchronous 1920x1080 RenderTexture render + 1-pixel AsyncGPUReadback, editor batch mode. Includes CPU submission and GPU completion; the GI delta is the composite pass. Not a vsync-limited FPS measurement.",
            };
            s_Summary.performance.Add(measurement);
            s_Log.Add($"{tag}: sync render ms GI on avg={measurement.giOnMsAvg:F2} median={measurement.giOnMsMedian:F2} p95={measurement.giOnMsP95:F2}; GI off avg={measurement.giOffMsAvg:F2} median={measurement.giOffMsMedian:F2} p95={measurement.giOffMsP95:F2}");
        }

        private static void MeasureRenderSamplesPaired(Camera camera, DynamicGIController controller, int samples, int warmup,
            List<float> on, List<float> off)
        {
            var rt = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            bool original = controller.enableGI;
            for (int i = 0; i < warmup; i++)
            {
                controller.enableGI = true;
                RenderOnce(camera, rt);
            }
            for (int i = 0; i < samples; i++)
            {
                bool giOn = (i & 1) == 0;
                controller.enableGI = giOn;
                float ms = RenderOnce(camera, rt);
                (giOn ? on : off).Add(ms);
            }
            controller.enableGI = original;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
        }

        private static float RenderOnce(Camera camera, RenderTexture rt)
        {
            var request = new RenderPipeline.StandardRequest { destination = rt };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RenderPipeline.SubmitRenderRequest(camera, request);
            var readback = AsyncGPUReadback.Request(rt, 0, 0, 1, 0, 1, 0, 1);
            readback.WaitForCompletion();
            sw.Stop();
            return (float)sw.Elapsed.TotalMilliseconds;
        }

        private static List<float> MeasureRenderSamples(Camera camera, int samples, int warmup)
        {
            var result = new List<float>(samples);
            var rt = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            for (int i = 0; i < warmup + samples; i++)
            {
                var request = new RenderPipeline.StandardRequest { destination = rt };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RenderPipeline.SubmitRenderRequest(camera, request);
                var readback = AsyncGPUReadback.Request(rt, 0, 0, 1, 0, 1, 0, 1);
                readback.WaitForCompletion();
                sw.Stop();
                if (i >= warmup) result.Add((float)sw.Elapsed.TotalMilliseconds);
            }
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            return result;
        }

        private static float Average(List<float> values)
        {
            double sum = 0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            return (float)(sum / Math.Max(1, values.Count));
        }

        private static float Percentile(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return 0;
            int index = Mathf.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1);
            return sorted[index];
        }

        private static void SetPerformanceSettings()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
        }

        private static IEnumerator WaitFrames(int count)
        {
            int end = Time.frameCount + Mathf.Max(1, count);
            while (Time.frameCount < end) yield return null;
        }

        private static IEnumerator WaitConverged(DynamicGIController controller, int maxFrames)
        {
            int start = Time.frameCount;
            yield return WaitFrames(2);
            while (controller && (!controller.IsReady || !controller.IsConverged))
            {
                if (Time.frameCount - start > maxFrames)
                {
                    Debug.LogWarning("[DynamicGI] convergence timeout. bounces=" + controller.BouncePass);
                    break;
                }
                yield return null;
            }
            yield return WaitFrames(3);
        }

        private static IEnumerator Capture(GIBreachScenario scenario, GIBreachEnvironment env, DynamicGIController controller, string name, string note)
        {
            s_Records.Add(CaptureNow(scenario, env, controller, name, note));
            yield return null;
        }

        private static CaptureRecord CaptureNow(GIBreachScenario scenario, GIBreachEnvironment env, DynamicGIController controller, string name, string note)
        {
            var cam = scenario.mainCamera;
            var rt = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            var request = new RenderPipeline.StandardRequest { destination = rt };
            RenderPipeline.SubmitRenderRequest(cam, request);
            var tex = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGBA32, false, false);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = previous;

            string file = name + ".png";
            File.WriteAllBytes(Path.Combine(s_OutputDir, file), tex.EncodeToPNG());

            var record = new CaptureRecord
            {
                file = file,
                note = note,
                state = scenario.state.ToString(),
                lighting = env.lighting.ToString(),
                interiorLamps = env.interiorLightsEnabled,
                previewFill = scenario.preview,
                giEnabled = controller.enableGI,
                viewMode = controller.viewMode.ToString(),
                viewIndex = scenario.View,
                viewName = scenario.viewNames[scenario.View],
                roofOpening = scenario.RoofOpening,
                interiorOpen = scenario.InteriorOpen,
                shutterClosed = scenario.ShutterClosed,
                width = CaptureWidth,
                height = CaptureHeight,
                geometryRevision = GeometryChanges.Revision,
                frame = Time.frameCount,
                framesSinceChange = Time.frameCount - scenario.LastChangeFrame,
                secondsSinceChange = Time.realtimeSinceStartupAsDouble - scenario.LastChangeTime,
                bouncePass = controller.BouncePass,
                maskReadMs = controller.LastMaskReadbackMs,
                maskBfsMs = controller.LastMaskBfsMs,
                meanLuma = MeanLuma(tex),
                receiverLuma = ReceiverLuma(scenario, cam, tex),
                receiverNames = ReceiverNames(scenario),
                cameraPosition = cam.transform.position,
                cameraEuler = cam.transform.eulerAngles,
                fieldOfView = cam.fieldOfView,
            };
            UnityEngine.Object.Destroy(tex);
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            return record;
        }

        private static string[] ReceiverNames(GIBreachScenario scenario)
        {
            var names = new string[scenario.receivers.Length];
            for (int i = 0; i < names.Length; i++) names[i] = scenario.receivers[i].name;
            return names;
        }

        private static float[] ReceiverLuma(GIBreachScenario scenario, Camera cam, Texture2D tex)
        {
            var result = new float[scenario.receivers.Length];
            for (int i = 0; i < scenario.receivers.Length; i++)
            {
                var sample = scenario.receivers[i];
                if (!sample.point) { result[i] = -1; continue; }
                int cx, cy;
                if (!WorldToCapturePixel(cam, sample.point.position, out cx, out cy)) { result[i] = -1; continue; }
                float sum = 0; int count = 0;
                for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= CaptureWidth || y < 0 || y >= CaptureHeight) continue;
                    Color c = tex.GetPixel(x, y);
                    sum += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                    count++;
                }
                result[i] = count > 0 ? sum / count : -1;
            }
            return result;
        }

        private static bool WorldToCapturePixel(Camera cam, Vector3 world, out int px, out int py)
        {
            px = py = 0;
            Vector3 view = cam.worldToCameraMatrix.MultiplyPoint(world);
            if (view.z > -cam.nearClipPlane) return false;
            Matrix4x4 proj = Matrix4x4.Perspective(cam.fieldOfView, CaptureWidth / (float)CaptureHeight,
                cam.nearClipPlane, cam.farClipPlane);
            Vector4 clip = proj * new Vector4(view.x, view.y, view.z, 1f);
            if (clip.w <= 1e-5f) return false;
            float ndcX = clip.x / clip.w;
            float ndcY = clip.y / clip.w;
            if (ndcX < -1f || ndcX > 1f || ndcY < -1f || ndcY > 1f) return false;
            px = Mathf.Clamp((int)((ndcX * 0.5f + 0.5f) * CaptureWidth), 0, CaptureWidth - 1);
            py = Mathf.Clamp((int)((ndcY * 0.5f + 0.5f) * CaptureHeight), 0, CaptureHeight - 1);
            return true;
        }

        private static float MeanLuma(Texture2D tex)
        {
            var pixels = tex.GetPixels();
            double sum = 0;
            for (int i = 0; i < pixels.Length; i++)
                sum += 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
            return (float)(sum / Math.Max(1, pixels.Length));
        }

        private static IEnumerator WriteSummary(GIBreachScenario scenario, GIBreachEnvironment env, DynamicGIController controller)
        {
            s_Summary.captures = s_Records;
            s_Summary.log = s_Log;
            s_Summary.hardware = new HardwareInfo
            {
                device = SystemInfo.graphicsDeviceName,
                deviceType = SystemInfo.graphicsDeviceType.ToString(),
                deviceVersion = SystemInfo.graphicsDeviceVersion,
                unity = Application.unityVersion,
                os = SystemInfo.operatingSystem,
                compute = SystemInfo.supportsComputeShaders,
                screen = Screen.width + "x" + Screen.height,
                capture = CaptureWidth + "x" + CaptureHeight,
                targetFrameRate = Application.targetFrameRate,
                vSync = QualitySettings.vSyncCount,
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                hdr = scenario.mainCamera.allowHDR,
            };
            s_Summary.volume = new VolumeInfo
            {
                dims = controller.VolumeDimX + "x" + controller.VolumeDimY + "x" + controller.VolumeDimZ,
                voxelSize = controller.ActualVoxelSize,
                volumeCenter = controller.volumeCenter,
                volumeSize = controller.volumeSize,
                textureMemoryBytes = controller.VolumeMemoryBytes,
                triangleBufferBytes = controller.TriangleBufferBytes,
                triangleCount = controller.TriangleCount,
                bounceIterations = controller.bounceIterations,
                bouncesPerFrame = controller.bouncesPerFrame,
                propagationDirections = controller.propagationDirections,
                skyDirections = controller.skyDirections,
                maxTraceDistance = controller.maxTraceDistance,
            };
            string json = JsonUtility.ToJson(s_Summary, true);
            File.WriteAllText(Path.Combine(s_OutputDir, "summary.json"), json);
            File.WriteAllText(Path.Combine(s_OutputDir, "conditions.txt"),
                "Dynamic GI verification\n" +
                "engine=" + Application.unityVersion + ", graphics=" + SystemInfo.graphicsDeviceType + ", device=" + SystemInfo.graphicsDeviceName + "\n" +
                "resolution=" + CaptureWidth + "x" + CaptureHeight + " (Camera RenderTexture, UI excluded)\n" +
                "GI volume=" + s_Summary.volume.dims + ", voxel=" + s_Summary.volume.voxelSize + ", bounces=" + s_Summary.volume.bounceIterations + ", propagation dirs=" + s_Summary.volume.propagationDirections + ", sky dirs=" + s_Summary.volume.skyDirections + ", max trace=" + s_Summary.volume.maxTraceDistance + "\n" +
                "camera=view 0 unless noted\npreview fill=OFF\nlamps=OFF unless noted\nlighting=SunAndSky unless noted\n" +
                "fixture frame limit: 30 fps in Awake; the harness overrides it to targetFrameRate=" + Application.targetFrameRate + ", vSync=" + QualitySettings.vSyncCount + " for measurement.\n" +
                "Performance: synchronous 1920x1080 render + 1-pixel AsyncGPUReadback; the batch editor play-mode frame counter is not a real frame time.\n");
            Debug.Log("[DynamicGI] summary written: " + Path.Combine(s_OutputDir, "summary.json"));
            yield return null;
        }

        private static void WriteError(string text)
        {
            try { File.WriteAllText(Path.Combine(s_OutputDir, "error.txt"), text); } catch { }
        }

        private static void Complete()
        {
            Finish(0);
        }

        private static void Finish(int exitCode)
        {
            if (!s_Running) return;
            s_Running = false;
            EditorApplication.update -= Tick;
            Debug.Log("[DynamicGI] Verification finished, exit=" + exitCode);
            if (s_Batch)
            {
                EditorApplication.Exit(exitCode);
            }
            else
            {
                EditorApplication.isPlaying = false;
            }
        }

        [Serializable] public class CaptureRecord
        {
            public string file, note, state, lighting, viewMode, viewName;
            public bool interiorLamps, previewFill, giEnabled, interiorOpen, shutterClosed;
            public int viewIndex, roofOpening, width, height, geometryRevision, frame, framesSinceChange;
            public double secondsSinceChange;
            public int bouncePass, changeFrame = -1, framesToConverge = -1, rebuildFrame = -1;
            public double secondsToConverge = -1, gpuConvergeMs = -1, rebuildCpuMs = -1, maskReadMs = -1, maskBfsMs = -1;
            public float meanLuma;
            public float[] receiverLuma;
            public string[] receiverNames;
            public Vector3 cameraPosition, cameraEuler;
            public float fieldOfView;
        }

        [Serializable] public class PerformanceMeasurement
        {
            public string tag, note;
            public float giOnMsAvg, giOffMsAvg, giOnMsMedian, giOffMsMedian, giOnMsP95, giOffMsP95;
            public int frames, targetFrameRate, vSync;
        }

        [Serializable] public class HardwareInfo
        {
            public string device, deviceType, deviceVersion, unity, os, screen, capture, colorSpace;
            public bool compute, hdr;
            public int targetFrameRate, vSync;
        }

        [Serializable] public class VolumeInfo
        {
            public string dims;
            public Vector3 voxelSize, volumeCenter, volumeSize;
            public long textureMemoryBytes, triangleBufferBytes;
            public int triangleCount, bounceIterations, bouncesPerFrame, propagationDirections, skyDirections;
            public float maxTraceDistance;
        }

        [Serializable] public class Summary
        {
            public HardwareInfo hardware;
            public VolumeInfo volume;
            public List<PerformanceMeasurement> performance = new List<PerformanceMeasurement>();
            public List<CaptureRecord> captures = new List<CaptureRecord>();
            public List<string> log = new List<string>();
        }
    }
}
