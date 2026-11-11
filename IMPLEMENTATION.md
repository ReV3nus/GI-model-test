# 可破坏环境中的实时动态全局光照 — 实现说明

本文件说明本工程中实现的动态漫反射 GI 系统：算法、接入方式、动态更新、实测性能、测试完成情况及已知限制。实现位于 `Assets/DynamicGI/`，可直接在现有 `GI_TestScene` 中运行。

---

## 1. 系统概览

| 部分 | 文件 | 作用 |
|---|---|---|
| 运行时控制器 | `Assets/DynamicGI/Runtime/DynamicGIController.cs` | 体素化、直接光注入、漫反射迭代、动态更新、参数与调试 UI |
| 连通性判定 | `Assets/DynamicGI/Runtime/DynamicGIController.Connectivity.cs` | 保守遮挡体回读、CPU 泛洪填充、室外空气掩码、密闭性门控 |
| URP 接入 | `Assets/DynamicGI/Runtime/DynamicGIRendererFeature.cs` | RenderGraph 全屏合成 pass，加入 GI |
| 体素化 | `Assets/DynamicGI/Resources/DynamicGIVoxelize.compute` | 三角形表面壳层体素化、邻域遮挡膨胀 |
| 直接光注入 | `Assets/DynamicGI/Resources/DynamicGIInject.compute` | 太阳/天空/室内灯的可见性与源项 |
| 漫反射传播 | `Assets/DynamicGI/Resources/DynamicGIPropagate.compute` | 余弦采样一次反弹、辐照度场空间滤波 |
| 屏幕合成 | `Assets/DynamicGI/Resources/DynamicGIComposite.shader` | 从深度重建表面、采样 GI、GI 开关/间接光/伪彩显示 |
| 公共 HLSL | `Assets/DynamicGI/Resources/DynamicGICommon.hlsl` | 体素坐标、DDA、余弦方向、点-三角形距离 |
| 验证工具 | `Assets/DynamicGI/Editor/DynamicGIAutomation.cs` | 菜单/命令行自动化：七步骤、B/E/I/V、截图、计时、JSON |
| 现有文件修改 | `Assets/Settings/PC_Renderer.asset` | 增加 `Dynamic GI` RendererFeature 子资源 |
| 现有文件修改 | `Assets/GIEvaluation/Runtime/GIBreachScenario.cs` | 仅更新 UI 状态文字、键位提示和 P 截图的 GI 元数据；未改变任何测试条件 |

系统在场景开始播放时由 `DynamicGIBootstrap`（`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`）自动挂到当前 `GIBreachScenario` 根对象上，不需要手工改场景或重建预制体。

### 运行

1. 打开 `Assets/GIEvaluation/Scenes/GI_TestScene.unity`。
2. 用 `GI Evaluation > Start Interactive Test` 或直接 Play。
3. 原有 1–7、B、E、I、V、R、T、P 键全部保留。新增：
   - `G`：GI 开/关（独立开关，不改变灯光、材质、曝光或相机）。
   - `O`：循环显示模式：Composite（正常加光）→ AllGI（仅显示 GI，含天空直接项）→ BouncesOnly（仅显示反弹的间接光）→ FalseColor（间接光伪彩）。
   - `H`：隐藏右上角 GI 面板。
   - 右上角面板也提供 GI 开关和显示模式按钮，并显示体素尺寸、反弹数、更新耗时、收敛状态。

---

## 2. 算法

### 2.1 光传输模型与单位

采用 **表面体素辐射度 / 辐照度场** 近似，不依赖 Unity 烘焙 GI、Light Probe、APV 或任何现成 GI 插件。

对每个有几何的表面体素保存线性漫反射反照率 `albedo`、表面法线 `n` 和内向直接光引起的出射项：

```
S(x) = albedo(x) * ( sunColor * max(dot(n, toSun), 0) * sunVisible
                   + skyIrradiance(x, n)
                   + Σ lamps ... )
```

