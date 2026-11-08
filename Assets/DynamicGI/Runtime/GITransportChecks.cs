using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GIEvaluation;
using UnityEngine;

namespace DynamicGI
{
    /// <summary>
    /// Camera-independent numerical evidence using the original fixture receiver
    /// positions. These are white Lambertian diagnostic receivers, not screenshots
    /// or predictions of the actual pixel/material values.
    /// </summary>
    public sealed class GITransportChecks : MonoBehaviour
    {
        public static bool Running { get; private set; }
        public static bool Finished { get; private set; }
        public static string ReportPath { get; private set; }
        public string CurrentOperation { get; private set; }
        public int Samples = 8192;

        GIBreachScenario fixture;
        DynamicGIController gi;
        TransportReport report;
        BreachState oldState;
        BreachLighting oldLighting;
        int oldRoof, oldView, oldLimit, oldVSync;
        bool oldPreview, oldLamps, oldShutter, oldGI, oldDebug, restorePending;
        Vector3 oldCameraPosition;
        Quaternion oldCameraRotation;
        int operationFrame;
        double operationTime;
        readonly Dictionary<string, Query> queries = new Dictionary<string, Query>();

        [Serializable] public sealed class TransportReport
        {
            public string startedUtc, finishedUtc, status = "running", unity, graphicsDevice, graphicsAPI;
            public int samplesPerProbe, bounceCount, width, height;
            public string method = "World-space GPU diagnostic probes at each original fixture receiver position + normal*0.02, with normalized receiver normal and white Lambertian receiver albedo. Total includes visible diffuse sky and reflected light. BounceOnly excludes sky paths that escape before hitting a secondary surface. Neither query adds primary direct sun. These raw linear radiances do not include the actual receiver material, exposure, tone mapping or screen upsampling.";
            public string timingMeaning = "Each query synchronously dispatches and reads back the stated sample budget. Diagnostic milliseconds include GPU completion and CPU readback; they are not regular interactive GI frame costs. Mutations settle for two frames before querying; this test measures transport after update, not minimum update latency.";
            public string comparisonMeaning = "Checks describe only the recorded diagnostic receivers and sample budget. Geometrically restored and camera-only comparisons use identical world rays if SampleProbes is deterministic. A positive BounceOnly value proves a reflected path at this receiver; it does not establish visual quality or lack of leaks everywhere.";
            public List<Query> queries = new List<Query>();
            public List<Check> checks = new List<Check>();
            public int passed, failed;
        }

        [Serializable] public sealed class Query
        {
            public string label, state, lighting, view, invalidationReason;
            public bool interiorLightsEnabled, previewFill, shutterClosed;
            public int preset, frame, geometryRevision, lightingRevision, framesSinceOperation, framesSinceFixtureChange, framesSinceInvalidation;
            public int samplesPerProbe, bounceCount, triangles, nodes;
            public double secondsSinceOperation, secondsSinceFixtureChange, secondsSinceInvalidation;
            public double totalDiagnosticMilliseconds, bounceOnlyDiagnosticMilliseconds, lastGeometryBuildMilliseconds;
            public Vector3 cameraPosition, cameraEuler;
            public List<Probe> probes = new List<Probe>();
        }

        [Serializable] public sealed class Probe
        {
            public string name;
            public Vector3 originalPoint, sampleOrigin, normal, totalRGB, bounceOnlyRGB;
            public float totalLuminance, bounceOnlyLuminance;
            public bool directSunVisible, finite;
        }

        [Serializable] public sealed class Check
        {
            public string name, status, meaning;
            public double measured, threshold;
        }

        public static GITransportChecks StartRun(int samples = 8192)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode to run transport checks.");
            if (Running || GIVerification.Running) throw new InvalidOperationException("Wait until the other verification completes.");
            var f = FindAnyObjectByType<GIBreachScenario>();
            if (!f || !DynamicGIController.Instance) throw new InvalidOperationException("A running fixture and GI controller are required.");
            var recorder = f.GetComponent<GITransportChecks>();
            if (!recorder) recorder = f.gameObject.AddComponent<GITransportChecks>();
            recorder.Samples = Mathf.Clamp(samples, 256, 131072);
            recorder.StartCoroutine(recorder.Run());
            return recorder;
        }

        public void StopRun()
        {
            StopAllCoroutines();
            if (report != null && Running) { report.status = "stopped"; Save(); }
            Restore(); Running = false;
        }

