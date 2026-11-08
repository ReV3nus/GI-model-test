using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GIEvaluation
{
    // Orthogonal charts use each triangle's longest edge as the horizontal axis.
    // Long thin triangles therefore need few rows, even on a large mesh.
    public sealed class GISurfaceReceivers : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Sample
        {
            public Vector3 position;
            public float padding0;
            public Vector3 normal;
            public float padding1;
        }

        private struct Corner
        {
            public Vector3 position, normal;
            public Vector2 uv;
        }

        private sealed class Batch : IDisposable
        {
            private readonly List<Vector3> positions = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<Vector4> chart = new List<Vector4>();
            private readonly List<Vector2> barycentric = new List<Vector2>();
            private readonly List<int> indices = new List<int>();
            private readonly Material material;
            private readonly int layer;
            private Mesh mesh;

            public Batch(Shader shader, Material source, int layer)
            {
                this.layer = layer;
                material = new Material(shader) {hideFlags = HideFlags.DontSave};
                string map = source.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                if (source.HasProperty(map))
                {
                    material.SetTexture("_BaseMap", source.GetTexture(map));
                    material.SetTextureOffset("_BaseMap", source.GetTextureOffset(map));
                    material.SetTextureScale("_BaseMap", source.GetTextureScale(map));
                }
                string tint = source.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                if (source.HasProperty(tint)) material.SetColor("_BaseColor", source.GetColor(tint));
                if (source.HasProperty("_Metallic")) material.SetFloat("_Metallic", source.GetFloat("_Metallic"));
                if (source.HasProperty("_Cull")) material.SetFloat("_Cull", source.GetFloat("_Cull"));
            }

            public void Add(Vector3 p, Vector2 uv, Vector2 bary, Vector4 address)
            {
                indices.Add(positions.Count);
                positions.Add(p);
                uvs.Add(uv);
                barycentric.Add(bary);
                chart.Add(address);
            }

            public int VertexCount => positions.Count;

            public Vector3[] CopyPositions(int start, int count) => positions.GetRange(start, count).ToArray();

            public void MoveRange(int start, Vector3[] initial, Matrix4x4 delta)
            {
                for (int i = 0; i < initial.Length; i++) positions[start + i] = delta.MultiplyPoint3x4(initial[i]);
            }

            public void UploadPositions()
            {
                mesh.SetVertices(positions);
                mesh.RecalculateBounds();
            }

            public long MeshBytes => mesh == null ? 0L : (long)mesh.vertexCount * (44 + 4);

            public void Upload()
            {
                if (positions.Count == 0) return;
                mesh = new Mesh { name = "GI receiver triangles", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
                mesh.SetVertices(positions);
                mesh.SetUVs(0, uvs);
                mesh.SetUVs(1, chart);
                mesh.SetUVs(2, barycentric);
                mesh.SetTriangles(indices, 0);
                mesh.UploadMeshData(false);
                positions.TrimExcess();
                uvs.Clear(); uvs.TrimExcess();
                chart.Clear(); chart.TrimExcess();
                barycentric.Clear(); barycentric.TrimExcess();
                indices.Clear(); indices.TrimExcess();
            }

            public void Draw(MaterialPropertyBlock properties)
            {
                if (!mesh) return;
                var render = new RenderParams(material)
                {
                    layer = layer, worldBounds = mesh.bounds, matProps = properties,
                    shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
                    lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = ReflectionProbeUsage.Off,
                    motionVectorMode = MotionVectorGenerationMode.ForceNoMotion
                };
                Graphics.RenderMesh(render, mesh, 0, Matrix4x4.identity);
            }

#if UNITY_EDITOR
            public bool Hit(Ray ray, ref float closest, out int vertex, out Vector2 bary, out Vector3 normal)
            {
                vertex = -1;
                bary = default;
                normal = default;
                if (!mesh || !mesh.bounds.IntersectRay(ray)) return false;
                for (int i = 0; i < positions.Count; i += 3)
                {
                    Vector3 ab = positions[i + 1] - positions[i], ac = positions[i + 2] - positions[i];
                    Vector3 h = Vector3.Cross(ray.direction, ac);
                    float det = Vector3.Dot(ab, h);
                    if (det < 1e-10f) continue;
                    Vector3 offset = ray.origin - positions[i];
                    float u = Vector3.Dot(offset, h) / det;
                    if (u < -1e-5f || u > 1.00001f) continue;
                    Vector3 q = Vector3.Cross(offset, ab);
                    float v = Vector3.Dot(ray.direction, q) / det;
                    if (v < -1e-5f || u + v > 1.00001f) continue;
                    float distance = Vector3.Dot(ac, q) / det;
                    if (distance < 0f || distance >= closest) continue;
                    closest = distance;
                    vertex = i;
                    bary = new Vector2(u, v);
                    normal = Vector3.Cross(ab, ac).normalized;
                }
                return vertex >= 0;
            }

            public Vector3 Interpolate(int vertex, Vector2 bary, Vector4[] light, List<Vector4> scratch)
            {
                mesh.GetUVs(1, scratch);
                Vector4 address = scratch[vertex];
                int first = (int)address.x, columns = (int)address.y, rows = (int)address.z;
                float x = Mathf.Clamp((bary.x + address.w * bary.y) * columns - .5f, 0, columns - 1);
                float y = Mathf.Clamp(bary.y * rows - .5f, 0, rows - 1);
                int x0 = (int)x, y0 = (int)y;
                int x1 = Mathf.Min(x0 + 1, columns - 1), y1 = Mathf.Min(y0 + 1, rows - 1);
                Vector4 bottom = Vector4.Lerp(light[first + y0 * columns + x0], light[first + y0 * columns + x1], x - x0);
                Vector4 top = Vector4.Lerp(light[first + y1 * columns + x0], light[first + y1 * columns + x1], x - x0);
                return (Vector3)Vector4.Lerp(bottom, top, y - y0);
            }
#endif

            public void Dispose()
            {
                if (mesh) Object.Destroy(mesh);
                if (material) Object.Destroy(material);
            }
        }

        private sealed class Span
        {
            public Batch batch;
            public int start, count;
            public Vector3[] initial;
        }

        private sealed class Source
        {
            public Matrix4x4 original;
            public int start, count;
            public List<Span> spans = new List<Span>();
            public Sample[] initial;
        }

        private readonly Dictionary<Renderer, Source> sources = new Dictionary<Renderer, Source>();
        private readonly HashSet<Batch> movedBatches = new HashSet<Batch>();
        private readonly Dictionary<(Material, int), Batch> batches = new Dictionary<(Material, int), Batch>();
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private List<Sample> samples = new List<Sample>();
        public ComputeBuffer Samples { get; private set; }
        public int SampleCount => samples.Count;
        public long DisplayMeshBytes { get; private set; }

        public static GISurfaceReceivers Build(float spacing = .4f)
        {
            Shader shader = Resources.Load<Shader>("GIReceiverOverlay");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("GI receiver shader unavailable");
            var result = new GISurfaceReceivers();
            try
            {
                var renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude);
                foreach (var renderer in renderers)
                    result.Add(renderer, shader, Mathf.Max(.05f, spacing));
                foreach (var batch in result.batches.Values)
                {
                    batch.Upload();
                    result.DisplayMeshBytes += batch.MeshBytes;
                }
                result.samples.TrimExcess();
                result.Samples = new ComputeBuffer(Mathf.Max(1, result.SampleCount), 32);
                if (result.SampleCount > 0) result.Samples.SetData(result.samples);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private void Add(MeshRenderer renderer, Shader shader, float spacing)
        {
            if (!renderer.enabled || renderer.forceRenderingOff || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly ||
                !renderer.TryGetComponent<MeshFilter>(out var filter)) return;
            var mesh = filter.sharedMesh;
            if (!mesh || !mesh.isReadable) return;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            var transform = renderer.localToWorldMatrix;
            var normalTransform = transform.inverse.transpose;
            bool flip = transform.determinant < 0f;
            var corners = new Corner[vertices.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                corners[i].position = transform.MultiplyPoint3x4(vertices[i]);
                if (normals.Length == vertices.Length) corners[i].normal = normalTransform.MultiplyVector(normals[i]).normalized;
                if (uvs.Length == vertices.Length) corners[i].uv = uvs[i];
            }

            var entry = new Source { original = transform, start = samples.Count };
            sources[renderer] = entry;
            var materials = renderer.sharedMaterials;
            for (int sub = 0; sub < Mathf.Min(mesh.subMeshCount, materials.Length); sub++)
            {
                var source = materials[sub];
                if (!GITraceGeometry.IsOpaque(source) || mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var key = (source, renderer.gameObject.layer);
                if (!batches.TryGetValue(key, out var batch))
                    batches.Add(key, batch = new Batch(shader, source, key.Item2));
                int vertexStart = batch.VertexCount;
                var indices = mesh.GetTriangles(sub);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    var a = corners[indices[i]];
                    var b = corners[indices[i + (flip ? 2 : 1)]];
                    var c = corners[indices[i + (flip ? 1 : 2)]];
                    AddChart(batch, a, b, c, spacing);
                }
                int added = batch.VertexCount - vertexStart;
                if (added > 0) entry.spans.Add(new Span {batch = batch, start = vertexStart, count = added});
            }
            entry.count = samples.Count - entry.start;
        }

        private void AddChart(Batch batch, Corner a, Corner b, Corner c, float spacing)
        {
            float ab = (a.position - b.position).sqrMagnitude;
            float bc = (b.position - c.position).sqrMagnitude;
            float ca = (c.position - a.position).sqrMagnitude;
            if (bc > ab && bc >= ca) (a, b, c) = (b, c, a);
            else if (ca > ab) (a, b, c) = (c, a, b);
            var cross = Vector3.Cross(b.position - a.position, c.position - a.position);
            float twiceArea = cross.magnitude;
            if (twiceArea < 1e-10f) return;
            float length = Vector3.Distance(a.position, b.position);
            int columns = Mathf.Clamp(Mathf.CeilToInt(length / spacing), 1, 128);
            // A slender triangle's apex can lie close to the line of its base
            // yet far along that line. Subdivide along the base, too.
            int rows = Mathf.Clamp(Mathf.CeilToInt(twiceArea / (length * spacing)), 1, 128);
            float apex = Mathf.Clamp01(Vector3.Dot(c.position - a.position, b.position - a.position) / (length * length));
            var address = new Vector4(samples.Count, columns, rows, apex);
            batch.Add(a.position, a.uv, Vector2.zero, address);
            batch.Add(b.position, b.uv, Vector2.right, address);
            batch.Add(c.position, c.uv, Vector2.up, address);
            var faceNormal = cross / twiceArea;

            for (int row = 0; row < rows; row++)
            {
                float height = (row + .5f) / rows;
                for (int col = 0; col < columns; col++)
                {
                    float x = (col + .5f) / columns;
                    float top = x < apex ? x / apex : (1f - x) / (1f - apex);
                    // Keep the regular grid orthogonal in world space. Only samples outside
                    // this triangle are clamped; stretching every row into the apex
                    // would turn Monte Carlo variation into long fan-shaped streaks.
                    float t = rows == 1 ? top * .5f : Mathf.Min(height, top * .998f);
                    float u = x - apex * t;
                    float w = 1f - u - t;
                    Vector3 n = w * a.normal + u * b.normal + t * c.normal;
                    samples.Add(new Sample {
                        position = w * a.position + u * b.position + t * c.position,
                        normal = n.sqrMagnitude > .0001f ? n.normalized : faceNormal
                    });
                }
            }
        }

        // Keep baseline data only for renderers that actually move. Derive every
        // update from that baseline so parking and restoring cannot accumulate drift.
        public bool TryRefitTransforms(IReadOnlyList<Renderer> moved)
        {
            movedBatches.Clear();
            foreach (var renderer in moved)
            {
                if (!renderer || !sources.TryGetValue(renderer, out var source)) return false;
                var delta = renderer.localToWorldMatrix * source.original.inverse;
                Vector3 right = delta.MultiplyVector(Vector3.right);
                Vector3 up = delta.MultiplyVector(Vector3.up);
                Vector3 forward = delta.MultiplyVector(Vector3.forward);
                if (Mathf.Abs(right.sqrMagnitude - 1) > .0001f || Mathf.Abs(up.sqrMagnitude - 1) > .0001f ||
                    Mathf.Abs(forward.sqrMagnitude - 1) > .0001f || Mathf.Abs(Vector3.Dot(right, up)) > .0001f ||
                    Mathf.Abs(Vector3.Dot(right, forward)) > .0001f || Mathf.Abs(Vector3.Dot(up, forward)) > .0001f ||
                    delta.determinant < 0f) return false;
            }
            foreach (var renderer in moved)
            {
                Source source = sources[renderer];
                if (source.count == 0) continue;
                var delta = renderer.localToWorldMatrix * source.original.inverse;
                source.initial ??= samples.GetRange(source.start, source.count).ToArray();
                for (int i = 0; i < source.count; i++)
                {
                    Sample s = source.initial[i];
                    s.position = delta.MultiplyPoint3x4(s.position);
                    s.normal = delta.MultiplyVector(s.normal).normalized;
                    samples[source.start + i] = s;
                }
                Samples.SetData(samples, source.start, source.start, source.count);
                foreach (var span in source.spans)
                {
                    span.initial ??= span.batch.CopyPositions(span.start, span.count);
                    span.batch.MoveRange(span.start, span.initial, delta);
                    movedBatches.Add(span.batch);
                }
            }
            foreach (var batch in movedBatches) batch.UploadPositions();
            return true;
        }

#if UNITY_EDITOR
        // Measurement only. Intersect the displayed triangles and use the same chart
        // coordinates and four taps as the fragment shader, before receiver albedo.
        private readonly List<Vector4> chartScratch = new List<Vector4>();
        public bool MeasureSurface(Vector3 point, Vector3 normal, Vector4[] light, out Vector3 position,
            out Vector3 faceNormal, out Vector3 irradiance)
        {
            normal.Normalize();
            Ray ray = new Ray(point + normal * .02f, -normal);
            float distance = .52f;
            Batch selected = null;
            int vertex = -1;
            Vector2 bary = default;
            faceNormal = position = irradiance = default;
            foreach (var batch in batches.Values)
            {
                if (!batch.Hit(ray, ref distance, out int v, out Vector2 b, out Vector3 n)) continue;
                vertex = v; bary = b; faceNormal = n; selected = batch;
            }
            if (selected == null) return false;
            position = ray.GetPoint(distance);
            irradiance = selected.Interpolate(vertex, bary, light, chartScratch);
            return true;
        }
#endif

        public void Display(ComputeBuffer irradiance, bool enabled)
        {
            if (!enabled || irradiance == null) return;
            properties.SetBuffer("_SurfaceLighting", irradiance);
            foreach (var batch in batches.Values) batch.Draw(properties);
        }

        public void Dispose()
        {
            Samples?.Release();
            Samples = null;
            foreach (var batch in batches.Values) batch.Dispose();
            batches.Clear();
            samples.Clear();
        }
    }
}

