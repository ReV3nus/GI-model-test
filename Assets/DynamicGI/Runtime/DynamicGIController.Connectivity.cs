using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI
{
    public sealed partial class DynamicGIController
    {
        // Connectivity data. The GPU builds a conservative occluder (surface
        // shell + one voxel dilation). It is read back, and a CPU flood fill from
        // the volume boundary marks the air that is connected to the outdoors.
        // Surface voxels whose adjacent air is not connected cannot receive sun
        // or sky light, which removes residual voxel-pinhole leakage in sealed
        // rooms. The flood fill is only run when geometry changes.
        private byte[] m_BlockedCpu;
        private byte[] m_OutsideCpu;
        private int[] m_BfsQueue;
        private byte[] m_OccluderReadback;
        private Texture3D m_OutsideMaskTex;
        private int m_ActiveLampCount;

        public Texture3D OutsideMaskTexture => m_OutsideMaskTex;
        public bool HasConnectivityMask => m_OutsideMaskTex != null;
        public double LastMaskReadbackMs { get; private set; }
        public double LastMaskBfsMs { get; private set; }

        public bool ProbeOutsideAir(Vector3 world)
        {
            if (m_OutsideCpu == null) return true;
            int x = Mathf.FloorToInt((world.x - m_VolumeMin.x) / m_VoxelSize.x);
            int y = Mathf.FloorToInt((world.y - m_VolumeMin.y) / m_VoxelSize.y);
            int z = Mathf.FloorToInt((world.z - m_VolumeMin.z) / m_VoxelSize.z);
            if (x < 0 || y < 0 || z < 0 || x >= m_Dims.x || y >= m_Dims.y || z >= m_Dims.z) return true;
            return m_OutsideCpu[x + m_Dims.x * (y + m_Dims.y * z)] != 0;
        }

        private void BuildOutsideMask()
        {
            int dx = m_Dims.x, dy = m_Dims.y, dz = m_Dims.z;
            int count = dx * dy * dz;
            if (m_BlockedCpu == null || m_BlockedCpu.Length != count)
            {
                m_BlockedCpu = new byte[count];
                m_OutsideCpu = new byte[count];
                m_BfsQueue = new int[count];
            }
            Array.Clear(m_OutsideCpu, 0, count);

            var readbackSw = Stopwatch.StartNew();
            if (m_OccluderBuffer == null || m_BlockedUInt == null || m_BlockedUInt.Length != count)
            {
                UnityEngine.Debug.LogWarning("[Dynamic GI] Occluder buffer unavailable; connectivity gate disabled for this update.");
                if (m_OutsideMaskTex) { Destroy(m_OutsideMaskTex); m_OutsideMaskTex = null; }
                return;
            }
            m_OccluderBuffer.GetData(m_BlockedUInt, 0, 0, count);
            LastMaskReadbackMs = readbackSw.Elapsed.TotalMilliseconds;

            var bfsSw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                m_BlockedCpu[i] = m_BlockedUInt[i] != 0 ? (byte)1 : (byte)0;

            int head = 0, tail = 0;
            void Seed(int index)
            {
                if (m_BlockedCpu[index] != 0 || m_OutsideCpu[index] != 0) return;
                m_OutsideCpu[index] = 255;
                m_BfsQueue[tail++] = index;
            }

            for (int z = 0; z < dz; z++)
            for (int y = 0; y < dy; y++)
            {
                int row = y * dx + z * dx * dy;
                Seed(row);
                Seed(dx - 1 + row);
            }
            for (int z = 0; z < dz; z++)
            for (int x = 0; x < dx; x++)
            {
                Seed(x + z * dx * dy);
                Seed(x + (dy - 1) * dx + z * dx * dy);
            }
            for (int y = 0; y < dy; y++)
            for (int x = 0; x < dx; x++)
            {
                Seed(x + y * dx);
                Seed(x + y * dx + (dz - 1) * dx * dy);
            }

            int plane = dx * dy;
            while (head < tail)
            {
                int index = m_BfsQueue[head++];
                int x = index % dx;
                int y = (index / dx) % dy;
                int z = index / plane;
                if (x > 0) Seed(index - 1);
                if (x < dx - 1) Seed(index + 1);
                if (y > 0) Seed(index - dx);
                if (y < dy - 1) Seed(index + dx);
                if (z > 0) Seed(index - plane);
                if (z < dz - 1) Seed(index + plane);
            }

            // Extend the outdoor label by one cell (6-connected). Query points in
            // front of a surface lie in the conservative one-voxel shell, which
            // the flood fill by construction treats as blocked. Use a snapshot so
            // the extension cannot cascade more than one cell into enclosed air.
            Array.Copy(m_OutsideCpu, m_BlockedCpu, count);
            for (int z = 0; z < dz; z++)
            for (int y = 0; y < dy; y++)
            {
                int row = y * dx + z * plane;
                for (int x = 0; x < dx; x++)
                {
                    int index = x + row;
                    if (m_OutsideCpu[index] != 0) continue;
                    bool adjacent =
                        (x > 0 && m_BlockedCpu[index - 1] != 0) ||
                        (x < dx - 1 && m_BlockedCpu[index + 1] != 0) ||
                        (y > 0 && m_BlockedCpu[index - dx] != 0) ||
                        (y < dy - 1 && m_BlockedCpu[index + dx] != 0) ||
                        (z > 0 && m_BlockedCpu[index - plane] != 0) ||
                        (z < dz - 1 && m_BlockedCpu[index + plane] != 0);
                    if (adjacent) m_OutsideCpu[index] = 255;
                }
            }
            LastMaskBfsMs = bfsSw.Elapsed.TotalMilliseconds;

            int outsideCount = 0;
            int blockedCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (m_OutsideCpu[i] != 0) outsideCount++;
                if (m_BlockedUInt[i] != 0) blockedCount++;
            }
            string probes = "";
            if (m_Scenario && m_Scenario.mainCamera)
            {
                var cam = m_Scenario.mainCamera.transform;
                probes += "cam=" + (ProbeOutsideAir(cam.position) ? "1 " : "0 ");
                probes += "camFwd=" + (ProbeOutsideAir(cam.position + cam.forward * 2f) ? "1 " : "0 ");
            }
            if (m_Scenario && m_Scenario.receivers != null)
            {
                for (int i = 0; i < m_Scenario.receivers.Length; i++)
                {
                    var p = m_Scenario.receivers[i].point;
                    if (p) probes += m_Scenario.receivers[i].name + "=" + (ProbeOutsideAir(p.position) ? "1 " : "0 ");
                }
            }
            UnityEngine.Debug.Log("[Dynamic GI] mask blocked=" + blockedCount + "/" + count + " outside=" + outsideCount + " probes: " + probes);

            if (m_OutsideMaskTex == null || m_OutsideMaskTex.width != dx || m_OutsideMaskTex.height != dy || m_OutsideMaskTex.depth != dz)
            {
                if (m_OutsideMaskTex) Destroy(m_OutsideMaskTex);
                m_OutsideMaskTex = new Texture3D(dx, dy, dz, TextureFormat.R8, false)
                {
                    name = "GI_OutsideMask",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 0,
                };
            }
            m_OutsideMaskTex.SetPixelData(m_OutsideCpu, 0);
            m_OutsideMaskTex.Apply(false, false);

        }

        private void ReleaseConnectivity()
        {
            m_BlockedCpu = null;
            m_OutsideCpu = null;
            m_BfsQueue = null;
            if (m_OutsideMaskTex)
            {
                if (Application.isPlaying) Destroy(m_OutsideMaskTex); else DestroyImmediate(m_OutsideMaskTex);
                m_OutsideMaskTex = null;
            }
        }
    }
}
