using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GIEvaluation;
using UnityEngine;

namespace DynamicGI
{
    /// <summary>Opt-in integration checks. Does not alter fixture meshes, states,
    /// cameras, lighting or the seven-step sequence. Run after the visual suite.</summary>
    public sealed class GIGeometryChecks : MonoBehaviour
    {
        [Serializable] public sealed class Check
        {
            public string operation;
            public bool passed, synchronousHistoryReset, gpuBoundsContainObject, gpuVertexFound;
            public int beforeTriangles, afterTriangles, beforeBuilds, afterBuilds, frames;
            public double seconds;
            public string note;
        }
        [Serializable] public sealed class Report
        {
            public string unity, device, graphicsApi, scene, note;
            public bool complete, passed, stableSignature;
            public int initialTriangles, finalTriangles, triangleStride, nodeStride;
            public List<Check> checks = new List<Check>();
        }
        public static bool Running { get; private set; }
        public static string LastReportPath { get; private set; }
        DynamicGIController controller;
        GIGeometry snapshot;
        Report report;
        GameObject temporary;
        Material material;
        Mesh replacement;
        bool previousGI;

        public static void StartRun()
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play mode first.");
            if (Running) throw new InvalidOperationException("Geometry checks are already running.");
            var c = FindAnyObjectByType<DynamicGIController>();
            if (!c || !c.enabled) throw new InvalidOperationException("An enabled DynamicGIController is required.");
            var fixture = FindAnyObjectByType<GIBreachScenario>();
            if (fixture && fixture.TimelineRunning) throw new InvalidOperationException("Stop the fixture timeline before geometry checks.");
            Running = true;
            var host = new GameObject("Dynamic GI geometry checks");
            host.AddComponent<GIGeometryChecks>().StartCoroutine("Run");
        }

        IEnumerator Run()
        {
            controller = FindAnyObjectByType<DynamicGIController>();
            previousGI = controller.GIEnabled; controller.GIEnabled = true;
            snapshot = new GIGeometry();
            report = new Report
            {
                unity = Application.unityVersion, device = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), scene = gameObject.scene.name,
                triangleStride = System.Runtime.InteropServices.Marshal.SizeOf<GIGeometry.Triangle>(),
                nodeStride = System.Runtime.InteropServices.Marshal.SizeOf<GIGeometry.Node>(),
                note = "Runtime integration/data checks, not a visual GI accuracy or performance test. Temporary cube is behind the current camera, outside the fixture. GPU structured buffers are read back synchronously for assertions."
            };
            yield return new WaitForSecondsRealtime(.8f);
            report.initialTriangles = controller.TriangleCount;
            int signature = GIGeometry.ComputeSceneSignature();
            yield return new WaitForSecondsRealtime(.65f);
            report.stableSignature = signature == GIGeometry.ComputeSceneSignature();
            report.passed = report.stableSignature && report.triangleStride == 112 && report.nodeStride == 48;

            int builds = controller.BuildCount, triangles = controller.TriangleCount;
            temporary = GameObject.CreatePrimitive(PrimitiveType.Cube);
            temporary.name = "Temporary generic geometry input";
            temporary.transform.position = controller.TargetCamera.transform.position - controller.TargetCamera.transform.forward * 500;
            temporary.transform.rotation = Quaternion.Euler(11, 29, 7);
            temporary.transform.localScale = new Vector3(1.3f, .9f, 1.7f);
            temporary.GetComponent<Collider>().enabled = false;
            var renderer = temporary.GetComponent<MeshRenderer>();
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", new Color(.6f, .2f, .1f)); renderer.sharedMaterial = material;
            int frame = Time.frameCount; double time = Time.realtimeSinceStartupAsDouble;
            GeometryChanges.Publish(GeometryChangeKind.Added, renderer, new Bounds());
            bool reset = controller.SampleIndex == 0;
            yield return null; yield return null;
            Record("Added event, off-camera opaque cube", builds, triangles, frame, time, reset, renderer, true, report.initialTriangles + 12);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            var before = renderer.bounds; frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            temporary.transform.position += Vector3.up * 7;
            GeometryChanges.Publish(GeometryChangeKind.TransformChanged, renderer, before);
            reset = controller.SampleIndex == 0;
            yield return null; yield return null;
            Record("TransformChanged event", builds, triangles, frame, time, reset, renderer, true, triangles);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            temporary.transform.position += Vector3.right * 3;
            yield return new WaitForSecondsRealtime(.8f);
            Record("Unnotified transform detected by 0.5-second polling", builds, triangles, frame, time, true, renderer, true, triangles);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            var filter = temporary.GetComponent<MeshFilter>();
            replacement = Instantiate(filter.sharedMesh); replacement.name = "Temporary edited cube mesh";
            var vertices = replacement.vertices;
            for (int i = 0; i < vertices.Length; ++i) vertices[i] += new Vector3(.15f, .05f, -.08f);
            replacement.vertices = vertices; replacement.RecalculateBounds();
            before = renderer.bounds; filter.sharedMesh = replacement;
            frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            GeometryChanges.Publish(GeometryChangeKind.MeshChanged, renderer, before);
            reset = controller.SampleIndex == 0;
            yield return null; yield return null;
            Record("Runtime mesh replacement event", builds, triangles, frame, time, reset, renderer, true, triangles);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            before = renderer.bounds; vertices[0] *= .8f;
            replacement.vertices = vertices; replacement.RecalculateBounds();
            frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            GeometryChanges.Publish(GeometryChangeKind.MeshChanged, renderer, before);
            reset = controller.SampleIndex == 0;
            yield return null; yield return null;
            Record("In-place vertex edit with MeshChanged event", builds, triangles, frame, time, reset, renderer, true, triangles);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            before = renderer.bounds; frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            GeometryChanges.Publish(GeometryChangeKind.Removed, renderer, before);
            reset = controller.SampleIndex == 0; temporary.SetActive(false);
            yield return null; yield return null;
            Record("Removed/deactivated event", builds, triangles, frame, time, reset, null, false, report.initialTriangles);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            temporary.SetActive(true); GeometryChanges.Publish(GeometryChangeKind.Added, renderer, new Bounds());
            reset = controller.SampleIndex == 0;
            yield return null; yield return null;
            Record("Reactivation Added event", builds, triangles, frame, time, reset, renderer, true, report.initialTriangles + 12);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            before = renderer.bounds; frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            GeometryChanges.Publish(GeometryChangeKind.Removed, renderer, before);
            reset = controller.SampleIndex == 0; Destroy(temporary); temporary = null;
            yield return null; yield return null;
            Record("Destroy/remove event", builds, triangles, frame, time, reset, null, false, report.initialTriangles);

