#ifndef DYNAMIC_GI_COMMON_INCLUDED
#define DYNAMIC_GI_COMMON_INCLUDED

// Shared data layout, BVH traversal and probe sampling for the dynamic GI system.

#define GI_MAX_CASCADES 4
#define GI_IRR_TILE 8      // 6x6 interior + 1 texel border
#define GI_IRR_INNER 6
#define GI_DIST_TILE 12    // 10x10 interior + 1 texel border
#define GI_DIST_INNER 10
#define GI_PI 3.14159265

struct GIBvhNode { float3 bmin; int leftFirst; float3 bmax; int count; };
struct GITriangle { float3 v0; int material; float3 e1; float pad0; float3 e2; float pad1; };
struct GIInstance
{
    float4 w2o0, w2o1, w2o2;   // world -> object rows
    int nodeOffset;            // root BLAS node index (absolute, in _GINodes)
    int triOffset;             // first triangle (absolute)
    int materialOffset;        // first material slot (absolute)
    float winding;             // +1, or -1 when negative scale flips the winding
};


// Probe grid parameters (one entry per cascade)
float4 _GICascadeOrigin[GI_MAX_CASCADES];   // xyz origin, w spacing
float4 _GICascadeCounts[GI_MAX_CASCADES];   // xyz counts, w first probe index
int _GICascadeCount;
int _GIProbeTotal;
int _GITilesPerRow;
float4 _GIIrradianceAtlasSize;  // w, h, 1/w, 1/h
float4 _GIDistanceAtlasSize;
float4 _GIBias;                 // x normal bias (in spacing units), y view bias (in spacing units), z cheb power, w sample energy
StructuredBuffer<float4> _GIProbeData;      // xyz relocation offset, w state (1 active, 0 inactive)
Texture2D<float4> _GIIrradianceAtlas;
Texture2D<float2> _GIDistanceAtlas;
SamplerState sampler_linear_clamp_GI;

float SignNotZero(float v) { return v >= 0 ? 1.0 : -1.0; }
float2 SignNotZero2(float2 v) { return float2(SignNotZero(v.x), SignNotZero(v.y)); }

float2 GIOctEncode(float3 n)
{
    float l1 = abs(n.x) + abs(n.y) + abs(n.z);
    float2 r = n.xy / max(l1, 1e-6);
    if (n.z < 0) r = (1.0 - abs(r.yx)) * SignNotZero2(r.xy);
    return r;
}

float3 GIOctDecode(float2 o)
{
    float3 n = float3(o.x, o.y, 1.0 - abs(o.x) - abs(o.y));
    if (n.z < 0) n.xy = (1.0 - abs(n.yx)) * SignNotZero2(n.xy);
    return normalize(n);
}

int3 GIProbeCoord(int localIndex, int3 counts)
{
    int x = localIndex % counts.x;
    int y = (localIndex / counts.x) % counts.y;
    int z = localIndex / (counts.x * counts.y);
    return int3(x, y, z);
}

int GIProbeCascade(int probeIndex)
{
    int c = 0;
    [unroll] for (int i = 1; i < GI_MAX_CASCADES; i++)
        if (i < _GICascadeCount && probeIndex >= (int)_GICascadeCounts[i].w) c = i;
    return c;
}

float3 GIProbeGridPosition(int probeIndex, out int cascade)
{
    cascade = GIProbeCascade(probeIndex);
    int3 counts = (int3)_GICascadeCounts[cascade].xyz;
    int3 coord = GIProbeCoord(probeIndex - (int)_GICascadeCounts[cascade].w, counts);
    return _GICascadeOrigin[cascade].xyz + coord * _GICascadeOrigin[cascade].w;
}

float2 GITileUV(int probeIndex, float3 dir, int tile, int inner, float4 atlasSize)
{
    int2 tileCoord = int2(probeIndex % _GITilesPerRow, probeIndex / _GITilesPerRow);
    float2 oct = GIOctEncode(dir) * 0.5 + 0.5;
    float2 texel = tileCoord * tile + 1.0 + oct * inner;
    return texel * atlasSize.zw;
}