其中 `sunColor = light.color.linear * light.intensity`，与 URP 前向直接光的单位保持一致；`skyIrradiance` 是用余弦加权方向对天空 Cubemap 取平均（即 Unity 光照约定下的漫反射辐照度）。每一轮漫反射反弹计算接收到的间接辐照度：

```
E_{k+1}(x) = (1/N) Σ_{i=1..N} [ S(y_i) + albedo(y_i) * E_k(y_i) ]
```

`y_i` 是从 `x` 沿余弦加权方向 `d_i` 看到的第一个表面体素（DDA 遍历）。余弦加权采样使该平均正好等于 Unity/URP 光照约定下的漫反射辐照度，不需要额外的 π 因子。屏幕合成只在接收面加上：

```
indirect(x) = albedo(x) * E_N(x)
```

因此：

- **太阳直接光**仍由 URP 前向 pass 绘制，GI 只添加它的反弹，不重复叠加。
- **天空直接光**在本工程中由 GI pass 提供（场景 `RenderSettings.ambientLight` 是黑色，URP 不会另外加环境光），但它是经过体素可见性采样的天空辐照度，不是常量填光。封闭房间内天空可见度为 0，因此不会无条件变亮。
- **室内灯**的直接照明仍由 URP 绘制；GI 在灯开启时把灯的直射项注入源项，只添加其漫反射反弹。
- 只处理不透明 `MeshRenderer` + `MeshFilter`。透明、体积散射、骨骼蒙皮、镜面焦散不包含。

### 2.2 空间数据与体素化

- 世界空间体素域：中心 `(0, 2.0, 1.5)`，尺寸 `15.6 × 5.6 × 18.6 m`；默认体素 `0.1 m`，得到 `156 × 56 × 186 = 1,624,896` 个体素。域覆盖房间、屋顶、内隔墙、活动遮挡板（parked 位置在域外，移入时纳入）、家具和前方庭院；域外几何不参与 GI。域和体素大小可在 Inspector 修改。
- 体素化是“表面壳层”而不是实体填充：每个三角形在体素 AABB 内用“体素中心到三角形距离 ≤ 0.87×体素尺寸”做保守覆盖；两遍 pass 用 `InterlockedMax` 的量化距离键选出离体素中心最近的表面，写入 albedo 和法线。这样空气体素为空，光线 DDA 只会在真实表面壳层上停止。
- 反照率简化：取材质的 `_BaseColor`（线性）× `_BaseMap` 的 16×16 采样平均亮度；没有逐纹素颜色、没有金属度/高光/法线贴图参与 GI。场景共 15,010 个三角形。
- 数据（全部 3D 纹理，`RGBA16F` 除注明外）：albedo(含占用 alpha)、normal、source、skyRaw、skyBlur、indirectA、indirectB、closeness(R32Int)、occluder(ARGB32)、outside mask(R8)。约 **105.4 MiB** 纹理显存 + 2 MiB 三角形 StructuredBuffer + 1.55 MiB 室外掩码纹理；另有约 16 MB CPU 数组（遮挡体回读、泛洪队列、三角形列表）。具体值见 `Reports/DynamicGI_Final/summary.json`。

### 2.3 直接光可见性

- **太阳**：从表面体素沿 `toSun` 在保守遮挡体（表面壳层再膨胀 1 体素）上做 DDA；无遮挡才注入 `sunColor * NdotL`。使用体素遮挡而不是 URP 阴影贴图，因此软阴影半影和极薄几何的遮挡与 URP 直接光可能有细微差别。
- **天空**：以法线为轴生成 16 个余弦加权方向（黄金比例序列 + 每体素哈希旋转），逐个在保守遮挡体上做 DDA；离开体素域的射线视为看到天空，取 `skyRadiance` Cubemap 的 HDR 值并乘 `EffectiveSkyIntensity`。结果再经 3×3×3 空间滤波。
- **室内灯**：最多 8 盏点光，按 URP 的距离衰减 `smoothStep / d²`、NdotL 和体素遮挡注入。只有 `interiorLightsEnabled` 且 Light 启用的灯参与。室内灯开关状态会被记录在截图 JSON 和 `GIBreachEnvironment` 中。

