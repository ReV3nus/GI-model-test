# 可复查的代码依据

以下代码来自标明的 Git 提交，不代表已在额外场景中完成视觉验证。行号对应该提交。

## eval1 / eb9b64e / Assets/DynamicGI/Shaders/Resources/DynamicGITrace.compute:126

太阳反弹的归一化：太阳项显式除以 π，天空逃逸项没有相同缩放；需与 URP 光源单位一起核对。

```text
126: float3 Indirect(float3 positionWS, float3 normal, float3 initialAlbedo, inout uint rng, bool skipPrimarySky)
127: {
128:     float3 result = 0, throughput = initialAlbedo;
129:     for (int bounce = 0; bounce < _BounceCount; bounce++)
130:     {
131:         float3 direction = CosineHemisphere(normal, rng);
132:         float3 origin = positionWS + normal * _RayBias;
133:         Hit hit;
134:         if (!Trace(origin, direction, _MaxDistance, false, hit))
135:         {
136:             if (bounce > 0 || !skipPrimarySky)
137:                 result += throughput * _Sky.SampleLevel(sampler_Sky, direction, 0).rgb * _SkyIntensity;
138:             break;
139:         }
140:         positionWS = origin + direction * hit.t;
141:         normal = SurfaceNormal(hit, direction);
142:         throughput *= _Triangles[hit.triIndex].albedo;
143:         // No solar NEE at the primary surface: URP already renders that light.
144:         // At a secondary hit this is actual reflected radiance rho * E / pi.
145:         float cosine = max(0, dot(normal, _SunDirection));
146:         if (cosine > 0 && any(_SunRadiance > 0))
147:         {
148:             Hit shadow;
149:             if (!Trace(positionWS + normal * _RayBias, _SunDirection, _MaxDistance, true, shadow))
150:                 result += throughput * _SunRadiance * (cosine / 3.14159265359);
151:         }
152:     }
```

## eval1 / eb9b64e / Assets/DynamicGI/Runtime/DynamicGIController.cs:138

相机视图或投影改变时清空图像历史。

```text
138:             int light = LightingSignature();
139:             if (light != lightSignature) { lightSignature = light; Invalidate("lighting changed"); }
140:             var view = TargetCamera.cameraToWorldMatrix;
141:             var projection = TargetCamera.projectionMatrix;
142:             if (view != previousView || projection != previousProjection)
143:             { previousView = view; previousProjection = projection; Invalidate("camera changed"); }
144:             int quality = SamplesPerPixel * 17 + BounceCount * 123 + RayBias.GetHashCode() + MaxDistance.GetHashCode();
145:             if (quality != qualitySignature) { qualitySignature = quality; Invalidate("quality changed"); }
```

## eval2 / be724f5 / Assets/GIEvaluation/Runtime/GISurfaceReceivers.cs:265

各三角形独立设置接收采样网格；最终插值不能自动保证跨三角形连续。

```text
265:             if (bc > ab && bc >= ca) (a, b, c) = (b, c, a);
266:             else if (ca > ab) (a, b, c) = (c, a, b);
267:             var cross = Vector3.Cross(b.position - a.position, c.position - a.position);
268:             float twiceArea = cross.magnitude;
269:             if (twiceArea < 1e-10f) return;
270:             float length = Vector3.Distance(a.position, b.position);
271:             int columns = Mathf.Clamp(Mathf.CeilToInt(length / spacing), 1, 128);
272:             // A slender triangle's apex can lie close to the line of its base
273:             // yet far along that line. Subdivide along the base, too.
274:             int rows = Mathf.Clamp(Mathf.CeilToInt(twiceArea / (length * spacing)), 1, 128);
275:             float apex = Mathf.Clamp01(Vector3.Dot(c.position - a.position, b.position - a.position) / (length * length));
276:             var address = new Vector4(samples.Count, columns, rows, apex);
277:             batch.Add(a.position, a.uv, Vector2.zero, address);
278:             batch.Add(b.position, b.uv, Vector2.right, address);
279:             batch.Add(c.position, c.uv, Vector2.up, address);
280:             var faceNormal = cross / twiceArea;
281: 
282:             for (int row = 0; row < rows; row++)
283:             {
284:                 float height = (row + .5f) / rows;
285:                 for (int col = 0; col < columns; col++)
286:                 {
287:                     float x = (col + .5f) / columns;
288:                     float top = x < apex ? x / apex : (1f - x) / (1f - apex);
289:                     // Keep the regular grid orthogonal in world space. Only samples outside
290:                     // this triangle are clamped; stretching every row into the apex
291:                     // would turn Monte Carlo variation into long fan-shaped streaks.
292:                     float t = rows == 1 ? top * .5f : Mathf.Min(height, top * .998f);
293:                     float u = x - apex * t;
294:                     float w = 1f - u - t;
295:                     Vector3 n = w * a.normal + u * b.normal + t * c.normal;
296:                     samples.Add(new Sample {
297:                         position = w * a.position + u * b.position + t * c.position,
```

## eval3 / 6883c64 / Assets/DynamicGI/Runtime/DynamicGISystem.cs:51

探针时间历史与变化加速参数。

```text
51:         [Header("Temporal history")]
52:         [Range(0, 0.995f)] public float hysteresis = 0.97f;
53:         [Range(0, 0.99f)] public float largeChangeHysteresis = 0.55f;
54:         [Range(0, 0.99f)] public float globalBoostHysteresis = 0.85f;
55:         [Range(0, 0.99f)] public float dirtyRegionHysteresis = 0.3f;
56:         [Min(1)] public int boostFrames = 45;
57:         [Min(0)] public float dirtyRegionPadding = 2.5f;
58:         [Range(0.05f, 1f)] public float changeThreshold = 0.3f;
59:         public bool clearHistoryOnSceneReset = false;
60: 
61:         [Header("Sampling bias (spacing units)")]
62:         [Range(0, 1)] public float normalBias = 0.25f;
63:         [Range(0, 1)] public float viewBias = 0.1f;
64:         [Range(1, 8)] public float chebyshevPower = 3f;
65: 
```