        IEnumerator Run()
        {
            Running = true; Finished = false;
            fixture = FindAnyObjectByType<GIBreachScenario>(); gi = DynamicGIController.Instance;
            fixture.StopTimeline();
            oldState = fixture.state; oldLighting = fixture.environment.lighting;
            oldRoof = fixture.RoofOpening; oldView = fixture.View;
            oldPreview = fixture.preview; oldLamps = fixture.environment.interiorLightsEnabled;
            oldShutter = fixture.ShutterClosed; oldGI = gi.GIEnabled; oldDebug = gi.IndirectOnly;
            oldLimit = Application.targetFrameRate; oldVSync = QualitySettings.vSyncCount;
            oldCameraPosition = fixture.mainCamera.transform.position; oldCameraRotation = fixture.mainCamera.transform.rotation;
            restorePending = true;
            try
            {
                var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports/DynamicGI"));
                Directory.CreateDirectory(folder); ReportPath = Path.Combine(folder, "transport-checks.json");
                queries.Clear();
                fixture.SetPreview(false); fixture.environment.SetInteriorLights(false);
                fixture.environment.Apply(BreachLighting.SunAndSky); fixture.SetView(0);
                gi.GIEnabled = true; gi.IndirectOnly = false;
                Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
                report = new TransportReport
                {
                    startedUtc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion,
                    graphicsDevice = SystemInfo.graphicsDeviceName, graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                    samplesPerProbe = Samples, bounceCount = gi.BounceCount,
                    width = fixture.mainCamera.pixelWidth, height = fixture.mainCamera.pixelHeight
                };
                Save();
                for (int step = 0; step < 7; step++)
                {
                    Begin("combined-step" + (step + 1));
                    fixture.ApplyState((BreachState)step);
                    yield return Settle(); Capture();
                }
                foreach (var lighting in new[] { BreachLighting.SunOnly, BreachLighting.SkyOnly })
                {
                    fixture.environment.Apply(lighting);
                    foreach (var step in new[] { BreachState.Sealed, BreachState.ConnectedRooms, BreachState.Restored })
                    {
                        Begin(lighting + "-" + step);
                        fixture.ApplyState(step);
                        yield return Settle(); Capture();
                    }
                }
                fixture.environment.Apply(BreachLighting.SunOnly);
                for (int step = 1; step <= 3; step++)
                {
                    Begin("blocker-step" + (step + 1) + "-open");
                    fixture.ApplyState((BreachState)step);
                    yield return Settle(); Capture();
                    Begin("blocker-step" + (step + 1) + "-blocked");
                    fixture.SetShutter(true);
                    yield return Settle(); Capture();
                    Begin("blocker-step" + (step + 1) + "-reopened");
                    fixture.SetShutter(false);
                    yield return Settle(); Capture();
                }
                fixture.environment.Apply(BreachLighting.SunAndSky); fixture.ApplyState(BreachState.ConnectedRooms);
                int[] cameraViews = { 0, Mathf.Min(1, fixture.viewPositions.Length - 1), 0 };
                for (int i = 0; i < cameraViews.Length; i++)
                {
                    Begin("camera-" + i); fixture.SetView(cameraViews[i]);
                    yield return Settle(); Capture();
                }
                Evaluate();
                report.finishedUtc = DateTime.UtcNow.ToString("O"); report.status = "completed";
                Save(); Finished = true;
                UnityEngine.Debug.Log("[Dynamic GI] Transport probes completed: " + report.passed + " passed, " + report.failed + " failed checks. " + ReportPath);
            }
            finally { Restore(); Running = false; }
        }

        void Begin(string label)
        {
            CurrentOperation = label; operationFrame = Time.frameCount; operationTime = Time.realtimeSinceStartupAsDouble;
        }
        static IEnumerator Settle() { yield return null; yield return null; }