### 2.4 漫反射迭代

- 初始 `E_0 = 0`；每轮对每个占用体素发 8 条余弦加权射线，命中后取命中体素的 `S + albedo*E`，平均得到新的 `E`。
- 默认 5 轮（即包含 5 次反弹链路），每帧 2 轮；每轮之后对 `E` 做两次 3×3×3 占用邻域滤波（第二次的结果写回另一张纹理，等价于约 2 体素半径的平滑），并向表面外侧的 1 体素空腔写入 halo，保证屏幕三线性采样不在体素边缘变黑。
- **没有时间累积历史**：每次几何变化重建时 `E` 清零并重新迭代；因此不存在历史拖影/逐帧累积噪声。收敛后每帧不再做计算（只有合成 pass）。

### 2.5 密闭性与漏光门控（重要）

体素分辨率、薄几何和表面壳层会产生少量“针孔”可见性误差。系统用两层基于几何连通性的门控处理，而不是按测试编号或状态硬编码：

1. GPU 把占用壳层膨胀 1 体素，写入 `occluder` 纹理和 `ComputeBuffer`；每个几何重建时回读。
2. CPU 在 6-连通网格上，从体素域边界向内部对“非遮挡空气”做泛洪填充，得到 `OutsideMask`（室外空气 = 1）。再做一次 6-邻域扩展，覆盖表面外紧邻的一层保守壳层。
3. **源门控**：只有表面法线方向 1.55 体素处的空气是室外连通时，才注入太阳/天空直接项。室内灯不受此门控。
4. **相机密闭门控**：当没有室内灯开启时，如果相机所在空气体素不是室外连通的，屏幕 pass 直接返回原图（GI 贡献为 0）。物理解释：一个与室外不连通、且没有室内光源的空间，外部太阳/天空 GI 必须为 0。
5. 这使第 1/7 步在关灯时严格为黑色；而 2–6 步只要开口让房间与室外空气连通，门控就放行，GI 正常显示。一个副作用是：近似上，窄于约 0.2 m 的缝隙会被保守遮挡膨胀判定为封闭。

门控只影响“户外 GI 是否允许出现在该空间”，不影响室内灯反弹。它不改变直接光照、材质、曝光或光源。

### 2.6 动态更新与缓存失效

- 订阅 `GeometryChanges.Changed`（Added/Removed/TransformChanged/MeshChanged/SceneReset）和 `GIBreachEnvironment.Changed`（太阳/天空/室内灯）。
- 同一帧内多个事件合并为一次更新；在 `LateUpdate` 执行，保证当前帧渲染能看到新数据。
- 几何变化触发**完整重建**：从当前活动 `MeshRenderer` 的网格、`localToWorldMatrix`、材质重新提取世界空间三角形 → 体素化 → 遮挡膨胀 → 回读 → 泛洪掩码 → 直接光注入 → 清零反弹 → 重新迭代。网格替换、对象加入/移除、变换、Reset 都自然被覆盖；不读取物体名称、状态编号或预设坐标。
- 纯光照变化只重新注入直接光并清零反弹，不重新体素化。
- 体素尺寸/域改变时销毁并重建全部纹理；三角形缓冲按需扩容；`OutsideMask` 纹理复用尺寸。
- 旧光照立即失效：第 7 步与第 1 步在同一相机/灯光/曝光下实测均值都为 0.00000；没有任何历史缓冲残留。
- 响应时间：几何重建在编辑器 batch 环境实测 CPU 约 83–113 ms（其中遮挡回读 26–52 ms、泛洪 36–50 ms），5 轮反弹分摊到帧内，状态变化到收敛的墙钟时间约 0.17–0.21 s（编辑器约 85 fps 的 update 环境）。变化后第一帧的均值与收敛均值的差 < 1%（见第 5 节）。

---

## 3. 渲染接入