            builds = controller.BuildCount; triangles = controller.TriangleCount;
            frame = Time.frameCount; time = Time.realtimeSinceStartupAsDouble;
            GeometryChanges.Publish(GeometryChangeKind.SceneReset, null, new Bounds());
            reset = controller.SampleIndex == 0;
            yield return null; yield return null;
            Record("SceneReset event", builds, triangles, frame, time, reset, null, false, report.initialTriangles);

            report.finalTriangles = controller.TriangleCount;
            report.complete = true; report.passed &= report.finalTriangles == report.initialTriangles;
            Save();
            Debug.Log("[Dynamic GI] Geometry checks " + (report.passed ? "PASS " : "FAIL ") + LastReportPath);
            Destroy(gameObject);
        }

        void Record(string operation, int builds, int triangles, int frame, double time, bool reset, MeshRenderer renderer, bool checkObject, int expectedTriangles)
        {
            var check = new Check
            {
                operation = operation, beforeBuilds = builds, afterBuilds = controller.BuildCount,
                beforeTriangles = triangles, afterTriangles = controller.TriangleCount,
                frames = Time.frameCount - frame, seconds = Time.realtimeSinceStartupAsDouble - time,
                synchronousHistoryReset = reset, gpuBoundsContainObject = !checkObject, gpuVertexFound = !checkObject,
                note = "Controller counts, history invalidation, and an independently rebuilt GPU BVH snapshot. This assertion does not prove pixel radiance correctness."
            };
            snapshot.Rebuild();
            if (checkObject && renderer && snapshot.NodeCount > 0)
            {
                var nodes = new GIGeometry.Node[1]; snapshot.Nodes.GetData(nodes, 0, 0, 1);
                var bounds = new Bounds((nodes[0].min + nodes[0].max) * .5f, nodes[0].max - nodes[0].min);
                var objectBounds = renderer.bounds; bounds.Expand(.001f);
                check.gpuBoundsContainObject = bounds.Contains(objectBounds.min) && bounds.Contains(objectBounds.max);
                var gpuTriangles = new GIGeometry.Triangle[snapshot.TriangleCount]; snapshot.Triangles.GetData(gpuTriangles);
                var filter = renderer.GetComponent<MeshFilter>();
                var target = renderer.localToWorldMatrix.MultiplyPoint3x4(filter.sharedMesh.vertices[filter.sharedMesh.triangles[0]]);
                foreach (var t in gpuTriangles) if ((t.v0 - target).sqrMagnitude < 1e-6f) { check.gpuVertexFound = true; break; }
            }
            check.passed = reset && check.afterBuilds > builds && check.afterTriangles == expectedTriangles && snapshot.TriangleCount == expectedTriangles && check.gpuBoundsContainObject && check.gpuVertexFound;
            report.checks.Add(check); report.passed &= check.passed;
            Save();
        }

        void Save()
        {
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Reports/DynamicGI"));
            Directory.CreateDirectory(directory); LastReportPath = Path.Combine(directory, "geometry-checks.json");
            File.WriteAllText(LastReportPath, JsonUtility.ToJson(report, true));
        }

        void OnDestroy()
        {
            if (temporary) { GeometryChanges.Publish(GeometryChangeKind.Removed, temporary.GetComponent<Renderer>(), temporary.GetComponent<Renderer>().bounds); Destroy(temporary); }
            if (replacement) Destroy(replacement); if (material) Destroy(material);
            snapshot?.Dispose(); snapshot = null;
            if (controller) controller.GIEnabled = previousGI;
            Running = false;
            if (report != null && !report.complete) { report.passed = false; report.note += " INTERRUPTED: sequence did not complete."; Save(); }
        }
    }
}
