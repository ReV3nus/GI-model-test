using System;
using System.Collections.Generic;
using System.Diagnostics;
using GIEvaluation;
using UnityEngine;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DynamicGI
{
    public enum GIViewMode
    {
        Composite = 0,
        AllGI = 1,
        BouncesOnly = 2,
        FalseColor = 3,
    }

    /// <summary>
    /// Runtime owner of the voxel radiance volume. It voxelizes the current opaque
    /// triangle geometry, injects occluded sun/sky/lamp direct light, and iterates
    /// diffuse bounces. Rendering is done by DynamicGIRendererFeature.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class DynamicGIController : MonoBehaviour
    {
        [Header("Voxel volume (world space)")]
        public Vector3 volumeCenter = new Vector3(0f, 2.0f, 1.5f);
        public Vector3 volumeSize = new Vector3(15.6f, 5.6f, 18.6f);
        [Range(0.04f, 0.30f)] public float voxelSize = 0.10f;

        [Header("Diffuse solver")]
        [Range(1, 8)] public int bounceIterations = 5;
        [Range(1, 4)] public int bouncesPerFrame = 2;
        [Range(4, 24)] public int propagationDirections = 8;
        [Range(4, 32)] public int skyDirections = 16;
        [Min(1f)] public float maxTraceDistance = 14f;
        [Min(0f)] public float indirectIntensity = 1f;
        [Min(0f)] public float skyIntensityScale = 1f;
        public bool includeInteriorLamps = true;

        [Header("Display")]
        public bool enableGI = true;
        public GIViewMode viewMode = GIViewMode.Composite;
        public bool showOverlay = true;
        public bool screenGateEnabled = true;
        private Vector3 m_CameraPositionWS;

        public static DynamicGIController Active { get; private set; }

        public bool IsReady => m_Ready;
        public bool IsConverged => m_Ready && m_BouncePass >= bounceIterations && !m_GeometryDirty && !m_LightingDirty;
        public int BouncePass => m_BouncePass;
        public int VolumeDimX => m_Dims.x;
        public int VolumeDimY => m_Dims.y;
        public int VolumeDimZ => m_Dims.z;
        public Vector3 ActualVoxelSize => m_VoxelSize;
        public long VolumeMemoryBytes => m_VolumeMemoryBytes;
        public long TriangleBufferBytes => m_TriangleBufferBytes;
        public int TriangleCount => m_TriangleCount;
        public int LastGeometryRevision { get; private set; }
        public double LastRebuildCpuMs { get; private set; }
        public double LastConvergeGpuMs { get; private set; }
        public int LastRebuildFrame { get; private set; }
        public int LastConvergedFrame { get; private set; }
        public double LastRebuildTime { get; private set; }
        public double LastConvergedTime { get; private set; }
        public int SkippedUnreadableMeshes { get; private set; }

        private struct GpuTriangle
        {
            public Vector4 p0, p1, p2, n, albedo, bmin, bmax, pad;
        }

        private static readonly int k_Albedo = Shader.PropertyToID("_VoxelAlbedo");
        private static readonly int k_Normal = Shader.PropertyToID("_VoxelNormal");
        private static readonly int k_SourceTex = Shader.PropertyToID("_SourceTex");
        private static readonly int k_IndirectRead = Shader.PropertyToID("_IndirectRead");
        private static readonly int k_IndirectWrite = Shader.PropertyToID("_IndirectWrite");
        private static readonly int k_SkyRead = Shader.PropertyToID("_SkyRead");
        private static readonly int k_SkyWrite = Shader.PropertyToID("_SkyWrite");
        private static readonly int k_SourceRW = Shader.PropertyToID("_SourceRW");
        private static readonly int k_SkyERW = Shader.PropertyToID("_SkyERW");
        private static readonly int k_IndirectClearRW = Shader.PropertyToID("_IndirectClearRW");
        private static readonly int k_Triangles = Shader.PropertyToID("_Triangles");
        private static readonly int k_AlbedoRW = Shader.PropertyToID("_AlbedoRW");
        private static readonly int k_NormalRW = Shader.PropertyToID("_NormalRW");
        private static readonly int k_ClosenessRW = Shader.PropertyToID("_ClosenessRW");
        private static readonly int k_OccluderRW = Shader.PropertyToID("_OccluderRW");
        private static readonly int k_OccluderTex = Shader.PropertyToID("_OccluderTex");
        private static readonly int k_OccluderBuffer = Shader.PropertyToID("_OccluderBuffer");
        private static readonly int k_OutsideMask = Shader.PropertyToID("_OutsideMask");
        private static readonly int k_SkyCube = Shader.PropertyToID("_SkyCube");

        private ComputeShader m_VoxelizeCS;
        private ComputeShader m_InjectCS;
        private ComputeShader m_PropagateCS;
        private int m_KClear, m_KSplat, m_KWrite, m_KResolve, m_KDilate;
        private int m_KInject, m_KClearIndirect;
        private int m_KPropagate, m_KBlurIndirect, m_KBlurSky;
        private bool m_ShadersOk;

        private RenderTexture m_Albedo;
        private RenderTexture m_Normal;
        private RenderTexture m_Source;
        private RenderTexture m_SkyRaw;
        private RenderTexture m_SkyBlur;
        private RenderTexture m_IndirectA;
        private RenderTexture m_IndirectB;
        private RenderTexture m_Closeness;
        private RenderTexture m_Occluder;
        private ComputeBuffer m_OccluderBuffer;
        private uint[] m_BlockedUInt;
        private RenderTexture m_IndirectCurrent;
        private RenderTexture m_IndirectRaw;

        private ComputeBuffer m_Triangles;
        private readonly List<GpuTriangle> m_TriangleList = new List<GpuTriangle>(4096);
        private readonly Dictionary<Material, Vector3> m_MaterialAlbedoCache = new Dictionary<Material, Vector3>();
        private readonly Dictionary<Texture, float> m_TextureAlbedoCache = new Dictionary<Texture, float>();

        private Vector3Int m_Dims;
        private Vector3 m_VoxelSize;
        private Vector3 m_VolumeMin;
        private long m_VolumeMemoryBytes;
        private long m_TriangleBufferBytes;
        private int m_TriangleCount;
        private bool m_Ready;
        private bool m_GeometryDirty = true;
        private bool m_LightingDirty = true;
        private int m_BouncePass;
        private double m_ChangeTime;
        private int m_ChangeFrame;
        private bool m_ReadbackPending;
        private double m_ReadbackStart;

        private GIBreachScenario m_Scenario;
        private GIBreachEnvironment m_Environment;
        private readonly Vector4[] m_LampPositions = new Vector4[8];
        private readonly Vector4[] m_LampColors = new Vector4[8];
        private readonly Vector4[] m_LampParams = new Vector4[8];

        private GUIStyle m_Style;

        private void Awake()
        {
            Active = this;
            m_VoxelizeCS = Resources.Load<ComputeShader>("DynamicGIVoxelize");
            m_InjectCS = Resources.Load<ComputeShader>("DynamicGIInject");
            m_PropagateCS = Resources.Load<ComputeShader>("DynamicGIPropagate");
            m_ShadersOk = m_VoxelizeCS && m_InjectCS && m_PropagateCS;
            if (!m_ShadersOk)
            {
                UnityEngine.Debug.LogError("[Dynamic GI] Missing compute shader resources. Expected DynamicGIVoxelize/Inject/Propagate in a Resources folder.");
                return;
            }
            try
            {
                m_KClear = m_VoxelizeCS.FindKernel("ClearVolume");
                m_KSplat = m_VoxelizeCS.FindKernel("SplatTriangles");
                m_KWrite = m_VoxelizeCS.FindKernel("WriteTriangles");
                m_KResolve = m_VoxelizeCS.FindKernel("ResolveVolume");
                m_KDilate = m_VoxelizeCS.FindKernel("DilateOccupancy");
                m_KInject = m_InjectCS.FindKernel("InjectDirect");
                m_KClearIndirect = m_InjectCS.FindKernel("ClearIndirect");
                m_KPropagate = m_PropagateCS.FindKernel("Propagate");
                m_KBlurIndirect = m_PropagateCS.FindKernel("BlurIndirect");
                m_KBlurSky = m_PropagateCS.FindKernel("BlurSky");
            }
            catch (Exception e)
            {
                m_ShadersOk = false;
                UnityEngine.Debug.LogError("[Dynamic GI] Compute kernel lookup failed: " + e.Message);
                return;
            }

            m_Scenario = FindAnyObjectByType<GIBreachScenario>();
            m_Environment = m_Scenario ? m_Scenario.environment : FindAnyObjectByType<GIBreachEnvironment>();
        }

        private void OnEnable()
        {
            GeometryChanges.Changed += OnGeometryChanged;
            if (!m_Environment) m_Environment = FindAnyObjectByType<GIBreachEnvironment>();
            if (m_Environment) m_Environment.Changed += OnEnvironmentChanged;
            m_GeometryDirty = true;
            m_LightingDirty = true;
        }

        private void OnDisable()
        {
            GeometryChanges.Changed -= OnGeometryChanged;
            if (m_Environment) m_Environment.Changed -= OnEnvironmentChanged;
            ReleaseResources();
            if (Active == this) Active = null;
        }

        private void OnDestroy()
        {
            ReleaseResources();
            if (Active == this) Active = null;
        }

        private void OnGeometryChanged(GeometryChange change)
        {
            m_GeometryDirty = true;
            m_ChangeTime = Time.realtimeSinceStartupAsDouble;
            m_ChangeFrame = Time.frameCount;
        }

        private void OnEnvironmentChanged(GIBreachEnvironment environment)
        {
            m_LightingDirty = true;
            m_ChangeTime = Time.realtimeSinceStartupAsDouble;
            m_ChangeFrame = Time.frameCount;
        }

        public void SetGIEnabled(bool value)
        {
            if (enableGI == value) return;
            enableGI = value;
            if (value && !m_Ready) m_GeometryDirty = true;
            if (value) m_LightingDirty = true;
        }

        public void ToggleGI() => SetGIEnabled(!enableGI);

        public void CycleViewMode()
        {
            viewMode = (GIViewMode)(((int)viewMode + 1) % 4);
        }

        public void RequestRefresh()
        {
            m_GeometryDirty = true;
            m_LightingDirty = true;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return;
            if (k.gKey.wasPressedThisFrame) ToggleGI();
            if (k.oKey.wasPressedThisFrame) CycleViewMode();
            if (k.hKey.wasPressedThisFrame) showOverlay = !showOverlay;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.G)) ToggleGI();
            if (Input.GetKeyDown(KeyCode.O)) CycleViewMode();
            if (Input.GetKeyDown(KeyCode.H)) showOverlay = !showOverlay;
#endif
        }

        private void LateUpdate()
        {
            if (!m_ShadersOk) return;

            if (!m_Environment)
            {
                m_Environment = FindAnyObjectByType<GIBreachEnvironment>();
                if (m_Environment) m_Environment.Changed += OnEnvironmentChanged;
            }
            if (!m_Scenario) m_Scenario = FindAnyObjectByType<GIBreachScenario>();

            if (!enableGI)
            {
                // Keep the last converged data. Dirty flags stay set so the next
                // enable rebuilds geometry or re-injects lighting if needed.
                return;
            }

            EnsureVolumes();
            if (!m_Ready) return;

            if (m_GeometryDirty)
            {
                RebuildGeometryAndLighting();
                m_GeometryDirty = false;
                m_LightingDirty = false;
            }
            else if (m_LightingDirty)
            {
                InjectLighting();
                m_LightingDirty = false;
            }
            else if (m_BouncePass < bounceIterations)
            {
                StepBounces(Mathf.Min(bouncesPerFrame, bounceIterations - m_BouncePass));
                if (m_BouncePass >= bounceIterations)
                    RequestConvergenceReadback();
            }
        }

        private void EnsureVolumes()
        {
            int dx = Mathf.Clamp(Mathf.CeilToInt(volumeSize.x / voxelSize), 8, 256);
            int dy = Mathf.Clamp(Mathf.CeilToInt(volumeSize.y / voxelSize), 8, 256);
            int dz = Mathf.Clamp(Mathf.CeilToInt(volumeSize.z / voxelSize), 8, 256);
            m_Dims = new Vector3Int(dx, dy, dz);
            m_VoxelSize = new Vector3(volumeSize.x / dx, volumeSize.y / dy, volumeSize.z / dz);
            m_VolumeMin = volumeCenter - volumeSize * 0.5f;

            if (m_Ready && m_Albedo && m_Albedo.width == dx && m_Albedo.height == dy && m_Albedo.volumeDepth == dz)
                return;

            ReleaseResources();

            m_Albedo = CreateVolume("GI_VoxelAlbedo", dx, dy, dz, RenderTextureFormat.ARGBHalf, FilterMode.Point);
            m_Normal = CreateVolume("GI_VoxelNormal", dx, dy, dz, RenderTextureFormat.ARGBHalf, FilterMode.Point);
            m_Source = CreateVolume("GI_VoxelSource", dx, dy, dz, RenderTextureFormat.ARGBHalf, FilterMode.Point);
            m_SkyRaw = CreateVolume("GI_VoxelSky", dx, dy, dz, RenderTextureFormat.ARGBHalf, FilterMode.Point);
            m_SkyBlur = CreateVolume("GI_VoxelSkyBlur", dx, dy, dz, RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
            m_IndirectA = CreateVolume("GI_IndirectA", dx, dy, dz, RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
            m_IndirectB = CreateVolume("GI_IndirectB", dx, dy, dz, RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
            m_Closeness = CreateVolume("GI_Closeness", dx, dy, dz, RenderTextureFormat.RInt, FilterMode.Point);
            m_Occluder = CreateVolume("GI_Occluder", dx, dy, dz, RenderTextureFormat.ARGB32, FilterMode.Point);
            if (!m_Albedo || !m_Normal || !m_Source || !m_SkyRaw || !m_SkyBlur || !m_IndirectA || !m_IndirectB || !m_Closeness || !m_Occluder)
            {
                UnityEngine.Debug.LogError("[Dynamic GI] Failed to create one or more 3D render textures (Format/3D RandomWrite unsupported?).");
                ReleaseResources();
                return;
            }

            m_VolumeMemoryBytes = (long)dx * dy * dz * (8L * 7L + 4L + 4L) + (long)dx * dy * dz * 4L;
            if (m_BlockedUInt == null || m_BlockedUInt.Length != dx * dy * dz)
                m_BlockedUInt = new uint[dx * dy * dz];
            if (m_OccluderBuffer == null || m_OccluderBuffer.count != dx * dy * dz)
            {
                if (m_OccluderBuffer != null) m_OccluderBuffer.Release();
                m_OccluderBuffer = new ComputeBuffer(dx * dy * dz, 4, ComputeBufferType.Structured);
            }
            m_Ready = true;
            m_BouncePass = 0;
            m_GeometryDirty = true;
            ApplyVolumeUniforms();
        }

        private RenderTexture CreateVolume(string name, int dx, int dy, int dz, RenderTextureFormat format, FilterMode filter)
        {
            if (format == RenderTextureFormat.RInt && !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RInt))
            {
                UnityEngine.Debug.LogError("[Dynamic GI] RInt render textures are not supported on this graphics device.");
                return null;
            }
            var rt = new RenderTexture(dx, dy, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                dimension = TextureDimension.Tex3D,
                volumeDepth = dz,
                enableRandomWrite = true,
                useMipMap = false,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };
            if (!rt.Create())
            {
                Destroy(rt);
                return null;
            }
            return rt;
        }

        private void ReleaseResources()
        {
            ReleaseVolume(ref m_Albedo);
            ReleaseVolume(ref m_Normal);
            ReleaseVolume(ref m_Source);
            ReleaseVolume(ref m_SkyRaw);
            ReleaseVolume(ref m_SkyBlur);
            ReleaseVolume(ref m_IndirectA);
            ReleaseVolume(ref m_IndirectB);
            ReleaseVolume(ref m_Closeness);
            ReleaseVolume(ref m_Occluder);
            if (m_OccluderBuffer != null) { m_OccluderBuffer.Release(); m_OccluderBuffer = null; }
            m_BlockedUInt = null;
            m_IndirectCurrent = null;
            m_IndirectRaw = null;
            if (m_Triangles != null) { m_Triangles.Release(); m_Triangles = null; }
            m_TriangleBufferBytes = 0;
            m_Ready = false;
            m_BouncePass = 0;
            ReleaseConnectivity();
        }

        private static void ReleaseVolume(ref RenderTexture rt)
        {
            if (!rt) return;
            rt.Release();
            if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
            rt = null;
        }

        private void ApplyVolumeUniforms()
        {
            SetVolumeUniforms(m_VoxelizeCS);
            SetVolumeUniforms(m_InjectCS);
            SetVolumeUniforms(m_PropagateCS);
        }

        private void SetVolumeUniforms(ComputeShader cs)
        {
            if (!cs) return;
            cs.SetVector("_VolumeMin", m_VolumeMin);
            cs.SetVector("_VolumeVoxelSize", m_VoxelSize);
            cs.SetInts("_VolumeDims", m_Dims.x, m_Dims.y, m_Dims.z);
        }

        private bool BoundsOverlapVolume(Bounds b)
        {
            var min = m_VolumeMin;
            var max = m_VolumeMin + volumeSize;
            return b.max.x >= min.x && b.min.x <= max.x &&
                   b.max.y >= min.y && b.min.y <= max.y &&
                   b.max.z >= min.z && b.min.z <= max.z;
        }

        private void RebuildGeometryAndLighting()
        {
            var sw = Stopwatch.StartNew();
            LastRebuildFrame = Time.frameCount;
            LastRebuildTime = Time.realtimeSinceStartupAsDouble;
            LastGeometryRevision = GeometryChanges.Revision;

            CollectTriangles();
            UploadTriangles();

            int groupsX = (m_Dims.x + 3) / 4;
            int groupsY = (m_Dims.y + 3) / 4;
            int groupsZ = (m_Dims.z + 3) / 4;

            BindVoxelizeTextures();
            m_VoxelizeCS.SetInt("_TriangleCount", m_TriangleCount);
            m_VoxelizeCS.SetFloat("_SplatRadius", 0.87f * Mathf.Max(m_VoxelSize.x, Mathf.Max(m_VoxelSize.y, m_VoxelSize.z)));
            m_VoxelizeCS.Dispatch(m_KClear, groupsX, groupsY, groupsZ);
            if (m_TriangleCount > 0)
            {
                int triGroups = (m_TriangleCount + 63) / 64;
                m_VoxelizeCS.Dispatch(m_KSplat, triGroups, 1, 1);
                m_VoxelizeCS.Dispatch(m_KWrite, triGroups, 1, 1);
            }
            m_VoxelizeCS.Dispatch(m_KResolve, groupsX, groupsY, groupsZ);
            m_VoxelizeCS.SetTexture(m_KDilate, k_AlbedoRW, m_Albedo);
            m_VoxelizeCS.SetTexture(m_KDilate, k_OccluderRW, m_Occluder);
            if (m_OccluderBuffer != null) m_VoxelizeCS.SetBuffer(m_KDilate, k_OccluderBuffer, m_OccluderBuffer);
            m_VoxelizeCS.Dispatch(m_KDilate, groupsX, groupsY, groupsZ);
            BuildOutsideMask();

            InjectLightingInternal();

            m_BouncePass = 0;
            m_ChangeTime = Time.realtimeSinceStartupAsDouble;
            m_ChangeFrame = Time.frameCount;
            sw.Stop();
            LastRebuildCpuMs = sw.Elapsed.TotalMilliseconds;
        }

        private void InjectLighting()
        {
            var sw = Stopwatch.StartNew();
            InjectLightingInternal();
            m_BouncePass = 0;
            sw.Stop();
            LastRebuildCpuMs = sw.Elapsed.TotalMilliseconds;
        }

        private void InjectLightingInternal()
        {
            ApplyVolumeUniforms();
            UpdateLightingUniforms();

            int groupsX = (m_Dims.x + 3) / 4;
            int groupsY = (m_Dims.y + 3) / 4;
            int groupsZ = (m_Dims.z + 3) / 4;

            m_InjectCS.SetTexture(m_KInject, k_Albedo, m_Albedo);
            m_InjectCS.SetTexture(m_KInject, k_Normal, m_Normal);
            m_InjectCS.SetTexture(m_KInject, k_OccluderTex, m_Occluder);
            if (m_OutsideMaskTex) m_InjectCS.SetTexture(m_KInject, k_OutsideMask, m_OutsideMaskTex);
            m_InjectCS.SetTexture(m_KInject, k_SourceRW, m_Source);
            m_InjectCS.SetTexture(m_KInject, k_SkyERW, m_SkyRaw);
            if (m_Environment && m_Environment.skyRadiance)
                m_InjectCS.SetTexture(m_KInject, k_SkyCube, m_Environment.skyRadiance);
            m_InjectCS.Dispatch(m_KInject, groupsX, groupsY, groupsZ);

            // One smoothing pass over the sky visibility field.
            m_PropagateCS.SetTexture(m_KBlurSky, k_Albedo, m_Albedo);
            m_PropagateCS.SetTexture(m_KBlurSky, k_SkyRead, m_SkyRaw);
            m_PropagateCS.SetTexture(m_KBlurSky, k_SkyWrite, m_SkyBlur);
            m_PropagateCS.Dispatch(m_KBlurSky, groupsX, groupsY, groupsZ);

            // Reset the bounce chain to E0 = 0.
            m_InjectCS.SetTexture(m_KClearIndirect, k_Albedo, m_Albedo);
            m_InjectCS.SetTexture(m_KClearIndirect, k_IndirectClearRW, m_IndirectA);
            m_InjectCS.Dispatch(m_KClearIndirect, groupsX, groupsY, groupsZ);

            m_IndirectCurrent = m_IndirectA;
            m_IndirectRaw = m_IndirectB;
            m_BouncePass = 0;
        }

        private void StepBounces(int steps)
        {
            if (!m_IndirectCurrent || !m_IndirectRaw) return;
            int groupsX = (m_Dims.x + 3) / 4;
            int groupsY = (m_Dims.y + 3) / 4;
            int groupsZ = (m_Dims.z + 3) / 4;
            float maxVoxel = Mathf.Max(m_VoxelSize.x, Mathf.Max(m_VoxelSize.y, m_VoxelSize.z));
            float rayStart = 1.0f * maxVoxel;

            for (int i = 0; i < steps && m_BouncePass < bounceIterations; i++)
            {
                m_PropagateCS.SetTexture(m_KPropagate, k_Albedo, m_Albedo);
                m_PropagateCS.SetTexture(m_KPropagate, k_Normal, m_Normal);
                m_PropagateCS.SetTexture(m_KPropagate, k_SourceTex, m_Source);
                m_PropagateCS.SetTexture(m_KPropagate, k_IndirectRead, m_IndirectCurrent);
                m_PropagateCS.SetTexture(m_KPropagate, k_IndirectWrite, m_IndirectRaw);
                m_PropagateCS.SetInt("_DirectionCount", Mathf.Max(1, propagationDirections));
                m_PropagateCS.SetFloat("_RayStart", rayStart);
                m_PropagateCS.SetFloat("_MaxTraceDistance", maxTraceDistance);
                m_PropagateCS.Dispatch(m_KPropagate, groupsX, groupsY, groupsZ);

                m_PropagateCS.SetTexture(m_KBlurIndirect, k_Albedo, m_Albedo);
                m_PropagateCS.SetTexture(m_KBlurIndirect, k_IndirectRead, m_IndirectRaw);
                m_PropagateCS.SetTexture(m_KBlurIndirect, k_IndirectWrite, m_IndirectCurrent);
                m_PropagateCS.Dispatch(m_KBlurIndirect, groupsX, groupsY, groupsZ);

                // Second smoothing pass (effective radius ~2 voxels).
                m_PropagateCS.SetTexture(m_KBlurIndirect, k_Albedo, m_Albedo);
                m_PropagateCS.SetTexture(m_KBlurIndirect, k_IndirectRead, m_IndirectCurrent);
                m_PropagateCS.SetTexture(m_KBlurIndirect, k_IndirectWrite, m_IndirectRaw);
                m_PropagateCS.Dispatch(m_KBlurIndirect, groupsX, groupsY, groupsZ);

                var swap = m_IndirectCurrent;
                m_IndirectCurrent = m_IndirectRaw;
                m_IndirectRaw = swap;

                m_BouncePass++;
            }
        }

        private void RequestConvergenceReadback()
        {
            if (m_ReadbackPending || !m_IndirectCurrent) return;
            m_ReadbackPending = true;
            m_ReadbackStart = Time.realtimeSinceStartupAsDouble;
            AsyncGPUReadback.Request(m_IndirectCurrent, 0, 0, 1, 0, 1, 0, 1, OnConvergenceReadback);
        }

        private void OnConvergenceReadback(AsyncGPUReadbackRequest request)
        {
            m_ReadbackPending = false;
            LastConvergedFrame = Time.frameCount;
            LastConvergedTime = Time.realtimeSinceStartupAsDouble;
            LastConvergeGpuMs = (LastConvergedTime - m_ReadbackStart) * 1000.0;
        }

        private void UpdateLightingUniforms()
        {
            if (!m_InjectCS) return;
            var env = m_Environment;
            bool sunEnabled = false;
            Vector3 sunDirection = Vector3.up;
            Vector3 sunColor = Vector3.zero;
            if (env && env.sun)
            {
                var sun = env.sun;
                bool modeAllowsSun = env.lighting != BreachLighting.SkyOnly;
                sunEnabled = modeAllowsSun && sun.enabled && sun.isActiveAndEnabled;
                sunDirection = (-sun.transform.forward).normalized;
                var c = sun.color.linear;
                sunColor = new Vector3(c.r, c.g, c.b) * Mathf.Max(0f, sun.intensity);
            }
            float skyScale = 0f;
            if (env)
                skyScale = env.EffectiveSkyIntensity * skyIntensityScale;
            bool skyEnabled = env && env.skyRadiance && skyScale > 0f;

            m_InjectCS.SetVector("_SunDirection", sunDirection);
            m_InjectCS.SetVector("_SunColor", sunColor);
            m_InjectCS.SetInt("_SunEnabled", sunEnabled ? 1 : 0);
            m_InjectCS.SetInt("_SkyEnabled", skyEnabled ? 1 : 0);
            m_InjectCS.SetFloat("_SkyIntensity", skyScale);
            m_InjectCS.SetInt("_SkyDirectionCount", Mathf.Clamp(skyDirections, 1, 32));
            float maxVoxel = Mathf.Max(m_VoxelSize.x, Mathf.Max(m_VoxelSize.y, m_VoxelSize.z));
            float rayStart = 1.0f * maxVoxel;
            m_InjectCS.SetFloat("_RayStart", rayStart);
            m_InjectCS.SetFloat("_OcclusionRayStart", 1.55f * maxVoxel);
            m_InjectCS.SetFloat("_SunTraceDistance", 40f);
            m_InjectCS.SetFloat("_SkyTraceDistance", maxTraceDistance);

            int lampCount = 0;
            if (includeInteriorLamps && env && env.interiorLights != null && env.interiorLightsEnabled)
            {
                for (int i = 0; i < env.interiorLights.Length && lampCount < 8; i++)
                {
                    var light = env.interiorLights[i];
                    if (!light || !light.enabled || !light.isActiveAndEnabled) continue;
                    Vector3 p = light.transform.position;
                    var c = light.color.linear;
                    Vector3 col = new Vector3(c.r, c.g, c.b) * Mathf.Max(0f, light.intensity);
                    m_LampPositions[lampCount] = new Vector4(p.x, p.y, p.z, 0f);
                    m_LampColors[lampCount] = new Vector4(col.x, col.y, col.z, 0f);
                    m_LampParams[lampCount] = new Vector4(Mathf.Max(0.01f, light.range), 1f, 0f, 0f);
                    lampCount++;
                }
            }
            m_InjectCS.SetInt("_LampCount", lampCount);
            m_InjectCS.SetInt("_HasOutsideMask", m_OutsideMaskTex ? 1 : 0);
            m_ActiveLampCount = lampCount;
            if (lampCount > 0)
            {
                m_InjectCS.SetVectorArray("_LampPositions", m_LampPositions);
                m_InjectCS.SetVectorArray("_LampColors", m_LampColors);
                m_InjectCS.SetVectorArray("_LampParams", m_LampParams);
            }
        }

        private void BindVoxelizeTextures()
        {
            m_VoxelizeCS.SetTexture(m_KClear, k_AlbedoRW, m_Albedo);
            m_VoxelizeCS.SetTexture(m_KClear, k_NormalRW, m_Normal);
            m_VoxelizeCS.SetTexture(m_KClear, k_ClosenessRW, m_Closeness);
            m_VoxelizeCS.SetTexture(m_KSplat, k_ClosenessRW, m_Closeness);
            m_VoxelizeCS.SetTexture(m_KWrite, k_AlbedoRW, m_Albedo);
            m_VoxelizeCS.SetTexture(m_KWrite, k_NormalRW, m_Normal);
            m_VoxelizeCS.SetTexture(m_KWrite, k_ClosenessRW, m_Closeness);
            m_VoxelizeCS.SetTexture(m_KResolve, k_AlbedoRW, m_Albedo);
            m_VoxelizeCS.SetTexture(m_KResolve, k_NormalRW, m_Normal);
            m_VoxelizeCS.SetTexture(m_KResolve, k_ClosenessRW, m_Closeness);
            if (m_Triangles != null)
                m_VoxelizeCS.SetBuffer(m_KSplat, k_Triangles, m_Triangles);
            if (m_Triangles != null)
                m_VoxelizeCS.SetBuffer(m_KWrite, k_Triangles, m_Triangles);
        }

        private void CollectTriangles()
        {
            m_TriangleList.Clear();
            SkippedUnreadableMeshes = 0;
            var renderers = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var volumeBounds = new Bounds(volumeCenter, volumeSize);
            for (int r = 0; r < renderers.Length; r++)
            {
                var renderer = renderers[r];
                if (!renderer || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!renderer.TryGetComponent<MeshFilter>(out var filter)) continue;
                var mesh = filter.sharedMesh;
                if (!mesh) continue;
                if (!mesh.isReadable) { SkippedUnreadableMeshes++; continue; }
                if (!BoundsOverlapVolume(renderer.bounds)) continue;

                var materials = renderer.sharedMaterials;
                var matrix = renderer.transform.localToWorldMatrix;
                var vertices = mesh.vertices;
                int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                for (int s = 0; s < subMeshCount; s++)
                {
                    Material material = materials != null && materials.Length > 0 ? materials[Mathf.Min(s, materials.Length - 1)] : null;
                    if (material == null || material.shader == null) continue;
                    if (material.renderQueue >= 3000) continue;
                    Vector3 albedo = GetMaterialAlbedo(material);
                    if (albedo.sqrMagnitude < 1e-8f) albedo = new Vector3(0.5f, 0.5f, 0.5f);
                    int[] indices = mesh.GetTriangles(s);
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        Vector3 p0 = matrix.MultiplyPoint3x4(vertices[indices[i]]);
                        Vector3 p1 = matrix.MultiplyPoint3x4(vertices[indices[i + 1]]);
                        Vector3 p2 = matrix.MultiplyPoint3x4(vertices[indices[i + 2]]);
                        Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
                        if (n.sqrMagnitude < 1e-10f) continue;
                        n.Normalize();
                        var bmin = Vector3.Min(p0, Vector3.Min(p1, p2));
                        var bmax = Vector3.Max(p0, Vector3.Max(p1, p2));
                        var tb = new Bounds((bmin + bmax) * 0.5f, bmax - bmin);
                        if (!volumeBounds.Intersects(tb)) continue;
                        m_TriangleList.Add(new GpuTriangle
                        {
                            p0 = p0, p1 = p1, p2 = p2,
                            n = n,
                            albedo = albedo,
                            bmin = bmin, bmax = bmax,
                            pad = Vector4.zero,
                        });
                    }
                }
            }
        }

        private void UploadTriangles()
        {
            m_TriangleCount = m_TriangleList.Count;
            int needed = Mathf.Max(1, m_TriangleList.Count);
            if (m_Triangles == null || m_Triangles.count < needed)
            {
                if (m_Triangles != null) m_Triangles.Release();
                int capacity = Mathf.NextPowerOfTwo(needed);
                m_Triangles = new ComputeBuffer(capacity, 128, ComputeBufferType.Structured);
            }
            if (m_TriangleList.Count > 0)
                m_Triangles.SetData(m_TriangleList);
            m_TriangleBufferBytes = (long)m_Triangles.count * 128L;
        }

        private Vector3 GetMaterialAlbedo(Material material)
        {
            if (m_MaterialAlbedoCache.TryGetValue(material, out var cached)) return cached;

            Color c = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
            Vector3 albedo = new Vector3(c.r, c.g, c.b);
            if (material.HasProperty("_BaseMap"))
            {
                var texture = material.GetTexture("_BaseMap");
                if (texture)
                {
                    float average = AverageTextureLuminance(texture);
                    albedo *= average;
                }
            }
            albedo = new Vector3(
                Mathf.Clamp(albedo.x, 0.02f, 0.95f),
                Mathf.Clamp(albedo.y, 0.02f, 0.95f),
                Mathf.Clamp(albedo.z, 0.02f, 0.95f));
            m_MaterialAlbedoCache[material] = albedo;
            return albedo;
        }

        private float AverageTextureLuminance(Texture texture)
        {
            if (m_TextureAlbedoCache.TryGetValue(texture, out var cached)) return cached;

            const int N = 16;
            var rt = RenderTexture.GetTemporary(N, N, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            float result = 1f;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, N, N), 0, 0);
                tex.Apply(false, false);
                var pixels = tex.GetPixels();
                float sum = 0f;
                for (int i = 0; i < pixels.Length; i++)
                    sum += 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
                result = pixels.Length > 0 ? sum / pixels.Length : 1f;
                Destroy(tex);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Dynamic GI] Could not average base map '" + texture.name + "': " + e.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            result = Mathf.Clamp(result, 0.05f, 1f);
            m_TextureAlbedoCache[texture] = result;
            return result;
        }

        public void SetCameraPosition(Vector3 position)
        {
            m_CameraPositionWS = position;
        }

        public void ApplyToMaterial(Material material)
        {
            if (!material) return;
            bool valid = enableGI && m_Ready && m_IndirectCurrent && m_SkyBlur;
            material.SetFloat("_VolumeValid", valid ? 1f : 0f);
            if (!valid) return;
            material.SetTexture("_VolumeAlbedo", m_Albedo);
            material.SetTexture("_VolumeIndirect", m_IndirectCurrent);
            material.SetTexture("_VolumeSky", m_SkyBlur);
            material.SetVector("_VolumeMin", new Vector4(m_VolumeMin.x, m_VolumeMin.y, m_VolumeMin.z, 0f));
            material.SetVector("_VolumeVoxelSize", new Vector4(m_VoxelSize.x, m_VoxelSize.y, m_VoxelSize.z, 0f));
            material.SetVector("_VolumeDimsF", new Vector4(m_Dims.x, m_Dims.y, m_Dims.z, 0f));
            material.SetFloat("_IndirectIntensity", indirectIntensity);
            material.SetInteger("_ViewMode", (int)viewMode);
            if (m_OutsideMaskTex) material.SetTexture("_OutsideMask", m_OutsideMaskTex);
            material.SetFloat("_GateEnclosed", (screenGateEnabled && m_ActiveLampCount == 0 && m_OutsideMaskTex) ? 1f : 0f);
            material.SetVector("_CameraPositionWS", new Vector4(m_CameraPositionWS.x, m_CameraPositionWS.y, m_CameraPositionWS.z, 0f));
        }

        private void OnGUI()
        {
            if (!showOverlay) return;
            if (m_Style == null)
                m_Style = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false };

            float w = 340f;
            var rect = new Rect(Screen.width - w - 14f, 14f, w, 205f);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label("DYNAMIC GI  " + (enableGI ? "[ON]" : "[OFF]"), new GUIStyle(m_Style) { fontSize = 15, fontStyle = FontStyle.Bold });
            if (!m_ShadersOk)
            {
                GUILayout.Label("compute shaders missing", m_Style);
            }
            else if (!m_Ready)
            {
                GUILayout.Label("initialising volume...", m_Style);
            }
            else
            {
                GUILayout.Label("View: " + viewMode + "   bounces " + Mathf.Min(m_BouncePass, bounceIterations) + "/" + bounceIterations, m_Style);
                GUILayout.Label(string.Format("Volume {0}x{1}x{2} @ {3:F3} m  {4:F1} MB", m_Dims.x, m_Dims.y, m_Dims.z, m_VoxelSize.x, m_VolumeMemoryBytes / (1024f * 1024f)), m_Style);
                GUILayout.Label(string.Format("Tris {0} ({1:F1} KB)  GPU done {2:F1} ms", m_TriangleCount, m_TriangleBufferBytes / 1024f, LastConvergeGpuMs), m_Style);
                GUILayout.Label(string.Format("Rebuild CPU {0:F1} ms  rev {1}  converged {2}", LastRebuildCpuMs, LastGeometryRevision, IsConverged ? "yes" : "no"), m_Style);
            }
            if (GUILayout.Button("Toggle GI (G)", GUILayout.Height(24))) ToggleGI();
            if (GUILayout.Button("Debug view: " + viewMode + " (O)", GUILayout.Height(24))) CycleViewMode();
            GUILayout.Label("H hide this panel", m_Style);
            GUILayout.EndArea();
        }
    }

    /// <summary>
    /// Adds the controller automatically when the GI test scene is played, so the
    /// existing scene does not have to be rebuilt by hand.
    /// </summary>
    public static class DynamicGIBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            var scenario = UnityEngine.Object.FindAnyObjectByType<GIBreachScenario>();
            if (!scenario) return;
            var existing = UnityEngine.Object.FindAnyObjectByType<DynamicGIController>();
            if (existing) return;
            scenario.gameObject.AddComponent<DynamicGIController>();
        }
    }
}