- `DynamicGIRendererFeature` 通过 `Assets/Settings/PC_Renderer.asset` 的 `m_RendererFeatures` / `m_RendererFeatureMap` 注册（PC 质量等级使用的正是 PC_Renderer）。它没有需要手工填写的材质引用，shader 通过 `Resources.Load` 获取。
- 在 `RenderPassEvent.AfterRenderingOpaques` 执行：
  1. 用 RenderGraph 把当前相机颜色 blit 到同描述临时纹理（避免读写同一 attachment）。
  2. 一个 RasterPass 采样 `_CameraDepthTexture`，从深度重建世界坐标；从体素 albedo 取接收面反照率（包含 0.6 体素回退查找）；对 `indirect` 和 `skyBlur` 做三线性采样并乘接收面 albedo；按 `_ViewMode` 输出原图+GI、仅 GI、仅反弹或伪彩。
  3. 天空/远平面像素直接返回原图；NaN/Inf 防护；全屏三角形由 `Blitter.BlitTexture` 绘制。
- 该 pass 只对 Game/Scene 相机执行，跳过 Preview/Reflection 和 offscreen depth 相机。它只请求 Depth，不额外请求 Normal；不依赖 SSAO 的深度法线 pass。
- 现有 SSAO RendererFeature 仍保留，在 GI 开/关两种对照中均相同，属于固定测试条件的一部分。
- 透明物体在本 pass 之后绘制，不受 GI 加光；这是已知范围限制。

---

## 4. 参数（默认值）

| 参数 | 默认 | 说明 |
|---|---|---|
| `volumeCenter` / `volumeSize` | (0,2,1.5) / (15.6,5.6,18.6) | GI 体素域，世界空间 |
| `voxelSize` | 0.10 m | 实际由域尺寸/整数体素数得到 |
| `bounceIterations` | 5 | 漫反射反弹轮数 |
| `bouncesPerFrame` | 2 | 每帧迭代轮数 |
| `propagationDirections` | 8 | 每个表面体素的余弦方向数 |
| `skyDirections` | 16 | 天空可见性方向数 |
| `maxTraceDistance` | 14 m | 反弹/天空追踪最大距离 |
| `indirectIntensity` / `skyIntensityScale` | 1 / 1 | 调试用线性倍率（正式对照保持 1） |
| `includeInteriorLamps` | true | 室内灯是否作为 GI 源 |
| `enableGI` | true | 独立 GI 开关 |
| `viewMode` | Composite | Composite/AllGI/BouncesOnly/FalseColor |
| `screenGateEnabled` | true | 相机密闭门控（调试可关） |
| 显示分辨率/域 | 156×56×186 | 内部辐照度场，屏幕为全分辨率三线性采样 |

---

## 5. 实测条件与结果

### 5.1 条件

- **硬件/引擎**：NVIDIA GeForce RTX 4080 Laptop GPU；Unity 6000.5.4f1；Direct3D 12（level 12.2）；Windows 11（10.0.26200）；Linear 色彩空间。
- **输出**：1920×1080，相机 RenderTexture（相机 UI 不包含在截图内）；固定视图 0、固定 FOV/曝光。截图为 LDR ARGB32 sRGB 目标；交互 GameView 可能使用 HDR/后处理路径，绝对亮度可能略有差异，但本文件中的所有开/关对照均在同一截图路径下采集。
- **测试条件**：Preview fill OFF；室内灯 OFF（除 I 测试）；SunAndSky（除 E 测试）；GI 开/关只切换 `enableGI`，不改变任何场景/灯光/相机/材质参数。
- **GI 内部**：156×56×186 体素（0.10 m）；5 次反弹；8 个传播方向；16 个天空方向；2 次/帧。
- **限帧**：夹具 `GIBreachScenario.Awake` 将 `Application.targetFrameRate` 设为 30。测量时 harness 将其覆盖为 `-1`、`vSync=0`；交互运行时仍保留夹具的 30 帧设置。编辑器 batch 模式下 `Time.unscaledDeltaTime` 给出的“帧时间”约 0.06 ms，明显不是真实渲染帧时间，因此**没有**把它当作 GI 成本；性能使用下面的同步离屏渲染 + GPU 回读方法。
- **性能方法**：对同一相机、同一 1920×1080 ARGB32 sRGB RenderTexture，交替进行 GI on/off 的 `RenderPipeline.SubmitRenderRequest`，每次用 1 像素 `AsyncGPUReadback.WaitForCompletion()` 强制 GPU 完成，去掉 5 次预热后取 20 对样本。该值包含 CPU 提交、GPU 完成和回读等待，因此是偏保守的整帧延迟；GI on/off 的差值主要是颜色 copy + 全屏 GI 合成 pass。不是 vsync 限制下的 FPS。

