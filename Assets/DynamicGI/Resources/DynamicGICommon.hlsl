#ifndef DYNAMIC_GI_COMMON_INCLUDED
#define DYNAMIC_GI_COMMON_INCLUDED

// Shared volume description and small math helpers for the Dynamic GI system.
// The volume is a world-space axis aligned grid of surface voxels. Only voxels
// touched by opaque geometry are "occupied"; the rest is empty air.

float3 _VolumeMin;
float3 _VolumeVoxelSize;   // world size of one voxel on each axis
int3   _VolumeDims;

Texture3D<float4> _VoxelAlbedo;   // rgb = linear diffuse albedo, a = occupancy
Texture3D<float4> _VoxelNormal;   // xyz = surface normal

#define GI_PI 3.14159265358979323846

float3 GIVolumeCenter(int3 v)
{
    return _VolumeMin + (float3(v) + 0.5) * _VolumeVoxelSize;
}

float3 GIWorldToVoxel(float3 p)
{
    return (p - _VolumeMin) / _VolumeVoxelSize;
}

int3 GIVoxelIndex(float3 p)
{
    return (int3)floor(GIWorldToVoxel(p));
}

bool GIInVolume(int3 v)
{
    return all(v >= 0) && all(v < _VolumeDims);
}

float3 GIMaxVoxelSize()
{
    return max(_VolumeVoxelSize, float3(1e-5, 1e-5, 1e-5));
}

float GIMaxVoxelExtent()
{
    return max(GIMaxVoxelSize().x, max(GIMaxVoxelSize().y, GIMaxVoxelSize().z));
}

uint GIHash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352d;
    x ^= x >> 15; x *= 0x846ca68b;
    x ^= x >> 16;
    return x;
}

float GIHash01(uint x)
{
    return (GIHash(x) & 0x00ffffffu) / 16777216.0;
}

void GIBasis(float3 n, out float3 t, out float3 b)
{
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float bb = n.x * n.y * a;
    t = float3(1.0 + s * n.x * n.x * a, s * bb, -s * n.x);
    b = float3(bb, s + n.y * n.y * a, -n.y);
}

// Cosine weighted direction in the hemisphere around n.
// (i + 0.5) / count and a golden-ratio sequence give a stable, low discrepancy
// set; rot decorrelates neighbouring voxels without introducing temporal noise.
float3 GICosineDirection(float3 n, uint i, uint count, float rot)
{
    float u1 = (float(i) + 0.5) / float(count);
    float u2 = frac(float(i) * 0.6180339887498948 + rot);
    float r = sqrt(u1);
    float phi = 2.0 * GI_PI * u2;
    float3 t, b;
    GIBasis(n, t, b);
    float3 d = t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u1));
    return normalize(d);
}

// Distance between a point and a triangle (Ericson, Real-Time Collision Detection).
float GIPointTriangleDistance(float3 p, float3 a, float3 b, float3 c)
{
    float3 ab = b - a;
    float3 ac = c - a;
    float3 ap = p - a;
    float d1 = dot(ab, ap);
    float d2 = dot(ac, ap);
    if (d1 <= 0.0 && d2 <= 0.0) return length(ap);

    float3 bp = p - b;
    float d3 = dot(ab, bp);
    float d4 = dot(ac, bp);
    if (d3 >= 0.0 && d4 <= d3) return length(bp);

    float vc = d1 * d4 - d3 * d2;
    if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
    {
        float v = d1 / (d1 - d3);
        return length(p - (a + v * ab));
    }

    float3 cp = p - c;
    float d5 = dot(ab, cp);
    float d6 = dot(ac, cp);
    if (d6 >= 0.0 && d5 <= d6) return length(cp);

    float vb = d5 * d2 - d1 * d6;
    if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
    {
        float w = d2 / (d2 - d6);
        return length(p - (a + w * ac));
    }

    float va = d3 * d6 - d5 * d4;
    if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
    {
        float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
        return length(p - (b + w * (c - b)));
    }

    float denom = 1.0 / (va + vb + vc);
    float v = vb * denom;
    float w = vc * denom;
    return length(p - (a + ab * v + ac * w));
}

// Amanatides & Woo voxel traversal. The ray starts at origin + dir * startT and
// the voxel containing that point is not tested, which keeps a surface from
// hitting its own (slightly dilated) voxel shell.
bool GITraceVoxels(float3 origin, float3 dir, float startT, float maxDist,
                   int maxSteps, out int3 hitVoxel, out float hitT)
{
    hitVoxel = int3(0, 0, 0);
    hitT = 0.0;
    float t = startT;
    float3 p = origin + dir * t;
    int3 v = GIVoxelIndex(p);
    if (!GIInVolume(v)) return false;

    int3 step;
    step.x = dir.x >= 0.0 ? 1 : -1;
    step.y = dir.y >= 0.0 ? 1 : -1;
    step.z = dir.z >= 0.0 ? 1 : -1;

    float3 tMax = float3(1e30, 1e30, 1e30);
    float3 tDelta = float3(1e30, 1e30, 1e30);

    [unroll] for (int a = 0; a < 3; a++)
    {
        float d = dir[a];
        if (abs(d) > 1e-7)
        {
            float boundary = _VolumeMin[a] + (float(v[a]) + (step[a] > 0 ? 1.0 : 0.0)) * _VolumeVoxelSize[a];
            tMax[a] = t + (boundary - p[a]) / d;
            tDelta[a] = _VolumeVoxelSize[a] / abs(d);
        }
    }

    [loop] for (int i = 0; i < maxSteps; i++)
    {
        if (tMax.x < tMax.y)
        {
            if (tMax.x < tMax.z) { v.x += step.x; t = tMax.x; tMax.x += tDelta.x; }
            else                 { v.z += step.z; t = tMax.z; tMax.z += tDelta.z; }
        }
        else
        {
            if (tMax.y < tMax.z) { v.y += step.y; t = tMax.y; tMax.y += tDelta.y; }
            else                 { v.z += step.z; t = tMax.z; tMax.z += tDelta.z; }
        }

        if (!GIInVolume(v)) return false;
        if (t > maxDist) return false;

        float4 a = _VoxelAlbedo.Load(int4(v, 0));
        if (a.a > 0.5)
        {
            hitVoxel = v;
            hitT = t;
            return true;
        }
    }
    return false;
}

bool GIOccluded(float3 origin, float3 dir, float startT, float maxDist)
{
    int3 hit;
    float hitT;
    return GITraceVoxels(origin, dir, startT, maxDist, 512, hit, hitT);
}

#endif // DYNAMIC_GI_COMMON_INCLUDED