## eval4 / 83640ee / Assets/DynamicGI/Resources/DynamicGIComposite.shader:98

无室内灯时，以相机空气体素的室外连通性决定整幅 GI 是否显示。

```text
98:                 float3 viewDir = normalize(positionWS - GetCameraPositionWS());
99:                 float maxVoxel = max(_VolumeVoxelSize.x, max(_VolumeVoxelSize.y, _VolumeVoxelSize.z));
100:                 float stepBack = maxVoxel * 0.6;
101: 
102:                 // Enclosure gate. When the interior lamps are off and the camera
103:                 // itself is in air that is not connected to the outdoor volume,
104:                 // no outdoor GI can physically be present at the camera. This
105:                 // removes residual voxel-pinhole leakage in sealed rooms without
106:                 // creating per-pixel gate halos around thin geometry.
107:                 if (_GateEnclosed > 0.5)
108:                 {
109:                     int3 camV = GIVoxelIndex(_CameraPositionWS.xyz);
110:                     float outside = 1.0;
111:                     if (GIInVolume(camV)) outside = _OutsideMask.Load(int4(camV, 0));
112:                     if (outside < 0.001)
113:                     {
114:                         if (_ViewMode == 0) return source;
115:                         return float4(0, 0, 0, 1);
116:                     }
117:                 }
118: 
```

## eval4 / 83640ee / Assets/DynamicGI/Runtime/DynamicGIController.cs:661

真实纯黑反照率被替换为 0.5 灰色，是普通材质输入的一项缺陷。

```text
661:                     Material material = materials != null && materials.Length > 0 ? materials[Mathf.Min(s, materials.Length - 1)] : null;
662:                     if (material == null || material.shader == null) continue;
663:                     if (material.renderQueue >= 3000) continue;
664:                     Vector3 albedo = GetMaterialAlbedo(material);
665:                     if (albedo.sqrMagnitude < 1e-8f) albedo = new Vector3(0.5f, 0.5f, 0.5f);
666:                     int[] indices = mesh.GetTriangles(s);
667:                     for (int i = 0; i + 2 < indices.Length; i += 3)
668:                     {
669:                         Vector3 p0 = matrix.MultiplyPoint3x4(vertices[indices[i]]);
670:                         Vector3 p1 = matrix.MultiplyPoint3x4(vertices[indices[i + 1]]);
671:                         Vector3 p2 = matrix.MultiplyPoint3x4(vertices[indices[i + 2]]);
672:                         Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
673:                         if (n.sqrMagnitude < 1e-10f) continue;
674:                         n.Normalize();
675:                         var bmin = Vector3.Min(p0, Vector3.Min(p1, p2));
```

## URP 17.5.0 / ShaderLibrary/Lighting.hlsl:48

```text
48: half3 LightingPhysicallyBased(BRDFData brdfData, BRDFData brdfDataClearCoat,
49:     half3 lightColor, half3 lightDirectionWS, float lightAttenuation,
50:     half3 normalWS, half3 viewDirectionWS,
51:     half clearCoatMask, bool specularHighlightsOff)
52: {
53: #if (UNITY_PLATFORM_META_QUEST)
54:     half NdotL = dot(normalWS, lightDirectionWS);
55: #else
56:     half NdotL = saturate(dot(normalWS, lightDirectionWS));
57: #endif
58: 
59: #if (UNITY_PLATFORM_META_QUEST)
60:     [branch]
61:     if (NdotL > 0.0)
62:     {    
63:         half3 radiance = lightColor * (lightAttenuation * saturate(NdotL));
64: #else
65:         half3 radiance = lightColor * (lightAttenuation * NdotL);
66: #endif
67:         half3 brdf = brdfData.diffuse;
68: #ifndef _SPECULARHIGHLIGHTS_OFF
69:         [branch]
70:         if (!specularHighlightsOff)
71:         {
72:             brdf += brdfData.specular * DirectBRDFSpecular(brdfData, normalWS, lightDirectionWS, viewDirectionWS);
73: 
74: #if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
```

## URP 17.5.0 / ShaderLibrary/BRDF.hlsl:79

```text
79: }
80: 
81: // Initialize BRDFData for material, managing both specular and metallic setup using shader keyword _SPECULAR_SETUP.
82: inline void InitializeBRDFData(half3 albedo, half metallic, half3 specular, half smoothness, inout half alpha, out BRDFData outBRDFData)
83: {
84: #ifdef _SPECULAR_SETUP
85:     half reflectivity = ReflectivitySpecular(specular);
86:     half oneMinusReflectivity = half(1.0) - reflectivity;
87:     half3 brdfDiffuse = albedo * oneMinusReflectivity;
88:     half3 brdfSpecular = specular;
89: #else
90:     half oneMinusReflectivity = OneMinusReflectivityMetallic(metallic);
91:     half reflectivity = half(1.0) - oneMinusReflectivity;
92:     half3 brdfDiffuse = albedo * oneMinusReflectivity;
93:     half3 brdfSpecular = lerp(kDielectricSpec.rgb, albedo, metallic);
94: #endif
95: 
96:     InitializeBRDFDataDirect(albedo, brdfDiffuse, brdfSpecular, reflectivity, oneMinusReflectivity, smoothness, alpha, outBRDFData);
97: }
```