### 5.2 性能结果（最终报告 `Reports/DynamicGI_Final/summary.json`）

| 场景 | GI 状态 | 平均 ms | 中位 ms | p95 ms |
|---|---:|---:|---:|---:|
| Sealed (step 1) | on | 5.80 | 6.18 | 6.84 |
| Sealed (step 1) | off | 5.46 | 5.89 | 6.23 |
| Large roof breach (step 6) | on | 4.94 | 4.96 | 5.40 |
| Large roof breach (step 6) | off | 4.70 | 4.66 | 4.99 |

- 合成 pass 的增量：Sealed 平均 +0.34 ms / 中位 +0.29 ms / p95 +0.61 ms；大顶洞平均 +0.24 ms / 中位 +0.30 ms / p95 +0.41 ms。即在本硬件、1920×1080 下，收敛后的 GI 合成开销约 **0.25–0.35 ms/帧（中位约 0.3 ms）**。
- 这是编辑器 batch 下的测量；没有制作独立播放器构建，因此播放器内的数值未测。

### 5.3 更新/收敛成本（几何变化时，编辑器 batch）

| 指标 | 实测 |
|---|---|
| 完整重建 CPU（含 GPU 回读等待） | 83–113 ms（第 2–7 步） |
| 遮挡体回读 `ComputeBuffer.GetData` | 26–52 ms |
| CPU 泛洪 + 室外掩码 | 36–50 ms |
| 最后一次反弹后的 GPU 回读等待 | 4–6.5 ms |
| 初始启动收敛 GPU 回读 | 33.1 ms |
| 状态变化 → 收敛截图（墙钟） | 0.170–0.214 s |
| 变化后第一帧 vs 收敛均值差 | 第 2 步 0.03288 vs 0.03316；第 3 步 0.05132 vs 0.05160；其余 <1% |

更新是**同步全量重建**，不是增量/局部更新；变化帧会有一次约 0.1 s 的 CPU/GPU 峰值开销，随后 5 轮反弹分摊到帧内。没有时间历史，也不需要在 Play 之外手动重建。

### 5.4 显存/内存

- GI 纹理 + 遮挡 buffer：`110,492,928` bytes（约 105.4 MiB，见 summary 的 `textureMemoryBytes`）。
- 三角形 StructuredBuffer：2,097,152 bytes（15,010 个三角形 × 128 B，容量按 2 的幂）。
- 室外掩码 R8 纹理：1,624,896 bytes（1.55 MiB）。
- CPU 数组：遮挡/室外/回读/队列约 16 MB；三角形列表约 2 MB。
- 这些是代码计算/测得的分配规模，不是 GPU Profiler 的整机占用；编辑器进程总显存未单独测量。

---

## 6. 测试完成情况

证据图片位于 `Reports/DynamicGI_Final/`（1920×1080；每张截图对应的状态/灯光/灯开关/分辨率/变化后帧数/时间/均值/接收点亮度见 `summary.json`）。`contact_sheet_steps.png` 与 `contact_sheet_tests.png` 是汇总对照图。

