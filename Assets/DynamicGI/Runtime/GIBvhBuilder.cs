using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DynamicGI
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GIBvhNode
    {
        public Vector3 bmin; public int leftFirst;   // leaf: first primitive, inner: left child index (right = left + 1)
        public Vector3 bmax; public int count;       // > 0 for leaves
        public const int Stride = 32;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GITriangle
    {
        public Vector3 v0; public int material;
        public Vector3 e1; public float pad0;
        public Vector3 e2; public float pad1;
        public const int Stride = 48;
    }

    // Binned SAH BVH over axis-aligned primitive bounds. Used for per-mesh BLAS (triangles)
    // and for the scene TLAS (instance bounds). Primitive order is returned so callers can reorder.
    public static class GIBvhBuilder
    {
        private const int Bins = 12;

        public static void Build(Vector3[] pmin, Vector3[] pmax, int maxLeaf, List<GIBvhNode> nodes, out int[] order)
        {
            int n = pmin.Length;
            order = new int[n];
            var centroid = new Vector3[n];
            for (int i = 0; i < n; i++) { order[i] = i; centroid[i] = (pmin[i] + pmax[i]) * 0.5f; }
            nodes.Clear();
            if (n == 0)
            {
                nodes.Add(new GIBvhNode { bmin = Vector3.one * 1e30f, bmax = Vector3.one * -1e30f, leftFirst = 0, count = 0 });
                return;
            }
            nodes.Add(default);
            var stack = new Stack<(int node, int first, int count)>();
            stack.Push((0, 0, n));
            var binMin = new Vector3[Bins]; var binMax = new Vector3[Bins]; var binCount = new int[Bins];
            while (stack.Count > 0)
            {
                var (ni, first, count) = stack.Pop();
                Vector3 bmin = Vector3.one * float.MaxValue, bmax = Vector3.one * float.MinValue;
                Vector3 cmin = bmin, cmax = bmax;
                for (int i = first; i < first + count; i++)
                {
                    int p = order[i];
                    bmin = Vector3.Min(bmin, pmin[p]); bmax = Vector3.Max(bmax, pmax[p]);
                    cmin = Vector3.Min(cmin, centroid[p]); cmax = Vector3.Max(cmax, centroid[p]);
                }
                if (count <= maxLeaf)
                {
                    nodes[ni] = new GIBvhNode { bmin = bmin, bmax = bmax, leftFirst = first, count = count };
                    continue;
                }
                // Binned SAH split search.
                float bestCost = float.MaxValue; int bestAxis = -1, bestSplit = -1;
                for (int axis = 0; axis < 3; axis++)
                {
                    float lo = cmin[axis], hi = cmax[axis];
                    if (hi - lo < 1e-7f) continue;
                    for (int b = 0; b < Bins; b++) { binMin[b] = Vector3.one * float.MaxValue; binMax[b] = Vector3.one * float.MinValue; binCount[b] = 0; }
                    float scale = Bins / (hi - lo);
                    for (int i = first; i < first + count; i++)
                    {
                        int p = order[i];
                        int b = Mathf.Min(Bins - 1, (int)((centroid[p][axis] - lo) * scale));
                        binCount[b]++; binMin[b] = Vector3.Min(binMin[b], pmin[p]); binMax[b] = Vector3.Max(binMax[b], pmax[p]);
                    }
                    for (int split = 1; split < Bins; split++)
                    {
                        Vector3 lmin = Vector3.one * float.MaxValue, lmax = Vector3.one * float.MinValue, rmin = lmin, rmax = lmax;
                        int lc = 0, rc = 0;
                        for (int b = 0; b < split; b++) if (binCount[b] > 0) { lc += binCount[b]; lmin = Vector3.Min(lmin, binMin[b]); lmax = Vector3.Max(lmax, binMax[b]); }
                        for (int b = split; b < Bins; b++) if (binCount[b] > 0) { rc += binCount[b]; rmin = Vector3.Min(rmin, binMin[b]); rmax = Vector3.Max(rmax, binMax[b]); }
                        if (lc == 0 || rc == 0) continue;
                        float cost = lc * Area(lmin, lmax) + rc * Area(rmin, rmax);
                        if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestSplit = split; }
                    }
                }
                int mid;
                if (bestAxis < 0 || bestCost >= count * Area(bmin, bmax))
                {
                    if (count <= maxLeaf * 4 && bestAxis < 0)
                    {
                        nodes[ni] = new GIBvhNode { bmin = bmin, bmax = bmax, leftFirst = first, count = count };
                        continue;
                    }
                    // Fallback: median split on the largest centroid axis.
                    int axis = LargestAxis(cmax - cmin);
                    Array.Sort(order, first, count, Comparer<int>.Create((a, b) => centroid[a][axis].CompareTo(centroid[b][axis])));
                    mid = first + count / 2;
                }
                else
                {
                    float lo = cmin[bestAxis], scale = Bins / (cmax[bestAxis] - lo);
                    int i = first, j = first + count - 1;
                    while (i <= j)
                    {
                        int b = Mathf.Min(Bins - 1, (int)((centroid[order[i]][bestAxis] - lo) * scale));
                        if (b < bestSplit) i++;
                        else { (order[i], order[j]) = (order[j], order[i]); j--; }
                    }
                    mid = i;
                    if (mid == first || mid == first + count) mid = first + count / 2;
                }
                int left = nodes.Count;
                nodes.Add(default); nodes.Add(default);
                nodes[ni] = new GIBvhNode { bmin = bmin, bmax = bmax, leftFirst = left, count = 0 };
                stack.Push((left, first, mid - first));
                stack.Push((left + 1, mid, first + count - mid));
            }
        }

        private static float Area(Vector3 mn, Vector3 mx)
        {
            Vector3 d = mx - mn;
            if (d.x < 0) return 0;
            return d.x * d.y + d.y * d.z + d.z * d.x;
        }

        private static int LargestAxis(Vector3 d) => d.x > d.y ? (d.x > d.z ? 0 : 2) : (d.y > d.z ? 1 : 2);
    }
}
