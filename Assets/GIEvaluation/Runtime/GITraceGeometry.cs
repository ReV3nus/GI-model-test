using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GIEvaluation
{
    // A world-space BVH shared by every gather, sunlight and skylight ray.
    public sealed class GITraceGeometry : IDisposable
    {
        public ComputeBuffer Nodes { get; private set; }
        public ComputeBuffer Triangles { get; private set; }
        public int TriangleCount { get; private set; }
        public int NodeCount { get; private set; }
        public Bounds WorldBounds { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Triangle
        {
            public Vector3 a;
            public float red;
            public Vector3 edge1;
            public float green;
            public Vector3 edge2;
            public float blue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Node
        {
            public Vector3 min;
            public int left;
            public Vector3 max;
            public int right;
        }

        private struct Primitive
        {
            public Vector3 min, max, center;
            public int triangle;
        }

        private struct SourceTriangle
        {
            public int i0, i1, i2, packed;
        }

        private struct RendererRange
        {
            public Mesh mesh;
            public int start, end;
        }

        private Node[] nodeData;
        private Triangle[] triangleData;
        private SourceTriangle[] sourceTriangles;
        private Dictionary<Renderer, RendererRange> ranges;
        private int[] parent, leaf;
        private readonly List<Vector3> movingVertices = new List<Vector3>();
        private readonly HashSet<int> dirtyBranches = new HashSet<int>();
        private readonly List<int> changedNodes = new List<int>(), changedTriangles = new List<int>();

        private struct Bin
        {
            public Vector3 min, max;
            public int count;

            public void Include(Vector3 lo, Vector3 hi, int number)
            {
                if (number == 0) return;
                min = count == 0 ? lo : Vector3.Min(min, lo);
                max = count == 0 ? hi : Vector3.Max(max, hi);
                count += number;
            }

            public float Cost()
            {
                Vector3 s = max - min;
                return count * (s.x * s.y + s.y * s.z + s.z * s.x);
            }
        }

        private sealed class Builder
        {
            private const int Bins = 12;
            public readonly List<Node> nodes = new List<Node>();
            public readonly List<Triangle> triangles = new List<Triangle>();
            public readonly Dictionary<Renderer, RendererRange> ranges = new Dictionary<Renderer, RendererRange>();
            private readonly List<SourceTriangle> sourceIndices = new List<SourceTriangle>();
            private readonly List<Primitive> primitives = new List<Primitive>();
            private readonly Dictionary<Material, Vector3> materialColors = new Dictionary<Material, Vector3>();
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<int> indices = new List<int>();
            private readonly List<Material> materials = new List<Material>();
            private readonly Bin[] bins = new Bin[Bins];
            private readonly float[] rightCosts = new float[Bins];
            private Primitive[] order;

            public void AddRenderer(MeshRenderer renderer)
            {
                if (!renderer.enabled || renderer.forceRenderingOff || !renderer.TryGetComponent(out MeshFilter filter)) return;
                Mesh mesh = filter.sharedMesh;
                if (!mesh) return;
                if (!mesh.isReadable)
                {
                    Debug.LogWarning("[Dynamic GI] Enable Read/Write to include mesh: " + mesh.name, renderer);
                    return;
                }

                renderer.GetSharedMaterials(materials);
                mesh.GetVertices(vertices);
                Matrix4x4 transform = renderer.localToWorldMatrix;
                for (int i = 0; i < vertices.Count; i++)
                    vertices[i] = transform.MultiplyPoint3x4(vertices[i]);

                int start = triangles.Count;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles || s >= materials.Count) continue;
                    Material mat = materials[s];
                    if (!IsOpaque(mat)) continue;
                    if (!materialColors.TryGetValue(mat, out Vector3 color))
                    {
                        color = DiffuseColor(mat);
                        materialColors.Add(mat, color);
                    }

                    mesh.GetTriangles(indices, s, true);
                    for (int i = 0; i + 2 < indices.Count; i += 3)
                    {
                        Vector3 a = vertices[indices[i]];
                        Vector3 b = vertices[indices[i + 1]];
                        Vector3 c = vertices[indices[i + 2]];
                        Vector3 edge1 = b - a, edge2 = c - a;
                        if (Vector3.Cross(edge1, edge2).sqrMagnitude < 1e-16f) continue;
                        var lo = Vector3.Min(Vector3.Min(a, b), c);
                        var hi = Vector3.Max(Vector3.Max(a, b), c);
                        primitives.Add(new Primitive { min = lo, max = hi, center = (lo + hi) * 0.5f, triangle = triangles.Count });
                        sourceIndices.Add(new SourceTriangle { i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2] });
                        triangles.Add(new Triangle { a = a, edge1 = edge1, edge2 = edge2, red = color.x, green = color.y, blue = color.z });
                    }
                }
                if (Mathf.Abs(transform.determinant) >= 1e-10f)
                    ranges[renderer] = new RendererRange { start = start, end = triangles.Count, mesh = mesh };
            }

            public Triangle[] BuildHierarchy(out SourceTriangle[] mapping)
            {
                mapping = sourceIndices.ToArray();
                order = primitives.ToArray();
                if (order.Length == 0) return Array.Empty<Triangle>();
                Split(0, order.Length, 0);
                var packed = new Triangle[order.Length];
                for (int i = 0; i < packed.Length; i++)
                {
                    int source = order[i].triangle;
                    packed[i] = triangles[source];
                    mapping[source].packed = i;
                }
                return packed;
            }

            // Binned surface-area heuristic along the widest centroid axis. Upper bound on
            // recursion depth also bounds the traversal shader's local stack.
            private int Split(int start, int count, int depth)
            {
                Vector3 lo = order[start].min, hi = order[start].max;
                Vector3 centersLo = order[start].center, centersHi = centersLo;
                int end = start + count;
                for (int i = start + 1; i < end; i++)
                {
                    lo = Vector3.Min(lo, order[i].min);
                    hi = Vector3.Max(hi, order[i].max);
                    centersLo = Vector3.Min(centersLo, order[i].center);
                    centersHi = Vector3.Max(centersHi, order[i].center);
                }

                int index = nodes.Count;
                var node = new Node { min = lo, max = hi, left = start, right = -count };
                nodes.Add(node);
                if (count <= 4 || depth >= 29) return index;
                Vector3 extents = centersHi - centersLo;
                int axis = extents.y > extents.x ? 1 : 0;
                if (extents.z > extents[axis]) axis = 2;
                if (extents[axis] < 1e-7f) return index;

                float scale = Bins / extents[axis];
                Array.Clear(bins, 0, bins.Length);
                for (int i = start; i < end; i++)
                {
                    int bin = Mathf.Min(Bins - 1, (int)((order[i].center[axis] - centersLo[axis]) * scale));
                    bins[bin].Include(order[i].min, order[i].max, 1);
                }
                Bin right = default, left = default;
                for (int i = Bins - 1; i > 0; i--)
                {
                    right.Include(bins[i].min, bins[i].max, bins[i].count);
                    rightCosts[i] = right.Cost();
                }
                float best = float.PositiveInfinity;
                int boundary = 1;
                for (int i = 1; i < Bins; i++)
                {
                    Bin bin = bins[i - 1];
                    left.Include(bin.min, bin.max, bin.count);
                    if (left.count == 0 || left.count == count) continue;
                    float cost = left.Cost() + rightCosts[i];
                    if (cost < best) { best = cost; boundary = i; }
                }
                int mid = start;
                for (int i = start; i < end; i++)
                {
                    if ((order[i].center[axis] - centersLo[axis]) * scale >= boundary) continue;
                    Primitive swap = order[mid];
                    order[mid++] = order[i];
                    order[i] = swap;
                }
                if (mid == start || mid == end) mid = start + count / 2;
                node.left = Split(start, mid - start, depth + 1);
                node.right = Split(mid, end - mid, depth + 1);
                nodes[index] = node;
                return index;
            }
        }

        private struct TextureAverage
        {
            public uint revision;
            public Color mean;
        }
        private static readonly Dictionary<Texture, TextureAverage> meanColors = new Dictionary<Texture, TextureAverage>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ClearTextureCache() => meanColors.Clear();

        public static bool IsOpaque(Material m)
        {
            return m && m.renderQueue <= (int)RenderQueue.GeometryLast &&
                   (!m.HasProperty("_Surface") || m.GetFloat("_Surface") < 0.5f);
        }

        private static Vector3 DiffuseColor(Material mat)
        {
            Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") :
                mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            baseColor = baseColor.linear;
            Texture map = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") :
                mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            if (map && map.dimension == TextureDimension.Tex2D)
            {
                if (!meanColors.TryGetValue(map, out TextureAverage average) || average.revision != map.updateCount)
                {
                    average = new TextureAverage { mean = EstimateMean(map), revision = map.updateCount };
                    meanColors[map] = average;
                }
                baseColor *= average.mean;
            }
            float diffuse = mat.HasProperty("_Metallic") ? 1f - Mathf.Clamp01(mat.GetFloat("_Metallic")) : 1f;
            return new Vector3(Mathf.Clamp01(baseColor.r * diffuse), Mathf.Clamp01(baseColor.g * diffuse), Mathf.Clamp01(baseColor.b * diffuse));
        }

        // Minification selects coarse texture mips even on non-CPU-readable imported textures.
        // Read back a small linear target once per unique source texture.
        private static Color EstimateMean(Texture source)
        {
            const int size = 8;
            RenderTexture previous = RenderTexture.active;
            bool srgbWrite = GL.sRGBWrite;
            var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var sample = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            try
            {
                GL.sRGBWrite = false;
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                sample.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                Color sum = Color.clear;
                foreach (var pixel in sample.GetPixels32()) sum += (Color)pixel;
                return sum / (size * size);
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = srgbWrite;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.Destroy(sample);
            }
        }

        public static GITraceGeometry Build()
        {
            var builder = new Builder();
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude);
            foreach (var renderer in renderers) builder.AddRenderer(renderer);
            Triangle[] packed = builder.BuildHierarchy(out SourceTriangle[] mapping);
            var result = new GITraceGeometry {
                TriangleCount = packed.Length, NodeCount = builder.nodes.Count,
                triangleData = packed, nodeData = builder.nodes.ToArray(), ranges = builder.ranges,
                sourceTriangles = mapping, parent = new int[builder.nodes.Count], leaf = new int[packed.Length]
            };
            result.IndexHierarchy();
            try
            {
                result.Nodes = new ComputeBuffer(Mathf.Max(1, result.NodeCount), 32);
                result.Triangles = new ComputeBuffer(Mathf.Max(1, result.TriangleCount), 48);
                if (packed.Length > 0)
                {
                    result.Nodes.SetData(builder.nodes);
                    result.Triangles.SetData(packed);
                    var bounds = new Bounds();
                    bounds.SetMinMax(builder.nodes[0].min, builder.nodes[0].max);
                    result.WorldBounds = bounds;
                }
                else
                {
                    result.Nodes.SetData(new[] { new Node { right = -1 } });
                    result.Triangles.SetData(new Triangle[1]);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private void IndexHierarchy()
        {
            if (NodeCount == 0) return;
            parent[0] = -1;
            for (int i = 0; i < NodeCount; i++)
            {
                Node node = nodeData[i];
                if (node.right >= 0)
                {
                    parent[node.left] = parent[node.right] = i;
                    continue;
                }
                for (int t = node.left; t < node.left - node.right; t++) leaf[t] = i;
            }
        }

        // Keep the spatial partition and refit bounds. Original mesh positions are reread
        // on each move so repeated parking/unparking never integrates transform roundoff.
        public bool TryRefitTransforms(IReadOnlyList<Renderer> moved)
        {
            for (int i = 0; i < moved.Count; i++)
            {
                var r = moved[i];
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy || !ranges.TryGetValue(r, out var range) ||
                    !r.TryGetComponent<MeshFilter>(out var f) || f.sharedMesh != range.mesh ||
                    Mathf.Abs(r.localToWorldMatrix.determinant) < 1e-10f) return false;
            }

            dirtyBranches.Clear();
            changedNodes.Clear();
            changedTriangles.Clear();
            for (int i = 0; i < moved.Count; i++)
            {
                var r = moved[i];
                var range = ranges[r];
                range.mesh.GetVertices(movingVertices);
                Matrix4x4 matrix = r.localToWorldMatrix;
                for (int v = 0; v < movingVertices.Count; v++)
                    movingVertices[v] = matrix.MultiplyPoint3x4(movingVertices[v]);
                for (int j = range.start; j < range.end; j++)
                {
                    var source = sourceTriangles[j];
                    int t = source.packed;
                    Vector3 a = movingVertices[source.i0];
                    triangleData[t].a = a;
                    triangleData[t].edge1 = movingVertices[source.i1] - a;
                    triangleData[t].edge2 = movingVertices[source.i2] - a;
                    changedTriangles.Add(t);
                    for (int n = leaf[t]; n >= 0 && dirtyBranches.Add(n); n = parent[n])
                        changedNodes.Add(n);
                }
            }
            changedNodes.Sort();
            changedTriangles.Sort();
            // Depth-first construction assigns each child a higher index than its parent.
            for (int k = changedNodes.Count - 1; k >= 0; k--)
            {
                int idx = changedNodes[k];
                Node node = nodeData[idx];
                if (node.right < 0)
                {
                    Vector3 lo = triangleData[node.left].a, hi = lo;
                    for (int j = node.left; j < node.left - node.right; j++)
                    {
                        Triangle t = triangleData[j];
                        Vector3 b = t.a + t.edge1, c = t.a + t.edge2;
                        lo = Vector3.Min(lo, Vector3.Min(t.a, Vector3.Min(b, c)));
                        hi = Vector3.Max(hi, Vector3.Max(t.a, Vector3.Max(b, c)));
                    }
                    node.min = lo;
                    node.max = hi;
                }
                else
                {
                    node.min = Vector3.Min(nodeData[node.left].min, nodeData[node.right].min);
                    node.max = Vector3.Max(nodeData[node.left].max, nodeData[node.right].max);
                }
                nodeData[idx] = node;
            }
            UploadRanges(Nodes, nodeData, changedNodes);
            UploadRanges(Triangles, triangleData, changedTriangles);
            if (NodeCount > 0)
            {
                Bounds bounds = default;
                bounds.SetMinMax(nodeData[0].min, nodeData[0].max);
                WorldBounds = bounds;
            }
            return true;
        }

        // Small gaps cost less as one contiguous transfer than as many driver calls.
        private static void UploadRanges<T>(ComputeBuffer destination, T[] source, List<int> sorted) where T : struct
        {
            for (int i = 0; i < sorted.Count;)
            {
                int first = sorted[i++], last = first;
                while (i < sorted.Count && sorted[i] <= last + 16) last = sorted[i++];
                destination.SetData(source, first, first, last - first + 1);
            }
        }

        public void Dispose()
        {
            Nodes?.Release();
            Triangles?.Release();
            Nodes = null;
            Triangles = null;
        }
    }
}