| 测试 | 结果 | 实测/证据 |
|---|---|---|
| 1 完整房间（关灯） | **通过** | GI on 与 off 均值均 0.00000；密封空间无漏光（SunOnly/SkyOnly 下也为 0）。`step1_on/off.png` |
| 2 外墙小洞 | **通过** | on 0.03316 vs off 0.00381；`Room_A_Indirect` 0.0290 vs 0；间接光可见。`step2_on/off/bounces.png` |
| 3 外墙大洞（内墙仍关） | **通过** | on 0.05160 vs off 0.01594；接收点 0.0455 vs 0；伪彩图见 `step3_falsecolor.png` |
| 4 打开内隔墙 | **部分通过** | GI on 0.05070 vs off 0.01594；Room B `Room_B_Indirect` 0.0078 vs 0（view 1/4）；跨房间光路存在但 Room B 亮度较低，近景 view 4 近乎黑。见 `view_1_on/off/bounces.png`、`view_4_on/off.png` |
| 5 小天花板洞 | **通过** | on 0.05810 vs off 0.02013；顶部光进入并产生室内间接光。`step5_on/off.png` |
| 6 两房大顶洞 | **通过** | on 0.11983 vs off 0.06871；接收点 0.107 vs 0；更大的更新与分布变化。`step6_on/off/falsecolor.png` |
| 7 恢复全部 | **通过** | on/off 均 0.00000，与第 1 步完全一致；旧光照立即消退（无历史）。`step7_on/off.png` |
| B 在 2–4 中切换遮挡板 | **通过** | 打开时 0.03316/0.05161/0.05070；关闭时全为 0.00000（房间重新封闭，关灯下无户外 GI）。`step*_shutter_*` |
| E 仅太阳/仅天空/组合（步 3） | **通过** | SunOnly on 0.03689 vs off 0.01594；SkyOnly on 0.03146 vs off 0.01140；SunAndSky on 0.05161 vs off 0.01594。`light_*`、`sealed_*` |
| I 室内灯开关 | **通过** | 密封+灯开 GI on 0.07297（接收点 0.1047）；灯关 GI on 0.00000；灯的直接光仍由 URP 绘制。`lamps_on_gi_on.png` / `lamps_off_gi_on.png` |
| V 相机变化/屏幕外贡献 | **通过** | 6 个视图的 GI on/off；view 1 0.01685 vs 0，view 4 0.00803 vs 0，view 5 0.40132 vs 0.39413。世界空间 GI 不随相机出现/消失。`view_*` |
| GI 独立开关与间接光调试 | **通过** | `G` 开关；`O` 四种显示模式；`step*_bounces.png`、`step*_falsecolor.png` |
| 漏光/历史/闪烁检查 | **通过（有保留）** | 封闭空间 0；无时间累积/拖影；静止收敛后无逐帧噪声。近景 0.1 m 体素块状感见 view_3/view_4；薄几何处仍有局部近似。 |
| 性能/内存记录 | **通过（有说明）** | 见第 5 节；同步渲染增量约 0.25–0.35 ms；更新约 0.1 s；编辑器 FPS 未作为成本依据。 |

### 6.1 各项能力完成情况

