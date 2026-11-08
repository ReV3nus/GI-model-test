using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace GIEvaluation
{
    public enum BreachState { Sealed, SmallBreach, LargeBreach, ConnectedRooms, SmallRoofBreach, LargeRoofBreach, Restored }

    public sealed class GIBreachScenario : MonoBehaviour
    {
        public MeshFilter exteriorWall;
        public Mesh[] exteriorMeshes;
        public GameObject smallDebris, largeDebris;
        public MeshFilter interiorWall;
        public Mesh interiorIntact, interiorOpened;
        public GameObject interiorDebris;
        public MeshFilter roof;
        public Mesh[] roofMeshes;
        public GameObject smallRoofDebris, largeRoofDebris;
        public GameObject[] roofTrim;
        public Transform shutter;
        public Vector3 shutterParked, shutterBlocking;
        public Collider[] envelopeColliders;
        public GIReceiverSample[] receivers;
        public GIBreachEnvironment environment;
        public GameObject previewLights;
        public Volume previewGrade;
        public Camera mainCamera;
        public Vector3[] viewPositions, viewTargets;
        public string[] viewNames;
        public BreachState state;
        public bool preview;
        [Min(1)] public float timelineInterval = 3;
        public bool captureTimeline;
        [Min(0)] public int editorFrameLimit = 30;
        public event Action<GIBreachScenario> StateChanged;
        public int LastChangeFrame { get; private set; }
        public double LastChangeTime { get; private set; }
        public int View { get; private set; }
        public int ExteriorOpening { get; private set; }
        public int RoofOpening { get; private set; }
        public bool InteriorOpen { get; private set; }
        public bool ShutterClosed { get; private set; }
        public bool TimelineRunning => timeline != null;
        private Coroutine timeline;
        private GUIStyle textStyle;
        private GUIStyle markerStyle;
        private Vector2 panelScroll;
        private bool showOptions;
        private string message = "";
        private int previousFrameLimit, previousVSync;
        private bool changedEditorLimit;
        private bool previousRunInBackground;
        private static readonly string[] StateLabels =
        {
            "Intact room", "Small exterior hole", "Large exterior hole",
            "Open the inner wall", "Small ceiling hole", "Large ceiling holes", "Restore everything"
        };
        private static readonly string[] Descriptions =
        {
            "All openings are closed. Dim interior lamps remain on.",
            "A small hole opens in the front wall. Watch the daylight patch.",
            "The front-wall hole is larger. The inner wall is still CLOSED.",
            "The inner wall is now OPEN. Look through the opening to Room B.",
            "A small ceiling hole opens above Room A. Wall openings remain open.",
            "Both rooms now have ceiling holes. Watch the ceiling and workbench.",
            "All wall and ceiling openings are closed again. Compare with step 1."
        };

        private void Awake()
        {
            previousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
            if (Application.isEditor && editorFrameLimit > 0)
            {
                previousFrameLimit = Application.targetFrameRate; previousVSync = QualitySettings.vSyncCount;
                Application.targetFrameRate = editorFrameLimit; QualitySettings.vSyncCount = 0; changedEditorLimit = true;
            }
            ApplyState(state);
            SetPreview(preview);
            SetView(0);
        }

        private static void SetMesh(MeshFilter filter, Mesh mesh)
        {
            if (filter.sharedMesh == mesh) return;
            var renderer = filter.GetComponent<Renderer>(); var before = renderer.bounds;
            filter.sharedMesh = mesh;
            var collider = filter.GetComponent<MeshCollider>(); if (collider) collider.sharedMesh = mesh;
            GeometryChanges.Publish(GeometryChangeKind.MeshChanged, renderer, before);
        }

        private static void SetGroup(GameObject group, bool active)
        {
            if (group.activeSelf == active) return;
            var renderers = group.GetComponentsInChildren<Renderer>(true);
            if (!active)
                foreach (var renderer in renderers) GeometryChanges.Publish(GeometryChangeKind.Removed, renderer, renderer.bounds);
            group.SetActive(active);
            if (active)
                foreach (var renderer in renderers) GeometryChanges.Publish(GeometryChangeKind.Added, renderer, new Bounds());
        }

        public void ApplyState(BreachState next)
        {
            int opening = next == BreachState.Sealed || next == BreachState.Restored ? 0 : next == BreachState.SmallBreach ? 1 : 2;
            bool inside = next >= BreachState.ConnectedRooms && next <= BreachState.LargeRoofBreach;
            SetMesh(exteriorWall, exteriorMeshes[opening]);
            SetGroup(smallDebris, opening == 1); SetGroup(largeDebris, opening == 2);
            SetMesh(interiorWall, inside ? interiorOpened : interiorIntact); SetGroup(interiorDebris, inside);
            SetRoofOpening(next == BreachState.SmallRoofBreach ? 1 : next == BreachState.LargeRoofBreach ? 2 : 0, false);
            SetShutter(false, false);
            ExteriorOpening = opening; InteriorOpen = inside;
            state = next; LastChangeFrame = Time.frameCount; LastChangeTime = Time.realtimeSinceStartupAsDouble;
            message = ""; Physics.SyncTransforms(); StateChanged?.Invoke(this);
            Debug.Log("[GI Breach] Step " + ((int)state + 1) + " " + StateLabels[(int)state] + " | geometry revision " + GeometryChanges.Revision);
        }

        public bool CanCompareWallDaylight => ExteriorOpening > 0 && RoofOpening == 0;
        public void SetShutter(bool blocked, bool notify = true)
        {
            var shutterRenderer = shutter.GetComponent<Renderer>(); var oldShutter = shutterRenderer.bounds;
            var shutterPosition = blocked ? shutterBlocking : shutterParked;
            if (shutter.position != shutterPosition)
            {
                shutter.position = shutterPosition;
                GeometryChanges.Publish(GeometryChangeKind.TransformChanged, shutterRenderer, oldShutter);
            }
            ShutterClosed = blocked; Physics.SyncTransforms();
            if (notify)
            {
                LastChangeFrame = Time.frameCount; LastChangeTime = Time.realtimeSinceStartupAsDouble;
                StateChanged?.Invoke(this);
            }
        }
        public void ToggleShutter()
        {
            StopTimeline();
            if (!CanCompareWallDaylight) { message = "For the B comparison, choose step 2, 3 or 4 first. The ceiling must be intact."; return; }
            SetShutter(!ShutterClosed);
            message = ShutterClosed ? "Only the outside blocker moved. Sunlight through the wall opening is now blocked." : "The outside blocker moved away. Daylight through the wall opening returns.";
        }

        public void NextState() => ApplyState((BreachState)(((int)state + 1) % 7));
        public void SetRoofOpening(int opening, bool notify = true)
        {
            if (!roof || roofMeshes == null || roofMeshes.Length < 3) return;
            opening = Mathf.Clamp(opening, 0, 2);
            if (RoofOpening == opening && roof.sharedMesh == roofMeshes[opening]) return;
            SetMesh(roof, roofMeshes[opening]);
            SetGroup(smallRoofDebris, opening == 1); SetGroup(largeRoofDebris, opening == 2);
            if (roofTrim != null) foreach (var trim in roofTrim) if (trim) SetGroup(trim, opening < 2);
            RoofOpening = opening; LastChangeFrame = Time.frameCount; LastChangeTime = Time.realtimeSinceStartupAsDouble;
            Physics.SyncTransforms(); if (notify) StateChanged?.Invoke(this);
            Debug.Log("[GI Breach] Roof opening " + opening + " | geometry revision " + GeometryChanges.Revision);
        }
        public void CycleRoof() { StopTimeline(); ApplyState(RoofOpening == 0 ? BreachState.SmallRoofBreach : RoofOpening == 1 ? BreachState.LargeRoofBreach : BreachState.ConnectedRooms); }
        public void ResetFixture()
        {
            StopTimeline(); ApplyState(BreachState.Sealed); SetView(0);
            GeometryChanges.Publish(GeometryChangeKind.SceneReset, null, new Bounds());
        }
        public void SetPreview(bool enabled)
        {
            preview = enabled; if (previewLights) previewLights.SetActive(enabled); if (previewGrade) previewGrade.enabled = enabled;
            if (Application.isPlaying) GeometryChanges.Publish(GeometryChangeKind.SceneReset, null, new Bounds());
        }
        public void SetView(int index)
        {
            View = Mathf.Clamp(index, 0, viewPositions.Length - 1);
            mainCamera.transform.position = viewPositions[View]; mainCamera.transform.LookAt(viewTargets[View]);
        }
        public void NextView() => SetView((View + 1) % viewPositions.Length);
        public void ToggleTimeline()
        {
            if (timeline != null) { StopTimeline(); return; }
            timeline = StartCoroutine(Timeline());
        }
        private IEnumerator Timeline()
        {
            SetPreview(false);
            for (int i = 0; i < 7; i++)
            {
                ApplyState((BreachState)i);
                yield return new WaitForSecondsRealtime(timelineInterval);
                if (captureTimeline) { Capture(); yield return null; }
            }
            timeline = null;
        }
        public void StopTimeline() { if (timeline != null) StopCoroutine(timeline); timeline = null; }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current; if (k == null) return;
            if (k.digit1Key.wasPressedThisFrame || k.digit2Key.wasPressedThisFrame || k.digit3Key.wasPressedThisFrame || k.digit4Key.wasPressedThisFrame || k.digit5Key.wasPressedThisFrame || k.digit6Key.wasPressedThisFrame || k.digit7Key.wasPressedThisFrame) StopTimeline();
            if (k.digit1Key.wasPressedThisFrame) ApplyState(BreachState.Sealed);
            if (k.digit2Key.wasPressedThisFrame) ApplyState(BreachState.SmallBreach);
            if (k.digit3Key.wasPressedThisFrame) ApplyState(BreachState.LargeBreach);
            if (k.digit4Key.wasPressedThisFrame) ApplyState(BreachState.ConnectedRooms);
            if (k.digit5Key.wasPressedThisFrame) ApplyState(BreachState.SmallRoofBreach);
            if (k.digit6Key.wasPressedThisFrame) ApplyState(BreachState.LargeRoofBreach);
            if (k.digit7Key.wasPressedThisFrame) ApplyState(BreachState.Restored);
            if (k.rKey.wasPressedThisFrame) ResetFixture();
            if (k.vKey.wasPressedThisFrame) NextView();
            if (k.lKey.wasPressedThisFrame) SetPreview(!preview);
            if (k.eKey.wasPressedThisFrame) environment.Next();
            if (k.cKey.wasPressedThisFrame) CycleRoof();
            if (k.bKey.wasPressedThisFrame) ToggleShutter();
            if (k.iKey.wasPressedThisFrame) environment.ToggleInteriorLights();
            if (k.tKey.wasPressedThisFrame) ToggleTimeline();
            if (k.pKey.wasPressedThisFrame) Capture();
#elif ENABLE_LEGACY_INPUT_MANAGER
            for (int i = 0; i < 7; i++) if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i))) { StopTimeline(); ApplyState((BreachState)i); }
            if (Input.GetKeyDown(KeyCode.R)) ResetFixture(); if (Input.GetKeyDown(KeyCode.V)) NextView();
            if (Input.GetKeyDown(KeyCode.L)) SetPreview(!preview); if (Input.GetKeyDown(KeyCode.E)) environment.Next();
            if (Input.GetKeyDown(KeyCode.C)) CycleRoof(); if (Input.GetKeyDown(KeyCode.I)) environment.ToggleInteriorLights();
            if (Input.GetKeyDown(KeyCode.B)) ToggleShutter();
            if (Input.GetKeyDown(KeyCode.T)) ToggleTimeline(); if (Input.GetKeyDown(KeyCode.P)) Capture();
