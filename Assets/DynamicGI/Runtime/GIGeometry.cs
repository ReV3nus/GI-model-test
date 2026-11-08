using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI
{
    /// <summary>Camera-independent, world-space opaque triangle snapshot and binary BVH.
    /// No fixture names, state IDs, colliders or predefined illumination enter this data.</summary>
    public sealed class GIGeometry : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Triangle
        {
            public Vector3 v0; public float pad0;
            public Vector3 e1; public float pad1;
            public Vector3 e2; public float pad2;
            public Vector3 n0; public float pad3;
            public Vector3 n1; public float pad4;
            public Vector3 n2; public float pad5;
            public Vector3 albedo; public float pad6;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Node
        {
            public Vector3 min; public int left;
            public Vector3 max; public int right;
            public int start; public int count; public Vector2 pad;
        }

        struct Primitive
        {
            public Triangle triangle;
            public Vector3 min, max, center;
        }

        struct RetiredBuffers
        {
            public ComputeBuffer triangles, nodes;
            public int frame;
            public long bytes;
        }

        public ComputeBuffer Triangles, Nodes;
        public int TriangleCount, NodeCount;
        public int RendererCount, SkippedRenderers, SkippedSubmeshes, TextureAverageCount;
        public long BufferBytes;
        public double BuildMilliseconds;
        public double ExtractionMilliseconds, BVHMilliseconds, UploadMilliseconds, ReleaseMilliseconds, TextureReadbackMilliseconds;
        public int MaterialCount, TextureReadbackCount;
        public long ActiveBufferBytes, RetiredBufferBytes;
        public string LastWarning = "";
        readonly Dictionary<int, Color> textureAverages = new Dictionary<int, Color>();
        readonly Dictionary<int, uint> textureRevisions = new Dictionary<int, uint>();
        readonly Dictionary<int, Vector3> buildMaterialColors = new Dictionary<int, Vector3>();
        readonly List<RetiredBuffers> retiredBuffers = new List<RetiredBuffers>();
        Primitive[] primitives;
        readonly List<Node> nodes = new List<Node>();

        static bool Opaque(Material material)
        {
            if (!material || material.renderQueue > (int)RenderQueue.GeometryLast) return false;
            return !(material.HasProperty("_Surface") && material.GetFloat("_Surface") > .5f);
        }

        public void Rebuild(ISet<int> pendingRemovals = null)
        {
            var timer = Stopwatch.StartNew();
            TextureReadbackMilliseconds = 0; TextureReadbackCount = 0;
            buildMaterialColors.Clear();
            // Deleting a buffer used by the previous GPU frame can synchronize the
            // CPU with the GPU. Retire older snapshots after four submitted frames.
            for (int i = retiredBuffers.Count - 1; i >= 0; --i)
            {
                var retired = retiredBuffers[i];
                if (Time.frameCount - retired.frame < 4) continue;
                retired.triangles?.Release(); retired.nodes?.Release(); retiredBuffers.RemoveAt(i);
            }
            ReleaseMilliseconds = timer.Elapsed.TotalMilliseconds;
            var list = new List<Primitive>(16000);
            RendererCount = SkippedRenderers = SkippedSubmeshes = 0;
            LastWarning = "";
            foreach (var renderer in SortedRenderers(false))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                // Removed notifications may precede Unity's deferred Destroy at the
                // end of this frame. Such objects must not enter the new snapshot.
                if (pendingRemovals != null && pendingRemovals.Contains(renderer.GetEntityId().GetHashCode())) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter ? filter.sharedMesh : null;
                if (!mesh) continue;
                if (!mesh.isReadable)
                {
                    SkippedRenderers++;
                    LastWarning = "Some mesh CPU data is unreadable; enable Read/Write on imported meshes.";
                    continue;
                }
                RendererCount++;
                var vertices = mesh.vertices;
                var world = renderer.localToWorldMatrix;
                var worldVertices = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; ++i) worldVertices[i] = world.MultiplyPoint3x4(vertices[i]);
                var materials = renderer.sharedMaterials;
                for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                {
                    var material = materials.Length == 0 ? null : materials[Mathf.Min(sub, materials.Length - 1)];
                    if (!Opaque(material) || mesh.GetTopology(sub) != MeshTopology.Triangles)
                    {
                        SkippedSubmeshes++;
                        continue;
                    }
                    int materialKey = material.GetEntityId().GetHashCode();
                    if (!buildMaterialColors.TryGetValue(materialKey, out var albedo))
                    {
                        albedo = DiffuseAlbedo(material); buildMaterialColors.Add(materialKey, albedo);
                    }
                    var indices = mesh.GetTriangles(sub);
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        Vector3 a = worldVertices[indices[i]], b = worldVertices[indices[i + 1]], c = worldVertices[indices[i + 2]];
                        Vector3 e1 = b - a, e2 = c - a, cross = Vector3.Cross(e1, e2);
                        if (cross.sqrMagnitude < 1e-16f) continue;
                        Vector3 n = cross.normalized;
                        var lo = Vector3.Min(a, Vector3.Min(b, c));
                        var hi = Vector3.Max(a, Vector3.Max(b, c));
                        list.Add(new Primitive
                        {
                            triangle = new Triangle { v0 = a, e1 = e1, e2 = e2, n0 = n, n1 = n, n2 = n, albedo = albedo },
                            min = lo, max = hi, center = (lo + hi) * .5f
                        });
                    }
                }
            }
            primitives = list.ToArray();
            ExtractionMilliseconds = timer.Elapsed.TotalMilliseconds - ReleaseMilliseconds;
            nodes.Clear();
            TriangleCount = primitives.Length;
            if (TriangleCount > 0) BuildNode(0, TriangleCount);
            NodeCount = nodes.Count;
            BVHMilliseconds = timer.Elapsed.TotalMilliseconds - ReleaseMilliseconds - ExtractionMilliseconds;
            var data = new Triangle[Mathf.Max(1, TriangleCount)];
            for (int i = 0; i < TriangleCount; ++i) data[i] = primitives[i].triangle;
            if (Triangles != null || Nodes != null)
                retiredBuffers.Add(new RetiredBuffers { triangles = Triangles, nodes = Nodes, frame = Time.frameCount, bytes = ActiveBufferBytes });
            Triangles = new ComputeBuffer(data.Length, 112, ComputeBufferType.Structured);
            Triangles.SetData(data);
            Nodes = new ComputeBuffer(Mathf.Max(1, NodeCount), 48, ComputeBufferType.Structured);
            Nodes.SetData(NodeCount > 0 ? nodes.ToArray() : new Node[1]);
            ActiveBufferBytes = (long)data.Length * 112 + (long)Mathf.Max(1, NodeCount) * 48;
            RetiredBufferBytes = 0;
            foreach (var retired in retiredBuffers) RetiredBufferBytes += retired.bytes;
            BufferBytes = ActiveBufferBytes + RetiredBufferBytes;
            primitives = null;
            TextureAverageCount = textureAverages.Count;
            MaterialCount = buildMaterialColors.Count;
            UploadMilliseconds = timer.Elapsed.TotalMilliseconds - ReleaseMilliseconds - ExtractionMilliseconds - BVHMilliseconds;
            timer.Stop(); BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        int BuildNode(int start, int count)
        {
            int index = nodes.Count;
            var lo = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var hi = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var clo = lo; var chi = hi;
            for (int i = start; i < start + count; ++i)
            {
                lo = Vector3.Min(lo, primitives[i].min); hi = Vector3.Max(hi, primitives[i].max);
                clo = Vector3.Min(clo, primitives[i].center); chi = Vector3.Max(chi, primitives[i].center);
            }
            var node = new Node { min = lo - Vector3.one * 0.00001f, max = hi + Vector3.one * 0.00001f, start = start, count = count, left = -1, right = -1 };
            nodes.Add(node);
            if (count > 4)
            {
                var extent = chi - clo;
                int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
                PartitionMedian(start, count, axis);
                int half = count / 2;
                node.left = BuildNode(start, half); node.right = BuildNode(start + half, count - half);
                node.count = 0;
                nodes[index] = node;
            }
            return index;
        }

        // A full sort at every tree level does unnecessary O(n log^2 n) comparisons
        // and copies a 148-byte primitive for each comparison/swap. Quickselect only
        // partitions the requested median; children retain the same balanced layout.
        void PartitionMedian(int start, int count, int axis)
        {
            int low = start, high = start + count - 1, target = start + count / 2;
            while (low < high)
            {
                float pivot = primitives[(low + high) / 2].center[axis];
                int left = low, right = high;
                while (left <= right)
                {
                    while (primitives[left].center[axis] < pivot) ++left;
                    while (primitives[right].center[axis] > pivot) --right;
                    if (left <= right)
                    {
                        var temporary = primitives[left]; primitives[left] = primitives[right]; primitives[right] = temporary;
                        ++left; --right;
                    }
                }
                if (target <= right) high = right;
                else if (target >= left) low = left;
                else break;
            }
        }

        Vector3 DiffuseAlbedo(Material material)
        {
            // Material color is stored in the authoring sRGB convention; shader color
            // properties are converted in a linear project. Textures are averaged in linear.
            Color color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear;
            Texture tex = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            if (tex) color *= TextureAverage(tex);
            float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0;
            return new Vector3(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b)) * (1 - Mathf.Clamp01(metallic));
        }

        Color TextureAverage(Texture texture)
        {
            int id = texture.GetEntityId().GetHashCode();
            if (textureAverages.TryGetValue(id, out var average) && textureRevisions.TryGetValue(id, out uint revision) && revision == texture.updateCount) return average;
            var readbackTimer = Stopwatch.StartNew(); TextureReadbackCount++;
            // A tiny GPU downsample supports non-readable imported textures too. This
            // synchronous read occurs only when a new texture enters the geometry cache.
            var previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            var rt = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Texture2D readback = null;
            try
            {
                GL.sRGBWrite = false;
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readback = new Texture2D(16, 16, TextureFormat.RGBAFloat, false, true);
                readback.ReadPixels(new Rect(0, 0, 16, 16), 0, 0); readback.Apply();
                var pixels = readback.GetPixels();
                average = Color.clear;
                foreach (var pixel in pixels) average += pixel;
                average /= pixels.Length;
                average.a = 1;
            }
            catch (Exception e)
            {
                average = Color.white;
                LastWarning = "Texture average fallback: " + e.Message;
            }
            finally
            {
                RenderTexture.active = previous; GL.sRGBWrite = previousSrgb;
                RenderTexture.ReleaseTemporary(rt);
                if (readback) UnityEngine.Object.Destroy(readback);
            }
            textureAverages[id] = average;
            textureRevisions[id] = texture.updateCount;
            TextureReadbackMilliseconds += readbackTimer.Elapsed.TotalMilliseconds;
            return average;
        }

        /// <summary>Fallback for clients not publishing GeometryChanges. In-place mesh
        /// vertex edits with unchanged bounds still require a MeshChanged notification.</summary>
        public static int ComputeSceneSignature()
        {
            unchecked
            {
                int hash = 17;
                foreach (var renderer in SortedRenderers(true))
                {
                    hash = hash * 31 + renderer.GetEntityId().GetHashCode();
                    hash = hash * 31 + (renderer.enabled && renderer.gameObject.activeInHierarchy ? 1 : 0);
                    hash = hash * 31 + renderer.localToWorldMatrix.GetHashCode();
                    var filter = renderer.GetComponent<MeshFilter>(); var mesh = filter ? filter.sharedMesh : null;
                    hash = hash * 31 + (mesh ? mesh.GetEntityId().GetHashCode() : 0);
                    if (mesh) { hash = hash * 31 + mesh.vertexCount; hash = hash * 31 + mesh.bounds.GetHashCode(); }
                    foreach (var material in renderer.sharedMaterials)
                    {
                        hash = hash * 31 + (material ? material.GetEntityId().GetHashCode() : 0);
                        if (!material) continue;
                        hash = hash * 31 + material.renderQueue;
                        if (material.HasProperty("_BaseColor")) hash = hash * 31 + material.GetColor("_BaseColor").GetHashCode();
                        else if (material.HasProperty("_Color")) hash = hash * 31 + material.GetColor("_Color").GetHashCode();
                        if (material.HasProperty("_Metallic")) hash = hash * 31 + material.GetFloat("_Metallic").GetHashCode();
                        if (material.HasProperty("_Surface")) hash = hash * 31 + material.GetFloat("_Surface").GetHashCode();
                        var tex = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : material.mainTexture;
                        hash = hash * 31 + (tex ? tex.GetEntityId().GetHashCode() : 0);
                        if (tex) hash = hash * 31 + tex.updateCount.GetHashCode();
                    }
                }
                return hash;
            }
        }

        static MeshRenderer[] SortedRenderers(bool includeInactive)
        {
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
            Array.Sort(renderers, (a, b) => EntityId.ToULong(a.GetEntityId()).CompareTo(EntityId.ToULong(b.GetEntityId())));
            return renderers;
        }

        public void Dispose()
        {
            Triangles?.Release(); Nodes?.Release(); Triangles = Nodes = null;
            foreach (var retired in retiredBuffers) { retired.triangles?.Release(); retired.nodes?.Release(); }
            retiredBuffers.Clear();
            textureAverages.Clear(); textureRevisions.Clear(); buildMaterialColors.Clear(); nodes.Clear(); primitives = null;
            ActiveBufferBytes = RetiredBufferBytes = BufferBytes = 0;
        }
    }
}