- **至少一次漫反射间接光反弹**：完成（5 轮；`BouncesOnly` 图直接显示反弹光）。
- **有遮挡的太阳与天空输入**：完成。密封房间 two outdoor modes 均 0；天空不是常量环境光。
- **表面颜色参与传输**：完成。使用材质 `_BaseColor × _BaseMap` 平均；场景红墙/青绿物体的颜色进入源项与反弹。纹理的逐像素变化被平均，见限制。
- **直接/间接区分，避免重复叠加**：完成。太阳直接光只在 URP；GI 只添加天空直接项（环境光为黑）和反弹；室内灯直接光只在 URP。
- **室内灯状态记录与间接光范围**：完成。截图 JSON 记录 `interiorLightsEnabled`；灯作为 GI 源最多 8 盏，只有开启时参与；其间接光在密封房间可见，关灯时消失。
- **以普通网格/变换/材质为输入**：完成。`MeshRenderer` + `MeshFilter` + `sharedMaterials`；未使用物体名、状态号或坐标表。
- **运行时网格替换/加入移除/变换/Reset**：完成。由 `GeometryChanges` 触发；第 1–7 步与 R 复位覆盖。
- **遮挡板移入/移出**：完成。B 测试。
- **恢复后旧光消退**：完成。第 7 步 = 第 1 步，均值均 0。
- **变化检测/更新范围/缓存失效/收敛**：完成并记录；全量重建 + E 清零 + 5 轮迭代。
- **可复用于其他网格/位置**：在配置的体素域内是；域/体素/参数可在 Inspector 调整，域外几何不参与（限制）。
- **相机/屏幕外贡献**：完成。GI 存在世界空间体素场，V 视图验证；不是屏幕空间效果。
- **漏光/拖影/闪烁/边界伪影处理**：密封漏光由连通性门控消除；无历史；0.1 m 体素近景块状感与薄几何近似仍存在（见限制）。
- **GI 开关与间接光调试显示**：完成（G/O/H + 面板按钮）。
- **不改变测试条件/不新增补光灯/不调亮度**：完成。只修改了夹具 UI 状态文字和 P 截图 JSON 字段；测试按键、几何、相机、灯光、材质、曝光均未改。

---

## 7. 已知限制与可复现问题

1. **近景体素块状感**：0.1 m 辐照度场在距离表面很近时可见约 10 cm 的块状过渡（view 3/4）。已用两次 3D 模糊和 halo 缓解；根本解决需要更小体素（显存/更新代价上升）或屏幕空间重建滤波。复现：步骤 4 的 view 3/4。
2. **Room B 亮度低**：步骤 4 的 Room B 接收点实测 0.0078（GI off 为 0），光路存在但视觉很暗；这是隔墙小开口的物理衰减 + 只有间接光。`view_4_on.png` 几乎黑；`view_1_on.png` 主要展示通过开口看到的 Room A。若要更亮的跨房演示，需要扩大开口或接受非物理提亮，本实现未这样做。
3. **薄/单面几何**：每个体素只保存一个“最近表面”的反照率和法线；薄片、单面网格和极端角落可能出现两侧光照混用。密封漏光由连通性门控抑制，但开放状态下薄几何的局部反弹仍可能不精确。
4. **连通性门控近似**：保守遮挡膨胀 + 泛洪把窄于约 0.2 m 的缝隙视为封闭；门控在关灯时整体抑制“相机所在空气与室外不连通”的 GI。开灯时相机门控关闭，薄几何处仍可能有少量户外漏光（`source` 门控仍生效）。
5. **太阳/天空可见性用体素 DDA**，不是 URP 阴影贴图；直接光斑和 GI 的阴影边缘在半影、薄遮挡处可能不完全一致。
6. **材质简化**：没有逐纹素反照率、金属/光滑度、法线纹理、自发光、透明；全部按漫反射处理。纹理只取平均亮度，因此有纹理的表面颜色较平。
7. **固定 GI 域**：域外物体不产生/不遮挡 GI；移动域需要改参数并重建。
8. **更新为同步全量重建**：几何变化帧有约 0.1 s CPU/GPU 峰值（编辑器 batch 实测），不是无感增量；5 轮反弹分摊到帧内。
9. **透明/体积/蒙皮**：不支持；GI 合成在 opaque 之后，透明物体不会被加 GI。
10. **性能**：只有编辑器 batch 的同步离屏测量；未构建 player，未测播放器 FPS/显存整机占用；没有逐 pass GPU timestamp（合成成本用同步渲染差值测得，包含颜色 copy）。
11. **相机密闭门控的语义**：它按几何连通性判断，与测试步骤编号无关；但它确实会隐藏“封闭且无灯空间中的残余户外可见性误差”。这是有物理依据的近似，仍需在文档中明确。
12. **灯泡阴影**：室内灯的 GI 可见性为体素 DDA；其直接光由 URP 绘制（URP 灯设置决定是否投影）。灯具/灯罩薄几何的遮挡可能不精确。

### 7.1 明确未完成 / 未测量项