#endif
        }

        public void Capture() { if (Application.isPlaying) StartCoroutine(CaptureFrame()); }
        private IEnumerator CaptureFrame()
        {
            yield return new WaitForEndOfFrame();
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../Captures")); Directory.CreateDirectory(folder);
            string stem = "gi-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-step" + ((int)state + 1) + "-r" + GeometryChanges.Revision;
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, stem + ".png"));
            var data = new CaptureData
            {
                utc = DateTime.UtcNow.ToString("O"), scene = gameObject.scene.name, state = state.ToString(),
                geometryRevision = GeometryChanges.Revision, lightingRevision = environment.Revision,
                lighting = environment.lighting.ToString(), skyIntensity = environment.EffectiveSkyIntensity,
                previewFill = preview, view = viewNames[View], cameraPosition = mainCamera.transform.position,
                roofOpening = RoofOpening, interiorLightsEnabled = environment.interiorLightsEnabled,
                preset = (int)state + 1, shutterClosed = ShutterClosed,
                cameraRotation = mainCamera.transform.eulerAngles, fieldOfView = mainCamera.fieldOfView,
                width = Screen.width, height = Screen.height, frame = Time.frameCount,
                changeFrame = LastChangeFrame, framesSinceChange = Time.frameCount - LastChangeFrame,
                secondsSinceChange = Time.realtimeSinceStartupAsDouble - LastChangeTime,
                unity = Application.unityVersion, device = SystemInfo.graphicsDeviceName,
                giEnabled = global::DynamicGI.DynamicGIController.Instance && global::DynamicGI.DynamicGIController.Instance.GIEnabled,
                giIndirectOnly = global::DynamicGI.DynamicGIController.Instance && global::DynamicGI.DynamicGIController.Instance.IndirectOnly,
                giSamples = global::DynamicGI.DynamicGIController.Instance ? global::DynamicGI.DynamicGIController.Instance.SampleIndex : 0,
                note = "Dynamic GI capture. See IMPLEMENTATION.md and Reports/DynamicGI for measured comparisons; this capture is not an accuracy score."
            };
            File.WriteAllText(Path.Combine(folder, stem + ".json"), JsonUtility.ToJson(data, true));
            message = "Saved " + stem;
        }
        [Serializable] private sealed class CaptureData
        {
            public string utc, scene, state, lighting, view, unity, device, note;
            public int geometryRevision, lightingRevision, width, height, frame, changeFrame, framesSinceChange, roofOpening, preset;
            public float skyIntensity, fieldOfView;
            public double secondsSinceChange;
            public bool previewFill, interiorLightsEnabled, shutterClosed;
            public bool giEnabled, giIndirectOnly;
            public int giSamples;
            public Vector3 cameraPosition, cameraRotation;
        }

        public bool SunVisibleAt(string name)
        {
            if (!environment.sun || !environment.sun.isActiveAndEnabled) return false;
            foreach (var sample in receivers)
                if (sample.name == name)
                {
                    var direction = -environment.sun.transform.forward;
                    return Vector3.Dot(sample.normal, direction) > 0 && !Physics.Raycast(sample.point.position + sample.normal * .02f, direction, 100, ~0, QueryTriggerInteraction.Ignore);
                }
            return false;
        }
        private void Marker(Vector3 world, string label)
        {
            var point = mainCamera.WorldToScreenPoint(world); if (point.z < mainCamera.nearClipPlane) return;
            float x = Mathf.Clamp(point.x - 95, 350, Screen.width - 200), y = Mathf.Clamp(Screen.height - point.y, 18, Screen.height - 40);
            GUI.Box(new Rect(x, y, 190, 26), label, markerStyle);
        }
        private void OnGUI()
        {
            if (textStyle == null)
            {
                textStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
                markerStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }
            Marker(new Vector3(-3.8f, 1.65f, .2f), "OUTER WALL: " + new[]{"CLOSED", "SMALL HOLE", "LARGE HOLE"}[ExteriorOpening]);
            Marker(new Vector3(-.14f, 1.65f, 5.1f), "INNER WALL: " + (InteriorOpen ? "OPEN" : "CLOSED"));
            Marker(new Vector3(-3.45f, 3.6f, 4.15f), "CEILING: " + new[]{"INTACT", "SMALL HOLE", "LARGE HOLES"}[RoofOpening]);
            GUILayout.BeginArea(new Rect(14, 14, 325, Mathf.Min(Screen.height - 28, 740)), GUI.skin.box);
            panelScroll = GUILayout.BeginScrollView(panelScroll);
            GUILayout.Label("GI LAB / 7 STEPS", new GUIStyle(textStyle) { fontSize = 20, fontStyle = FontStyle.Bold });
            GUILayout.Label("DYNAMIC GI | " + (preview ? "PREVIEW FILL ON" : "EVALUATION"), textStyle);
            GUILayout.Label("Outdoor " + environment.lighting + " | Dim lamps " + (environment.interiorLightsEnabled ? "ON" : "OFF"), textStyle);
            GUILayout.Label("Same camera: " + viewNames[View], textStyle);
            GUILayout.Label(Descriptions[(int)state], new GUIStyle(textStyle) { fontSize = 14 });
            GUILayout.Space(4);
            for (int i = 0; i < 7; i++)
                if (GUILayout.Button((i + 1) + "  |  " + StateLabels[i] + (i == (int)state ? "  <" : ""), GUILayout.Height(30))) { StopTimeline(); ApplyState((BreachState)i); }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("R Reset", GUILayout.Height(30))) ResetFixture();
            if (GUILayout.Button("V Camera", GUILayout.Height(30))) NextView();
            if (GUILayout.Button(TimelineRunning ? "T Stop" : "T Run all", GUILayout.Height(30))) ToggleTimeline();
            GUILayout.EndHorizontal();
            bool enabled = GUI.enabled; GUI.enabled = enabled && CanCompareWallDaylight;
            if (GUILayout.Button("B  |  Wall daylight: " + (ShutterClosed ? "COVERED" : "OPEN") + "  (compare)", GUILayout.Height(30))) ToggleShutter();
            GUI.enabled = enabled;
            GUILayout.Label("Sun paths: wall " + (SunVisibleAt("Direct_Control") ? "OPEN" : "BLOCKED") + " | ceiling " + (SunVisibleAt("Roof_Direct_Control") ? "OPEN" : "BLOCKED"), textStyle);
            GUILayout.Label("These paths are geometry checks, not a GI brightness score.", textStyle);
            GUILayout.Label("B compares the same opening, lights and camera with / without the outside blocker. GI should update the bounced light in shaded areas.", textStyle);
            if (GUILayout.Button(showOptions ? "Hide lighting / capture options" : "Lighting / capture options", GUILayout.Height(28))) showOptions = !showOptions;
            if (showOptions)
            {
                if (GUILayout.Button("I  |  Dim lamps: " + (environment.interiorLightsEnabled ? "ON" : "OFF"), GUILayout.Height(28))) environment.ToggleInteriorLights();
                if (GUILayout.Button("E  |  Outdoor: " + environment.lighting, GUILayout.Height(28))) environment.Next();
                if (GUILayout.Button("L  |  Preview fill: " + (preview ? "ON" : "OFF"), GUILayout.Height(28))) SetPreview(!preview);
                if (GUILayout.Button("P  |  Capture screenshot", GUILayout.Height(28))) Capture();
            }
            if (!string.IsNullOrEmpty(message)) GUILayout.Label(message, textStyle);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
        private void OnDestroy()
        {
            Application.runInBackground = previousRunInBackground;
            if (changedEditorLimit) { Application.targetFrameRate = previousFrameLimit; QualitySettings.vSyncCount = previousVSync; }
        }
    }
}