        void Capture()
        {
            double captureTime = Time.realtimeSinceStartupAsDouble;
            int count = fixture.receivers.Length;
            var positions = new Vector3[count]; var normals = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                normals[i] = fixture.receivers[i].normal.normalized;
                positions[i] = fixture.receivers[i].point.position + normals[i] * .02f;
            }
            var stopwatch = Stopwatch.StartNew();
            Color[] total = gi.SampleProbes(positions, normals, Samples, false);
            double totalMs = stopwatch.Elapsed.TotalMilliseconds; stopwatch.Restart();
            Color[] bounced = gi.SampleProbes(positions, normals, Samples, true);
            double bouncedMs = stopwatch.Elapsed.TotalMilliseconds; stopwatch.Stop();
            var result = new Query
            {
                label = CurrentOperation, state = fixture.state.ToString(), preset = (int)fixture.state + 1,
                lighting = fixture.environment.lighting.ToString(), view = fixture.viewNames[fixture.View],
                previewFill = fixture.preview, interiorLightsEnabled = fixture.environment.interiorLightsEnabled,
                shutterClosed = fixture.ShutterClosed, frame = Time.frameCount,
                geometryRevision = GeometryChanges.Revision, lightingRevision = fixture.environment.Revision,
                framesSinceOperation = Time.frameCount - operationFrame,
                framesSinceFixtureChange = Time.frameCount - fixture.LastChangeFrame,
                framesSinceInvalidation = Time.frameCount - gi.LastInvalidationFrame,
                secondsSinceOperation = captureTime - operationTime,
                secondsSinceFixtureChange = captureTime - fixture.LastChangeTime,
                secondsSinceInvalidation = captureTime - gi.LastInvalidationTime,
                invalidationReason = gi.LastInvalidationReason, samplesPerProbe = Samples, bounceCount = gi.BounceCount,
                triangles = gi.TriangleCount, nodes = gi.NodeCount, lastGeometryBuildMilliseconds = gi.LastBuildMilliseconds,
                totalDiagnosticMilliseconds = totalMs, bounceOnlyDiagnosticMilliseconds = bouncedMs,
                cameraPosition = fixture.mainCamera.transform.position, cameraEuler = fixture.mainCamera.transform.eulerAngles
            };
            for (int i = 0; i < count; i++)
            {
                result.probes.Add(new Probe
                {
                    name = fixture.receivers[i].name, originalPoint = fixture.receivers[i].point.position,
                    sampleOrigin = positions[i], normal = normals[i],
                    totalRGB = new Vector3(total[i].r, total[i].g, total[i].b),
                    bounceOnlyRGB = new Vector3(bounced[i].r, bounced[i].g, bounced[i].b),
                    totalLuminance = Luminance(total[i]), bounceOnlyLuminance = Luminance(bounced[i]),
                    directSunVisible = fixture.SunVisibleAt(fixture.receivers[i].name),
                    finite = Finite(total[i]) && Finite(bounced[i])
                });
            }
            queries[result.label] = result; report.queries.Add(result); Save();
        }

        void Evaluate()
        {
            bool finite = true; foreach (var q in report.queries) foreach (var p in q.probes) finite &= p.finite;
            Add("All diagnostic radiances finite", finite, finite ? 1 : 0, 1, "Checks all recorded RGB values for NaN/Infinity.");
            foreach (string prefix in new[] { "combined", "SunOnly", "SkyOnly" })
            {
                string first = prefix == "combined" ? "combined-step1" : prefix + "-Sealed";
                string opened = prefix == "combined" ? "combined-step4" : prefix + "-ConnectedRooms";
                string restored = prefix == "combined" ? "combined-step7" : prefix + "-Restored";
                float sealedMaximum = MaxRadiance(queries[first], false);
                float restoredMaximum = MaxRadiance(queries[restored], false);
                Add(prefix + ": sealed exterior contribution zero", sealedMaximum <= 1e-6f, sealedMaximum, 1e-6, "Maximum raw RGB component across original world-space diagnostic receivers; interior lamps disabled.");
                Add(prefix + ": restored exterior contribution zero", restoredMaximum <= 1e-6f, restoredMaximum, 1e-6, "Maximum raw RGB component after restoring the original envelope.");
                float restoredDifference = Difference(queries[first], queries[restored]);
                Add(prefix + ": restored matches sealed", restoredDifference <= 1e-6f, restoredDifference, 1e-6, "Maximum absolute RGB difference across both Total and BounceOnly diagnostics.");
                float bounceMaximum = MaxRadiance(queries[opened], true);
                Add(prefix + ": open state has reflected light", bounceMaximum > 1e-6f, bounceMaximum, 1e-6, "BounceOnly excludes paths directly escaping the primary receiver to the sky, so a nonzero value requires a secondary surface hit.");
            }
            for (int step = 2; step <= 6; step++)
            {
                float reflected = MaxRadiance(queries["combined-step" + step], true);
                Add("Step " + step + ": reflected contribution exists", reflected > 1e-6f, reflected, 1e-6, "At least one fixture diagnostic receiver receives reflected radiance; this does not assert every receiver is lit.");
            }
            var before = FindProbe(queries["combined-step3"], "Room_B_Indirect");
            var after = FindProbe(queries["combined-step4"], "Room_B_Indirect");
            float roomBIncrease = after.bounceOnlyLuminance - before.bounceOnlyLuminance;
            Add("Opening inner partition increases Room B reflected radiance", roomBIncrease > 1e-6f, roomBIncrease, 1e-6, "Same original Room_B_Indirect world receiver, same exterior opening/roof/lights; only fixture partition and its debris change.");
            Add("Room B comparison receiver remains out of direct sun", !before.directSunVisible && !after.directSunVisible, after.directSunVisible ? 1 : 0, 0, "Original fixture visibility query at Room_B_Indirect.");
            for (int step = 2; step <= 4; step++)
            {
                var open = queries["blocker-step" + step + "-open"];
                var blocked = queries["blocker-step" + step + "-blocked"];
                var reopened = queries["blocker-step" + step + "-reopened"];
                float decrease = SumBounce(open) - SumBounce(blocked);
                Add("Step " + step + ": blocker reduces reflected input", decrease > 1e-6f, decrease, 1e-6, "Sum of original receivers' BounceOnly luminance; SunOnly and interior lamps off. Only outside blocker moves between these queries.");
                float reopeningDifference = Difference(open, reopened);
                Add("Step " + step + ": blocker reopening restores input", reopeningDifference <= 1e-6f, reopeningDifference, 1e-6, "Identical world-ray diagnostic comparisons; tolerance is absolute linear RGB.");
            }
            for (int i = 1; i <= 2; i++)
            {
                float difference = Difference(queries["camera-0"], queries["camera-" + i]);
                Add("Camera-only change " + i + " preserves world transport", difference <= 1e-6f, difference, 1e-6, "All original world receivers including offscreen points; camera changes only. This tests transport camera independence, not screen accumulation noise or edge filtering.");
            }
        }

        void Add(string name, bool passed, double measured, double threshold, string meaning)
        {
            report.checks.Add(new Check { name = name, status = passed ? "passed" : "failed", measured = measured, threshold = threshold, meaning = meaning });
            if (passed) report.passed++; else report.failed++;
        }
        static float MaxRadiance(Query q, bool bounce)
        {
            float maximum = 0;
            foreach (var p in q.probes)
            {
                Vector3 rgb = bounce ? p.bounceOnlyRGB : p.totalRGB;
                maximum = Mathf.Max(maximum, Mathf.Max(rgb.x, Mathf.Max(rgb.y, rgb.z)));
            }
            return maximum;
        }
        static float SumBounce(Query q) { float sum = 0; foreach (var p in q.probes) sum += p.bounceOnlyLuminance; return sum; }
        static Probe FindProbe(Query q, string name)
        {
            foreach (var p in q.probes) if (p.name == name) return p;
            throw new InvalidOperationException("Missing original fixture receiver: " + name);
        }
        static float Difference(Query a, Query b)
        {
            float maximum = 0;
            foreach (var ap in a.probes)
            {
                var bp = FindProbe(b, ap.name);
                foreach (var d in new[] { ap.totalRGB - bp.totalRGB, ap.bounceOnlyRGB - bp.bounceOnlyRGB })
                    maximum = Mathf.Max(maximum, Mathf.Max(Mathf.Abs(d.x), Mathf.Max(Mathf.Abs(d.y), Mathf.Abs(d.z))));
            }
            return maximum;
        }
        static float Luminance(Color c) => c.r * .2126f + c.g * .7152f + c.b * .0722f;
        static bool Finite(Color c) => !(float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) || float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b));
        void Save() { if (report != null && !string.IsNullOrEmpty(ReportPath)) File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true)); }
        void Restore()
        {
            if (!restorePending) return; restorePending = false;
            if (fixture)
            {
                fixture.ApplyState(oldState); fixture.SetRoofOpening(oldRoof); fixture.SetShutter(oldShutter);
                fixture.SetPreview(oldPreview); fixture.environment.Apply(oldLighting); fixture.environment.SetInteriorLights(oldLamps);
                fixture.SetView(oldView); fixture.mainCamera.transform.SetPositionAndRotation(oldCameraPosition, oldCameraRotation);
            }
            if (gi) { gi.GIEnabled = oldGI; gi.IndirectOnly = oldDebug; gi.ResetHistory(); }
            Application.targetFrameRate = oldLimit; QualitySettings.vSyncCount = oldVSync;
        }
        void OnDisable() { if (Running) StopRun(); else Restore(); }
    }
}
