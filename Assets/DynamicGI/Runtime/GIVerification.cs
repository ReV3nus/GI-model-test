using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GIEvaluation;
using UnityEngine;
using UnityEngine.Profiling;

namespace DynamicGI
{
    // This is an evidence recorder, not a GI correctness oracle. It uses the original
    // fixture operations, camera presets and lights without changing their meaning.
    public sealed class GIVerification : MonoBehaviour
    {
        public static bool Running { get; private set; }
        public static bool Finished { get; private set; }
        public static string ReportDirectory { get; private set; }
        public int WaitFrames = 64;
        public int OffFrames = 32;
        public bool ExtendedTests = true;
        public string CurrentOperation { get; private set; }

        GIBreachScenario fixture;
        DynamicGIController gi;
        RenderTexture target, oldTarget;
        BreachState oldState;
        BreachLighting oldLighting;
        int oldView, oldRoof, oldLimit, oldVsync;
        bool oldPreview, oldLamps, oldShutter, oldGI, oldDebug;
        bool restorePending;
        float oldAspect;
        Vector3 oldPosition;
        Quaternion oldRotation;
        Evidence report;
        readonly FrameTiming[] timing = new FrameTiming[1];
        readonly List<float> frameMs = new List<float>();
        readonly List<float> gpuMs = new List<float>();
        readonly List<double> dispatchMs = new List<double>();
        double peakBuildMs;
        double operationTime;
        int operationFrame;

        [Serializable] public sealed class Evidence
        {
            public string startedUtc, finishedUtc, unity, graphicsDevice, graphicsAPI, cpu, operatingSystem;
            public string status = "running";
            public string interpretation = "Recorded observations only. Numeric image statistics are not a substitute for visual GI correctness assessment. GPU timings are whole-frame values only when FrameTimingManager returns nonzero timings; CPU dispatch is submission time, not GPU execution.";
            public int systemMemoryMB, graphicsMemoryMB, width = 1920, height = 1080;
            public int giWidth, giHeight, samplesPerPixel, bounceCount, waitFrames, offFrames, vSync, targetFrameRate;
            public long managedHeapBytesAtStart, totalAllocatedBytesAtStart;
            public List<Observation> observations = new List<Observation>();
            public string knownMeasurementLimitations = "Editor Play mode; uncapped, no vSync. PNG/readback/file-writing frames are excluded from steady frame timing windows. Frame times include all Editor/rendering activity and are not isolated GI GPU cost. No physical ground truth comparison or automatic visual pass claim. No formal asymptotic convergence bound; stable means the stated fixed sample budget.";
        }

        [Serializable] public sealed class Observation
        {
            public string label, image, indirectImage, state, lighting, view, invalidationReason, indirectBufferMeaning, timingWindowMeaning;
            public bool giEnabled, indirectOnly, previewFill, interiorLightsEnabled, shutterClosed;
            public int width, height, giWidth, giHeight, preset, roofOpening, geometryRevision, lightingRevision;
            public int frame, framesSinceFixtureChange, framesSinceOperation, framesSinceInvalidation, sampleIndex;
            public double secondsSinceFixtureChange, secondsSinceOperation, secondsSinceInvalidation;
            public int triangles, nodes, frameTimingSamples, gpuTimingSamples;
            public long geometryBufferBytes, historyBufferEstimateBytes, totalAllocatedBytes, managedHeapBytes;
            public double lastBuildMilliseconds, peakBuildMilliseconds, averageDispatchSubmissionMilliseconds;
            public float meanFrameMilliseconds, medianFrameMilliseconds, p95FrameMilliseconds, maxFrameMilliseconds;
            public float wholeFrameGPUMilliseconds;
            public string gpuTimingMeaning;
            public Vector3 cameraPosition, cameraEuler;
            public float fieldOfView;
            public float imageMeanLuminance, imageMaxLuminance, indirectMeanLuminance, indirectMaxLuminance;
            public int indirectNonzeroPixels;
            public List<ReceiverObservation> receivers = new List<ReceiverObservation>();
        }

        [Serializable] public sealed class ReceiverObservation
        {
            public string name;
            public Vector3 worldPoint, viewport;
            public bool inView, directSunVisible;
            public Color indirectMean;
            public float sampledDepth, receiverEyeDepth, depthDifference;
            public string meaning = "5x5 GI pixel neighborhood projected from original fixture receiver. Compute texture row zero stores the top camera ray, so readback uses 1-viewport.y. May cover a different visible surface if depthDifference is large.";
        }