// ---------------------------------------------------------------------------------------------
// Probe sampling (DDGI style: trilinear * backface * Chebyshev visibility weights)
// ---------------------------------------------------------------------------------------------
float3 GISampleCascade(int cascade, float3 worldPos, float3 normal, float3 toViewer, out float weightSum)
{
    float spacing = _GICascadeOrigin[cascade].w;
    int3 counts = (int3)_GICascadeCounts[cascade].xyz;
    int first = (int)_GICascadeCounts[cascade].w;
    float3 biased = worldPos + (normal * _GIBias.x + toViewer * _GIBias.y) * spacing;
    float3 gridF = (biased - _GICascadeOrigin[cascade].xyz) / spacing;
    int3 base = clamp((int3)floor(gridF), 0, counts - 2);
    float3 alpha = saturate(gridF - base);

    float3 sum = 0;
    weightSum = 0;
    [loop] for (int i = 0; i < 8; i++)
    {
        int3 offs = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int3 coord = base + offs;
        int probe = first + coord.x + coord.y * counts.x + coord.z * counts.x * counts.y;
        float4 pdata = _GIProbeData[probe];
        if (pdata.w < 0.5) continue;
        float3 probePos = _GICascadeOrigin[cascade].xyz + coord * spacing + pdata.xyz;

        float3 trilinear = lerp(1.0 - alpha, alpha, offs);
        float w = trilinear.x * trilinear.y * trilinear.z;

        // Smooth backface weight: probes behind the surface contribute little.
        float3 dirToProbe = normalize(probePos - worldPos);
        float wrap = (dot(dirToProbe, normal) + 1.0) * 0.5;
        float wb = wrap * wrap + 0.05;

        // Chebyshev visibility against the probe's mean / mean^2 distance.
        float3 probeToPoint = biased - probePos;
        float dist = length(probeToPoint);
        float3 dirFromProbe = probeToPoint / max(dist, 1e-4);
        float2 moments = _GIDistanceAtlas.SampleLevel(sampler_linear_clamp_GI, GITileUV(probe, dirFromProbe, GI_DIST_TILE, GI_DIST_INNER, _GIDistanceAtlasSize), 0);
        float cheb = 1.0;
        if (dist > moments.x)
        {
            float variance = abs(moments.y - moments.x * moments.x);
            float d = dist - moments.x;
            cheb = variance / (variance + d * d);
            cheb = pow(max(cheb, 0.0), _GIBias.z);
        }
        float wv = max(cheb, 0.002);

        float weight = wb * wv;
        // Crush tiny weights so nearly occluded probes cannot leak through thin walls.
        const float crush = 0.2;
        if (weight < crush) weight *= weight * weight / (crush * crush);
        weight *= w;

        float3 irr = _GIIrradianceAtlas.SampleLevel(sampler_linear_clamp_GI, GITileUV(probe, normal, GI_IRR_TILE, GI_IRR_INNER, _GIIrradianceAtlasSize), 0).rgb;
        sum += irr * weight;
        weightSum += weight;
    }
    return weightSum > 1e-6 ? sum / weightSum : 0;
}

// Returns cosine-weighted mean incident radiance (= irradiance / pi) in URP light units.
float3 GISampleIrradiance(float3 worldPos, float3 normal, float3 toViewer)
{
    float3 result = 0;
    float remaining = 1.0;
    [loop] for (int c = 0; c < _GICascadeCount; c++)
    {
        float spacing = _GICascadeOrigin[c].w;
        float3 lo = _GICascadeOrigin[c].xyz;
        float3 hi = lo + (_GICascadeCounts[c].xyz - 1) * spacing;
        // Distance (in spacing units) to the inside border; blend over one cell near the border.
        float3 dLo = (worldPos - lo) / spacing, dHi = (hi - worldPos) / spacing;
        float edge = min(min(min(dLo.x, dLo.y), dLo.z), min(min(dHi.x, dHi.y), dHi.z));
        bool last = c == _GICascadeCount - 1;
        float cover = last ? 1.0 : saturate(edge - 0.5);
        if (cover <= 0) continue;
        float ws;
        float3 irr = GISampleCascade(c, worldPos, normal, toViewer, ws);
        if (ws <= 1e-6) continue;
        result += irr * cover * remaining;
        remaining *= 1.0 - cover;
        if (remaining <= 1e-3) break;
    }
    return result;
}

#endif