- **未构建独立播放器**：所有运行与性能数据来自 Unity Editor（batch/交互）；播放器内的帧率、显存峰值和不同图形 API 未测。
- **未获得逐 pass GPU timestamp**：GI 合成成本用“同步 1080p 渲染 + 1 像素回读”的 on/off 差值度量，包含颜色 copy 和回读等待，不是 GPU Profiler 的单独 pass 时间。
- **Room B 亮度**：步骤 4 只达到“可测到跨房间接光、但视觉较暗”，因此该项标为部分通过；没有通过增加隐藏灯/提亮材质来美化。
- **近景体素块状感**：0.1 m 分辨率下未消除（view 3/4）；没有用更高分辨率重测（显存/更新开销会显著增加）。
- **透明、体积散射、蒙皮、镜面焦散**：按任务不属于必需能力，未实现。
- **连通性门控对窄缝的保守处理**：小于约 0.2 m 的缝隙可能被视为封闭；未做更精细的多尺度连通性。
- **渐进式/增量更新**：未实现；每次几何变化为全量同步重建，变化帧有约 0.1 s 峰值。

---

## 8. 验证工具与复现

- 菜单：`GI Evaluation > Dynamic GI > Run Full Verification (Play)` 运行完整七步骤、B/E/I/V、性能与截图；需要先停止 Play。
- 命令行（batch）：
  ```
  Unity.exe -batchmode -projectPath "<project>" ^
    -executeMethod DynamicGI.Editor.DynamicGIAutomation.BatchEntry ^
    -logFile "Logs/gi_verification.log" -force-d3d12
  ```
  该入口进入 Play，按顺序执行并写 `Reports/DynamicGI_Final/summary.json`、`conditions.txt` 和 PNG，然后退出编辑器。
- 输出目录中的 `summary.json` 包含每张截图的 `state/lighting/interiorLamps/previewFill/giEnabled/viewMode/viewName/roofOpening/interiorOpen/shutterClosed/width/height/geometryRevision/framesSinceChange/secondsSinceChange/rebuildCpuMs/maskReadMs/maskBfsMs/gpuConvergeMs/bouncePass/meanLuma/receiverLuma/cameraPosition/cameraEuler/fieldOfView`。
- `Reports/DynamicGI_Final/contact_sheet_steps.png`：七步骤的 off/on/bounces/first 四列对照。
- `Reports/DynamicGI_Final/contact_sheet_tests.png`：B/E/I/跨房间对照。
- 说明：这些验证截图由 1920×1080 相机 RenderTexture 产生，不包含 IMGUI 面板；交互模式下的 P 截图仍由夹具原逻辑产生，并额外记录 GI 状态字段。
- 运行环境说明：验证时主 Editor 已打开本工程（Unity 单实例锁），因此自动化使用同一 `Assets`/`ProjectSettings` 同步出的临时 batch 工程执行（执行后已删除临时目录），结果复制回主工程的 `Reports/DynamicGI_Final`。主工程的同一份 Assets 已在 Editor 中重新导入并编译通过（无 error，仅两个关于新 API 的 warning）。

---

## 9. 实现约束遵守情况

- 未使用 Unity 内置烘焙 GI、Lightmap、Light Probe、Adaptive Probe Volume 或现成完整 GI 插件；光传输、体素化、可见性、反弹迭代、连通性判定均为本工程 C#/Compute Shader 实现。
- 使用 URP RendererFeature/RenderGraph、Compute Shader、ComputeBuffer/AsyncGPUReadback、3D RenderTexture 等低层通用工具；用途已在上文注明。
- 没有新增隐藏补光灯、没有按状态调亮材质/曝光/环境亮度；GI 开/关对照只切换 `enableGI`。
- 测试场景、七步骤、遮挡板、光照输入切换、相机、几何、材质、曝光保持不变。对夹具的唯一修改是 UI 状态文字、键位提示、P 截图 JSON 增加 `gi*` 字段（附于现有字段，不改变语义），以及 `README`/本说明的文档更新。