        public static GIVerification StartRun(int waitFrames = 64, int offFrames = 32, bool extendedTests = true)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode before running GI verification.");
            if (Running) throw new InvalidOperationException("GI verification is already running.");
            var f = FindAnyObjectByType<GIBreachScenario>();
            if (!f || !DynamicGIController.Instance) throw new InvalidOperationException("The test fixture and DynamicGIController must be running.");
            var recorder = f.GetComponent<GIVerification>();
            if (!recorder) recorder = f.gameObject.AddComponent<GIVerification>();
            recorder.WaitFrames = Mathf.Max(1, waitFrames);
            recorder.OffFrames = Mathf.Max(1, offFrames);
            recorder.ExtendedTests = extendedTests;
            recorder.StartCoroutine(recorder.Run());
            return recorder;
        }

        public void StopRun()
        {
            StopAllCoroutines();
            if (report != null && Running) { report.status = "stopped"; SaveReport(); }
            Restore();
            Running = false;
        }

        IEnumerator Run()
        {
            Running = true; Finished = false;
            fixture = FindAnyObjectByType<GIBreachScenario>(); gi = DynamicGIController.Instance;
            fixture.StopTimeline();
            oldState = fixture.state; oldLighting = fixture.environment.lighting;
            oldRoof = fixture.RoofOpening; oldView = fixture.View; oldPreview = fixture.preview;
            oldLamps = fixture.environment.interiorLightsEnabled; oldShutter = fixture.ShutterClosed;
            oldGI = gi.GIEnabled; oldDebug = gi.IndirectOnly;
            oldLimit = Application.targetFrameRate; oldVsync = QualitySettings.vSyncCount;
            oldTarget = fixture.mainCamera.targetTexture; oldAspect = fixture.mainCamera.aspect;
            oldPosition = fixture.mainCamera.transform.position; oldRotation = fixture.mainCamera.transform.rotation;
            restorePending = true;
            try
            {
                ReportDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports/DynamicGI"));
                Directory.CreateDirectory(ReportDirectory);
                target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "GI verification fixed 1920x1080", antiAliasing = 1, useDynamicScale = false };
                target.Create(); fixture.mainCamera.targetTexture = target; fixture.mainCamera.aspect = 1920f / 1080f;
                fixture.SetPreview(false); fixture.environment.SetInteriorLights(false);
                fixture.environment.Apply(BreachLighting.SunAndSky); fixture.SetView(0);
                Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
                gi.GIEnabled = true; gi.IndirectOnly = false;
                report = new Evidence
                {
                    startedUtc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion,
                    graphicsDevice = SystemInfo.graphicsDeviceName, graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                    cpu = SystemInfo.processorType, operatingSystem = SystemInfo.operatingSystem,
                    systemMemoryMB = SystemInfo.systemMemorySize, graphicsMemoryMB = SystemInfo.graphicsMemorySize,
                    giWidth = gi.Width, giHeight = gi.Height, samplesPerPixel = gi.SamplesPerPixel, bounceCount = gi.BounceCount,
                    waitFrames = WaitFrames, offFrames = OffFrames, vSync = QualitySettings.vSyncCount,
                    targetFrameRate = Application.targetFrameRate,
                    totalAllocatedBytesAtStart = Profiler.GetTotalAllocatedMemoryLong(),
                    managedHeapBytesAtStart = GC.GetTotalMemory(false)
                };
                SaveReport();
                // Allow output allocation and camera projection changes to settle.
                yield return Wait(4);
                for (int step = 0; step < 7; step++)
                {
                    BeginOperation("step" + (step + 1));
                    gi.GIEnabled = true; fixture.ApplyState((BreachState)step);
                    gi.ResetHistory();
                    yield return new WaitForEndOfFrame(); Capture("step" + (step + 1) + "-first");
                    yield return Wait(WaitFrames);
                    yield return new WaitForEndOfFrame(); Capture("step" + (step + 1) + "-on");
                    gi.GIEnabled = false;
                    yield return Wait(OffFrames);
                    yield return new WaitForEndOfFrame(); Capture("step" + (step + 1) + "-off");
                }
                if (ExtendedTests)
                {
                    gi.GIEnabled = true;
                    fixture.environment.Apply(BreachLighting.SunOnly);
                    for (int step = 1; step <= 3; step++)
                    {
                        BeginOperation("blocker-step" + (step + 1) + "-open");
                        fixture.ApplyState((BreachState)step); gi.ResetHistory();
                        yield return Wait(WaitFrames);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation);
                        BeginOperation("blocker-step" + (step + 1) + "-closed");
                        fixture.SetShutter(true);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation + "-first");
                        yield return Wait(WaitFrames);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation);
                        BeginOperation("blocker-step" + (step + 1) + "-reopened");
                        fixture.SetShutter(false);
                        yield return Wait(WaitFrames);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation);
                    }
                    fixture.ApplyState(BreachState.ConnectedRooms);
                    for (int mode = 0; mode < 3; mode++)
                    {
                        BeginOperation("lighting-" + ((BreachLighting)mode));
                        gi.GIEnabled = true; fixture.environment.Apply((BreachLighting)mode);
                        yield return Wait(WaitFrames);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation + "-on");
                        gi.GIEnabled = false;
                        yield return Wait(OffFrames);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation + "-off");
                    }
                    BeginOperation("interior-lamps-on");
                    gi.GIEnabled = true; fixture.environment.Apply(BreachLighting.SunAndSky); fixture.environment.SetInteriorLights(true);
                    yield return Wait(WaitFrames);
                    yield return new WaitForEndOfFrame(); Capture(CurrentOperation + "-gi-on");
                    gi.GIEnabled = false;
                    yield return Wait(OffFrames);
                    yield return new WaitForEndOfFrame(); Capture(CurrentOperation + "-gi-off");
                    BeginOperation("interior-lamps-off");
                    gi.GIEnabled = true; fixture.environment.SetInteriorLights(false);
                    yield return Wait(WaitFrames);
                    yield return new WaitForEndOfFrame(); Capture(CurrentOperation);

                    // Camera presets are original fixture inputs. Return to the identical
                    // first view to permit a same-world-position comparison.
                    int[] views = { 0, Mathf.Min(1, fixture.viewPositions.Length - 1), 0 };
                    for (int index = 0; index < views.Length; index++)
                    {
                        BeginOperation("camera-" + index + "-view" + views[index]);
                        fixture.SetView(views[index]);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation + "-first");
                        yield return Wait(WaitFrames);
                        yield return new WaitForEndOfFrame(); Capture(CurrentOperation);
                    }

                    BeginOperation("scene-reset");
                    fixture.ResetFixture();
                    yield return new WaitForEndOfFrame(); Capture(CurrentOperation + "-first");
                    yield return Wait(WaitFrames);
                    yield return new WaitForEndOfFrame(); Capture(CurrentOperation);
                }
                report.status = "completed";
                report.finishedUtc = DateTime.UtcNow.ToString("O");
                SaveReport(); Finished = true;
                Debug.Log("[Dynamic GI] Verification completed: " + Path.Combine(ReportDirectory, "summary.json"));
            }
            finally
            {
                Restore(); Running = false;
            }
        }

        void BeginOperation(string label)
        {
            CurrentOperation = label; operationTime = Time.realtimeSinceStartupAsDouble; operationFrame = Time.frameCount;
            ClearPerformance();
        }

        void ClearPerformance()
        {
            frameMs.Clear(); gpuMs.Clear(); dispatchMs.Clear(); peakBuildMs = 0;
        }

        IEnumerator Wait(int count)
        {
            ClearPerformance();
            for (int i = 0; i < count; i++)
            {
                yield return null;
                peakBuildMs = Math.Max(peakBuildMs, gi.LastBuildMilliseconds);
                // Exclude capture, reset, rebuild and readback stalls at the start.
                if (i < Math.Min(8, count / 2)) continue;
                frameMs.Add(Time.unscaledDeltaTime * 1000);
                dispatchMs.Add(gi.GIEnabled ? gi.LastDispatchMilliseconds : 0);
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].gpuFrameTime > 0)
                    gpuMs.Add((float)timing[0].gpuFrameTime);
            }
        }

        void Capture(string label)
        {
            // Timestamp before synchronous evidence readback/encoding, so image I/O
            // cannot inflate the reported response latency or steady allocation.
            double captureTime = Time.realtimeSinceStartupAsDouble;
            long allocatedBeforeReadback = Profiler.GetTotalAllocatedMemoryLong();
            long managedBeforeReadback = GC.GetTotalMemory(false);
            var image = ReadTexture(target, TextureFormat.RGB24, false);
            string imageName = label + ".png";
            File.WriteAllBytes(Path.Combine(ReportDirectory, imageName), image.EncodeToPNG());
            var pixels = image.GetPixels();
            float imageMean, imageMax; int ignored;
            Luminance(pixels, out imageMean, out imageMax, out ignored);
            Destroy(image);
            var observation = new Observation
            {
                label = label, image = imageName, state = fixture.state.ToString(), preset = (int)fixture.state + 1,
                lighting = fixture.environment.lighting.ToString(), view = fixture.viewNames[fixture.View],
                giEnabled = gi.GIEnabled, indirectOnly = gi.IndirectOnly, previewFill = fixture.preview,
                interiorLightsEnabled = fixture.environment.interiorLightsEnabled, shutterClosed = fixture.ShutterClosed,
                width = target.width, height = target.height, giWidth = gi.Width, giHeight = gi.Height,
                roofOpening = fixture.RoofOpening, geometryRevision = GeometryChanges.Revision,
                lightingRevision = fixture.environment.Revision, frame = Time.frameCount,
                framesSinceFixtureChange = Time.frameCount - fixture.LastChangeFrame,
                framesSinceOperation = Time.frameCount - operationFrame,
                framesSinceInvalidation = Time.frameCount - gi.LastInvalidationFrame,
                secondsSinceFixtureChange = captureTime - fixture.LastChangeTime,
                secondsSinceOperation = captureTime - operationTime,
                secondsSinceInvalidation = captureTime - gi.LastInvalidationTime,
                invalidationReason = gi.LastInvalidationReason, sampleIndex = gi.SampleIndex,
                indirectBufferMeaning = gi.GIEnabled ? "Current enabled GI radiance and eye depth cache." : "Retained GI cache while GI is disabled; this buffer is not composited in the corresponding PNG.",
                triangles = gi.TriangleCount, nodes = gi.NodeCount,
                geometryBufferBytes = Math.Max(0, gi.BufferBytes - (long)gi.Width * gi.Height * 16 * 2),
                historyBufferEstimateBytes = (long)gi.Width * gi.Height * 16 * 2,
                totalAllocatedBytes = allocatedBeforeReadback, managedHeapBytes = managedBeforeReadback,
                lastBuildMilliseconds = gi.LastBuildMilliseconds, peakBuildMilliseconds = peakBuildMs,
                averageDispatchSubmissionMilliseconds = Average(dispatchMs),
                frameTimingSamples = frameMs.Count, gpuTimingSamples = gpuMs.Count,
                timingWindowMeaning = frameMs.Count == 0 ? "Transition image only; no steady frame performance measurement in this observation." : "Steady window after discarding the first eight wait frames; excludes image readback and file writing.",
                meanFrameMilliseconds = Average(frameMs), medianFrameMilliseconds = Percentile(frameMs, .5f),
                p95FrameMilliseconds = Percentile(frameMs, .95f), maxFrameMilliseconds = Percentile(frameMs, 1),
                wholeFrameGPUMilliseconds = Average(gpuMs),
                gpuTimingMeaning = gpuMs.Count > 0 ? "Whole-frame GPU time returned by FrameTimingManager, not isolated GI pass cost." : "Unavailable: FrameTimingManager returned no valid nonzero GPU timing. No GPU GI cost measured.",
                cameraPosition = fixture.mainCamera.transform.position, cameraEuler = fixture.mainCamera.transform.eulerAngles,
                fieldOfView = fixture.mainCamera.fieldOfView, imageMeanLuminance = imageMean, imageMaxLuminance = imageMax
            };
            if (gi.Output && gi.Output.IsCreated())
            {
                var indirect = ReadTexture(gi.Output, TextureFormat.RGBAFloat, true);
                Color[] indirectPixels = indirect.GetPixels();
                Luminance(indirectPixels, out observation.indirectMeanLuminance, out observation.indirectMaxLuminance, out observation.indirectNonzeroPixels);
                // HDR evidence preserves radiance values and eye depth in alpha.
                observation.indirectImage = label + "-indirect.exr";
                File.WriteAllBytes(Path.Combine(ReportDirectory, observation.indirectImage), indirect.EncodeToEXR(Texture2D.EXRFlags.CompressZIP));
                foreach (var receiver in fixture.receivers)
                {
                    var viewport = fixture.mainCamera.WorldToViewportPoint(receiver.point.position);
                    var record = new ReceiverObservation
                    {
                        name = receiver.name, worldPoint = receiver.point.position, viewport = viewport,
                        inView = viewport.z > 0 && viewport.x >= 0 && viewport.x <= 1 && viewport.y >= 0 && viewport.y <= 1,
                        directSunVisible = fixture.SunVisibleAt(receiver.name), receiverEyeDepth = viewport.z
                    };
                    if (record.inView)
                    {
                        int x = Mathf.Clamp((int)(viewport.x * indirect.width), 0, indirect.width - 1);
                        int y = Mathf.Clamp((int)((1 - viewport.y) * indirect.height), 0, indirect.height - 1);
                        Color sum = Color.clear; int count = 0;
                        for (int oy = -2; oy <= 2; oy++) for (int ox = -2; ox <= 2; ox++)
                        {
                            int sx = Mathf.Clamp(x + ox, 0, indirect.width - 1), sy = Mathf.Clamp(y + oy, 0, indirect.height - 1);
                            sum += indirectPixels[sy * indirect.width + sx]; count++;
                        }
                        record.indirectMean = sum / count; record.sampledDepth = indirectPixels[y * indirect.width + x].a;
                        record.depthDifference = Mathf.Abs(record.sampledDepth - record.receiverEyeDepth);
                    }
                    observation.receivers.Add(record);
                }
                Destroy(indirect);
            }
            report.observations.Add(observation);
            File.WriteAllText(Path.Combine(ReportDirectory, label + ".json"), JsonUtility.ToJson(observation, true));
            SaveReport();
        }

        static Texture2D ReadTexture(RenderTexture texture, TextureFormat format, bool linear)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                var result = new Texture2D(texture.width, texture.height, format, false, linear);
                result.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false); result.Apply(false, false);
                return result;
            }
            finally { RenderTexture.active = previous; }
        }

        static void Luminance(Color[] colors, out float mean, out float max, out int nonzero)
        {
            double total = 0; max = 0; nonzero = 0;
            foreach (var color in colors)
            {
                float value = color.r * .2126f + color.g * .7152f + color.b * .0722f;
                total += value; max = Mathf.Max(max, value); if (value > .000001f) nonzero++;
            }
            mean = colors.Length > 0 ? (float)(total / colors.Length) : 0;
        }
        static float Average(List<float> values) { double sum = 0; foreach (float value in values) sum += value; return values.Count > 0 ? (float)(sum / values.Count) : 0; }
        static double Average(List<double> values) { double sum = 0; foreach (double value in values) sum += value; return values.Count > 0 ? sum / values.Count : 0; }
        static float Percentile(List<float> values, float p)
        {
            if (values.Count == 0) return 0;
            var sorted = values.ToArray(); Array.Sort(sorted);
            return sorted[Mathf.Clamp(Mathf.CeilToInt(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        }
        void SaveReport() { if (report != null && !string.IsNullOrEmpty(ReportDirectory)) File.WriteAllText(Path.Combine(ReportDirectory, "summary.json"), JsonUtility.ToJson(report, true)); }

        void Restore()
        {
            if (!restorePending) return; restorePending = false;
            if (fixture)
            {
                fixture.ApplyState(oldState); fixture.SetRoofOpening(oldRoof); fixture.SetShutter(oldShutter);
                fixture.SetPreview(oldPreview); fixture.environment.Apply(oldLighting); fixture.environment.SetInteriorLights(oldLamps);
                fixture.SetView(oldView); fixture.mainCamera.transform.SetPositionAndRotation(oldPosition, oldRotation);
                fixture.mainCamera.targetTexture = oldTarget; fixture.mainCamera.aspect = oldAspect;
            }
            if (gi) { gi.GIEnabled = oldGI; gi.IndirectOnly = oldDebug; gi.ResetHistory(); }
            Application.targetFrameRate = oldLimit; QualitySettings.vSyncCount = oldVsync;
            if (target) { target.Release(); Destroy(target); target = null; }
        }
        void OnDisable() { if (Running) StopRun(); else Restore(); }
    }
}
