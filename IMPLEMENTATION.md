# 可破坏环境中的实时动态全局光照：实现说明

本文档对应 `eval3` 分支上的实现。下文所有数字都来自本机的实际运行，记录在
`Docs/DynamicGI/data/log.txt`、`results.json`、`series.csv`、`../convergence.json`。没有运行过或无法测得的内容会单独注明。

---

## 1. 概览

| 项目 | 内容 |
|---|---|
| 方法 | 世界空间辐照度探针（DDGI 风格）+ GPU 软件光线追踪（两级 BVH，Compute Shader），逐帧更新，时间累积 |
| 反弹 | 探针光线命中点 = 漫反射率 ×（阴影光线测得的太阳/点光源直接光 + 上一帧探针辐照度）→ 无限次漫反射反弹（随帧数递推） |
| 天空 | 只在探针光线**逃逸出所有几何**时采样 `GIBreachEnvironment.skyRadiance × EffectiveSkyIntensity`；封闭空间没有任何常量环境光 |
| 几何输入 | 场景中所有启用的不透明 `MeshRenderer + MeshFilter` 的三角形、`localToWorldMatrix` 与材质；与测试编号、物体名称无关 |
| 渲染接入 | URP RenderGraph pass（`RenderPipelineManager.beginCameraRendering` 中 `EnqueuePass`）：不透明物体后绘制 albedo/法线/位置 MRT，再全屏叠加 `albedo × E/π` |
| 开关 | **G** GI 开/关；**H** 调试视图（Final / Indirect only / Irradiance only）；**J** 隐藏 GI 面板；右上角面板也有按钮 |
| 代码 | `Assets/DynamicGI/`（Runtime、Resources 着色器、Editor 工具），`Tools/DynamicGI/make_report_assets.py` |

未使用 Unity 内置烘焙 / Enlighten / APV / SSGI / 任何第三方 GI 插件，也未使用硬件光线追踪。用到的低层通用工具只有 URP RenderGraph、
`GraphicsBuffer`/`ComputeShader`、`AsyncGPUReadback`（仅诊断采样）、`CustomSampler/Recorder`（计时）。
MCP 不是运行依赖；开发过程中也没有使用 MCP，而是使用工程内自写的编辑器命令桥（见 §9）。

---

## 2. 算法

### 2.1 场景表示：两级 BVH（`GIBvhBuilder.cs`、`DynamicGIProbes.compute`）

- **BLAS**：按 `Mesh` 对象缓存。从 `mesh.vertices / GetTriangles(submesh)` 读取物体空间三角形（存 `v0, e1, e2, submesh`），
  用分箱 SAH（12 bins，叶子 ≤ 2 个三角形）建树。本场景共 13 个不同网格、4752 个三角形。
- **TLAS**：对所有可见实例的世界包围盒用同一个 SAH 构建器建树（叶子 1 个实例）。每个实例保存 world→object 矩阵、BLAS 偏移、
  材质偏移、绕序符号（负缩放）。本场景 239 个实例、15152 个场景三角形、5630 个节点。
- **GPU 遍历**：Compute Shader 中的栈式遍历（近子节点优先入栈，固定上限 6000 次节点访问，避免畸形数据导致 GPU 挂起）。
  实例内光线变换到物体空间但不重新归一化，因此命中距离 t 在两级之间一致。Möller–Trumbore 求交，双面，
  通过 `cross(e1,e2)` 与光线方向判断正/背面（背面命中用于探针分类与重定位）。

### 2.2 探针体（`DynamicGISystem.cascades`）

| 级联 | 原点 | 间距 | 数量 | 用途 |
|---|---|---|---|---|
| 0（细） | (-6.3, -0.05, -0.45) | 0.40 m | 33×11×28 = 10164 | 建筑内部 |
| 1（粗） | (-14, -0.6, -13) | 1.00 m | 29×8×26 = 6032 | 庭院、远场、细级联之外的命中点 |

级联范围是**场景配置参数**（相当于关卡里摆放的 GI Volume），与测试状态无关。换到其他场景时需要调整这两个包围体，详见 §8。
着色时选用包含该点的最细级联，并在细级联边界外的一个格距内与粗级联线性过渡。

每个探针保存：
- 辐照度：八面体映射 6×6 + 1 像素边框，存于 RGBA16F 图集；数值为余弦加权平均入射辐射度 = E/π，与 URP 的光照单位一致。
- 距离矩：八面体 10×10 + 边框，存均值与均值平方，RG16F。
- 重定位偏移与激活状态：`float4` 缓冲。

### 2.3 每帧探针更新（6 个 kernel，`DynamicGIProbes.compute`）

1. **TraceProbes**：每个探针 128 条光线。前 32 条为固定的球面 Fibonacci 方向，仅用于分类和重定位；其余 96 条每帧随机整体旋转。
   - 未命中：天空辐射度（Cubemap mip 1 × 有效天空强度；SunOnly 时为 0）。
   - 背面命中：记为负距离。
   - 正面命中：`albedo × (Σ 灯光颜色 × NdotL × 衰减 × 阴影光线可见性 + 上一帧探针在命中点的辐照度)`。
   - 灯光：场景内所有启用的方向光、点光源和聚光灯，最多 8 盏；衰减公式与 URP 相同。阴影由光线追踪计算，不使用阴影贴图，因此与相机无关。
2. **RelocateProbes**：按 DDGI 方式处理。背面比例 > 25% 视为探针在几何体内部，沿最近的背面方向移出；离正面过近则推开；
   空间充裕时逐步回到格点。偏移上限为 0.45×间距。仍处于内部的探针标记为未激活，着色时忽略，只继续追踪 32 条分类光线，以便几何被移除后重新激活。
3. **BlendIrradiance / BlendDistance**：按余弦（或 cos⁵⁰）加权聚合光线，再与历史值做指数滑动平均，历史滞后系数见 §4.3。
4. **BorderIrradiance / BorderDistance**：复制八面体边框，保证双线性过滤正确。

粗级联每帧只更新 1/4（`coarseUpdateFraction`，轮转窗口），细级联每帧全部更新。

### 2.4 着色采样（`DynamicGICommon.hlsl: GISampleIrradiance`）

取 8 个相邻探针，权重 = 三线性权重 × 背面权重 `((dot(dirToProbe, n)+1)/2)² + 0.05` × Chebyshev 可见性
（用距离矩做可见性检验，结果取 3 次方）。权重小于 0.2 时再做立方压缩，防止几乎被遮挡的探针透过薄墙漏光。
采样点沿法线偏移 0.25×间距，沿视线方向偏移 0.10×间距。同一个函数既用于屏幕像素，也用于探针光线的命中点（多次反弹）。

### 2.5 直接光 / 间接光的区分

- URP 前向渲染只负责直接光：太阳、两盏室内灯、阴影。夹具把环境光强度和反射强度都设为 0，环境 SH 为黑。
- GI 合成 pass **只叠加漫反射间接光** `diffuse × E/π × indirectIntensity`，其中 `diffuse = baseColor × baseMap × 0.96 × (1 − metallic)`，与 URP 的 `brdfData.diffuse` 一致。
- 探针光线永远看不到光源本身：天空 Cubemap 中没有太阳圆盘（实测最亮的天空像素约为 0.65 线性单位，位于地平线渐变处），灯光也不是几何体。
  因此着色点所受的直射光不会再被 GI 加一次。
- 室内灯：直接光由 URP 绘制，间接光由本系统计算，二者都随 **I** 开关变化（灯的状态会改变 GI 输入，并触发全局重新收敛）。
  正式的室外对照（A、B、E、G 组）一律关闭室内灯，并在每条记录中写明灯的状态。

### 2.6 材质简化

- 反照率 = `_BaseColor`（线性空间）× `_BaseMap` 的**平均颜色**（GPU 降采样到 64×64 后回读一次，按纹理缓存）× (1 − metallic) × 0.96。
  在 GI 命中点上，纹理细节被平均掉；屏幕像素则使用真实的纹理和法线贴图。
- 不计算间接高光、清漆、透明、自发光（`_EmissionColor` 被忽略，本场景全为 0）、半透明和体积散射。
- 材质为 `_Surface = Transparent` 的渲染器不参与 GI 场景。

---

## 3. 几何与光照输入

- **启动**：`OnEnable` 中 `FindObjectsByType<MeshRenderer>` 读取当前全部有效几何，满足启用、`activeInHierarchy`、在层遮罩内、
  非 ShadowsOnly、有 Mesh、不透明这些条件即纳入。
- **事件**：订阅 `GeometryChanges.Changed`（Added / Removed / TransformChanged / MeshChanged / SceneReset）和 `GIBreachEnvironment.Changed`。
- **轮询兜底**（每帧，约 240 个渲染器）：逐个比较 `localToWorldMatrix`、`sharedMesh` 引用、`sharedMaterials` 引用和可见性；
  每 0.5 s 重新扫描一次，以发现新建或销毁的渲染器。这样，没有发布事件的外部修改也能被发现（G1、G2 已实测）。
- **原地修改网格顶点**：Mesh 引用不变，轮询无法察觉，需要发布 `MeshChanged`（这正是接口约定的用法）。收到后丢弃该 Mesh 的 BLAS 缓存并重建（G3 已实测）。
- **光照**：每帧打包灯光参数（位置/方向、颜色 × 强度、范围、聚光角）以及天空 Cubemap 和有效强度，并计算哈希。
  哈希变化或收到环境事件时触发全局重新收敛。

---

## 4. 动态更新、缓存失效与历史处理

### 4.1 变化检测 → 重建范围

| 变化 | 处理 |
|---|---|
| 变换（例如遮挡板 B） | 只重建 TLAS 并重新上传实例和材质表，BLAS 不变 |
| 物体加入/移除（碎片、顶部饰条） | TLAS 重建；新出现的 Mesh 构建 BLAS；不再使用的 BLAS 从池中移除，重新上传三角形池 |
| 网格替换（外墙、隔墙、屋顶三种网格） | 同上，替换后的网格各自有缓存的 BLAS（首次出现时构建） |
| 原地修改网格 + `MeshChanged` | 丢弃并重建该 Mesh 的 BLAS |
| `SceneReset`（R、L 键） | 清空 BLAS 缓存，完整重扫；可选清空探针历史（`clearHistoryOnSceneReset`，默认关闭，改用全局加速收敛避免黑屏一帧） |

实测的 CPU 重建耗时（TLAS 加新 BLAS 加上传）：步骤切换 3.8–5.2 ms，遮挡板 3.5 ms。重建在变化当帧的 `LateUpdate` 中完成，同一帧就用新几何追踪。

### 4.2 失效区域

每次变化记录"变化前包围盒"和"变化后包围盒"，各向外扩 2.5 m 作为失效区域（最多同时 16 个），并保持 45 帧。
区域内探针的历史滞后系数降到 0.30，区域外的探针在 45 帧内降到 0.85。

### 4.3 历史滞后系数

- 稳态：0.97，用来抑制噪声。
- 变化窗口内：
  - 全局 0.85；
  - 失效区域内 0.30；
  - 逐纹素**大幅变化检测**：新值与旧值之差超过 30% × max(新, 旧) 时取 0.55；距离矩的均值变化超过 20% 最大距离时同样取 0.55。
- 大幅变化检测**只在变化窗口内启用**。早期版本一直开着，结果采样噪声频繁触发它，历史被反复截断，墙面出现跳动的斑块。
- GI 从关闭切回开启时，也会触发一次全局加速收敛。

### 4.4 重新收敛（实测，见 §6 与 `convergence.json`）

- 刚变化后的第一帧就已使用新几何。截图中的"first frame"画面已经反映新的光路，但噪声较大。
- 室外光进入时，阳光直照区域附近的接收点 0.3–1.6 s 内进入最终值 ±10%；只靠多次反弹照亮的阴影区接收点为 2.4–3.5 s。
- 入口被重新堵住时，旧光照消退很快：恢复（7）0.26–0.43 s；遮挡板 B 0.44–0.87 s；床下暗区约 1.4 s。

---

## 5. 渲染接入（`DynamicGIRenderPass.cs`、`Resources/*.shader`）

1. **DynamicGI Albedo**（RenderGraph raster pass，`AfterRenderingOpaques`）：用 `overrideShader = Hidden/DynamicGI/GBuffer` 重画所有不透明物体。
   - 原材质的 `UnityPerMaterial` 由 `LitInput.hlsl` 保持一致，兼容 SRP Batcher。
   - 深度测试 LEqual，不写深度。
   - 输出三张 RT：漫反射率（RGBA8）、带法线贴图的世界法线（RGBA16F）、世界坐标（RGBA32F）。
2. **DynamicGI Composite**：全屏三角形读取上面的 MRT 和探针图集。
   - Pass 0 使用 `Blend One One`，把间接漫反射叠加到 HDR 颜色上；随后才进行 URP 的天空盒、后处理和色调映射，所以曝光与 GI 关闭时完全相同。
   - Pass 1 是调试视图，输出纯间接光或纯辐照度。
3. 计算 pass 在 `DynamicGISystem.LateUpdate` 中用 `Graphics.ExecuteCommandBuffer` 每帧提交一次，与相机数量无关，Game 视图和 Scene 视图共享同一份结果。

工程设置**未做修改**：URP 资源、渲染器和 Forward+ 都保持原样。本系统也不依赖 URP 的 SSAO、深度纹理或法线纹理。

---

## 6. 测试结果

### 6.1 测试条件

- 硬件：NVIDIA GeForce RTX 4080 Laptop GPU、Intel i9-13980HX、32 GB 内存，Windows 11。
- 软件：Unity 6000.5.4f1（编辑器 Play 模式）、URP 17.5.0、Direct3D 12。
- 输出：主相机渲染到 **1920×1080** RT（`DynamicGIEvaluationRunner` 自己渲染，与 Game 视图尺寸无关），8-bit sRGB；后处理按场景原有配置（ACES、Bloom、Vignette、SMAA）。
- 条件：预览补光 L **关闭**；曝光固定（夹具 Volume，无自动曝光）；固定室内相机为视角 0 "Room A - fixed comparison view"。
- 帧率：夹具在编辑器中限制为 30 帧、垂直同步关闭，测试过程保持这一设置；性能测试期间临时改为 `targetFrameRate = -1`、vSync 0，结束后恢复。
- GI 参数：两级联共 16196 个探针，每探针 128 条光线（32 条固定 + 96 条旋转），粗级联每帧更新 1/4，滞后系数 0.97。
- 每个状态：在变化后第 1 帧截图（`*_first`）；接收点辐照度逐帧记录 4 s；4 s 后分别截取 GI 开、GI 关、纯间接光三张图。
- 接收点：夹具的 4 个接收点（Direct_Control、Room_A_Indirect、Room_B_Indirect、Roof_Direct_Control），加上场景里的两个 GI Sample 点（Rear_Alcove、Under_Bench）。
  数值为探针辐照度 E/π 的亮度，由 GPU 计算后经 `AsyncGPUReadback` 读回。
- 复现：打开场景 → Play → 通过编辑器命令桥或代码调用 `DynamicGI.Evaluation.DynamicGIEvaluationRunner.Run()`
  （完整一轮约 5 分钟，输出到 `Captures/DynamicGI/eval/`）→ 运行 `python Tools/DynamicGI/make_report_assets.py` 生成 `Docs/DynamicGI/*.jpg` 和 `convergence.json`。

### 6.2 对照图

| 内容 | 文件 |
|---|---|
| 步骤 1–7（Sun+Sky，室内灯关）：第一帧 / +4 s GI 开 / GI 关 / 纯间接光 | `Docs/DynamicGI/steps_1-4_sunsky_lampsoff.jpg`、`steps_5-7_sunsky_lampsoff.jpg` |
| 步骤 1、4、7，室内灯开 | `steps_lamps_on.jpg` |
| 遮挡板 B（步骤 2/3/4） | `blocker_steps_2-4.jpg` |
| E × I（步骤 4） | `lighting_modes_step4.jpg` |
| 6 个夹具视角（步骤 6） | `views_step6.jpg` |
| 恢复对照 | `restore_compare.jpg` |
| 7 步之外的通用几何编辑 | `generic_geometry.jpg` |
| 单张 1920×1080 原图（JPG） | `Docs/DynamicGI/data/*.jpg`；全部 PNG 在 `Captures/DynamicGI/eval/`（不进版本库） |

### 6.3 逐项结论

接收点数值是 4 s 后的探针辐照度亮度（E/π，线性），图像均值是 8-bit 截图的平均亮度。均为同一相机、同一曝光、1920×1080。

| 测试 | 结果 | 依据（实测） |
|---|---|---|
| **1 完整房间**（灯关） | **通过** | GI 开/关图像均值 0.02 / 0.00（255 级）。各接收点 ≤ 1.7e-4，即数值噪声水平，没有室外光透过墙或屋顶。E 组的封闭参考图为 0.06 |
| **2 外墙小洞** | **通过** | 阴影接收点 Room_A_Indirect 由 3e-5 升到 0.0042，Roof_Direct_Control 升到 0.0007（无直射，靠反弹）。图像均值 GI 开 3.63 / 关 0.97 |
| **3 外墙大洞** | **通过** | Room_A_Indirect 升到 0.0195，Under_Bench（床下，无直射）由 9e-5 升到 0.0019。图像均值 11.6 / 4.1。Room_B_Indirect 保持 0.00000，因为隔墙仍然完整，没有跨墙泄漏 |
| **4 打开内隔墙** | **部分通过** | 隔墙打开后，Room_B_Indirect 由 0.00000 升到 0.00025（灯关，0.9 s 收敛），跨房间传播确实存在且方向正确，但该点只靠两次以上反弹，量很弱。灯开时 B 间受本房间灯光主导（0.0041）。0.4 m 探针间距下，开口两侧的间接光细节有限 |
| **5 小天花板洞** | **通过** | Roof_Direct_Control 由 0.0028 升到 0.0205；Room_A_Indirect 由 0.0178 升到 0.0262；1.7–3.4 s 收敛 |
| **6 两间房大顶洞** | **通过** | Room_B_Indirect 升到 0.0198，Rear_Alcove 由 1e-5 升到 4.5e-4，Under_Bench 0.0029。图像均值 34.5 / 17.5。探针更新 GPU 3.8 ms（步骤 1 为 2.9 ms），重建 5.2 ms |
| **7 恢复全部** | **通过** | 从步骤 6 恢复后，所有接收点在 0.26–0.43 s 内回到 ≤ 2e-4（床下 1.4 s）。与恢复前的封闭参考相比：接收点最大差 1e-4；8-bit 图像平均绝对差 **0.07**；恢复后图像均值 0.00 |
| **B 遮挡板（步骤 2–4，屋顶完整）** | **通过** | 遮挡后，Direct_Control 由 0.025–0.063 降到约 0.0002，Room_A_Indirect 降到 ≤ 7e-5，0.44–0.87 s 内完成；移开后恢复到原值（例如步骤 3：0.0622 对 0.0616），反弹光 2.4–3.2 s 回到最终值 ±10%。没有残留旧亮斑 |
| **E 切换 + 单独关 I** | **通过** | 步骤 4，室内灯关：SunOnly 时 Room_A 为 0.0084（暖色）；SkyOnly 时 Direct_Control 为 0.052、Room_A 为 0.0108（冷色）；SunAndSky 时 Room_A 为 0.0174。天空只经开口进入（封闭时为 0）。灯开后各项增加，灯光的间接光也被计算 |
| **V 切换 / 移动相机** | **通过**（世界空间一致性）/ **部分通过**（噪声） | 步骤 6 中切换 6 个视角，各接收点数值与切换前的最大绝对差为 0.0015–0.0022（参考值 0.0004–0.092），属于时间噪声量级；相对差大的项都出现在数值极小的接收点上。探针更新与相机无关：视角 4 中两个天花板洞都不在画面内，视角 3 中外墙洞不在画面内，这些画面里的间接光依旧存在（`views_step6.jpg`）。残余的时间噪声见下一行 |
| 闪烁 / 时间噪声 | **部分通过** | 步骤 3 静态 90 帧，逐帧标准差：Direct_Control 0.17%，Room_A 2.7%，Roof_Direct 2.2%，Under_Bench **8.7%**（最暗处）。暗区仍有轻微的低频亮度起伏；变化后的第一帧和刚收敛时有斑驳噪声，这是时间累积方案本身的代价 |
| 7 步之外的通用几何（G） | **通过** | G1 不发布事件移动 Moving Occluder，Room_B 由 0.0040 变为 0.0038，移回后恢复到 0.0045。G2 运行时新建红色面板（无事件），Room_B 由 0.0045 降到 0.0006，并呈现红色染色；删除后恢复到 0.0043。G3 原地改顶点并发布 MeshChanged，Room_B 由 0.0045 降到 0.0008，换回原网格后恢复到 0.0047 |
| 有色表面染色 | **通过（目视）** | 红墙附近的地面、天花板呈红色，G2 红板也同样染色（`steps_*`、`generic_geometry.jpg` 的 indirect-only 图）。没有做定量的色度测量 |

### 6.4 响应延迟

- GI 在变化**当帧**（同一帧 `LateUpdate`）就用新几何追踪，截图中的 `*_first` 即为变化后第 1 帧的画面。
- 诊断回读的接收点数值通常在第 3–7 帧开始变化，其中包含 `AsyncGPUReadback` 约 2–3 帧的延迟，所以 GI 本身的延迟比这更短。
- 收敛到最终值 ±10%（3 帧平滑）的时间，见 `Docs/DynamicGI/convergence.json`：
  - 光照增加时：直射附近 0.3–1.6 s，阴影区 2.4–3.5 s（30 fps 下约 75–105 帧）。
  - 光照减少时：0.26–0.87 s。
- 瞬时开销：变化当帧的 CPU 重建 3.5–5.2 ms。没有观察到额外的 GPU 尖峰，因为每帧的追踪量本来就是固定的。

---

## 7. 性能（实测）

条件：编辑器 Play 模式，主相机渲染到 1920×1080 RT（Game 视图同时也在绘制），视角 0，Sun+Sky，室内灯关，`targetFrameRate = -1`、vSync 0。
每种情况取 3 s 平均，GI 开/关/开交替进行。GPU 数字来自 `CustomSampler`/`ProfilingSampler` 的 GPU 计时。

| 状态 | 帧耗时 GI 开 | 帧耗时 GI 关 | GI 探针更新 GPU | Albedo pass GPU | 合成 GPU | GI CPU（每帧） |
|---|---|---|---|---|---|---|
| 步骤 1 | 5.82 ms（p95 6.9） | 5.43 ms | 2.88 ms | 0.12 ms | 0.30 ms | 0.70 ms |
| 步骤 4 | 5.88 ms | 5.37 ms | 3.28 ms | 0.12 ms | 0.30 ms | 0.72 ms |
| 步骤 6 | 5.94 ms | 5.63 ms | 3.80 ms | 0.11 ms | 0.30 ms | 0.74 ms |

说明：
- 编辑器下帧耗时以 CPU 为瓶颈（约 5.4 ms），GPU 工作与 CPU 并行，因此 GI 开/关的帧耗时差只有 0.3–0.5 ms，**不能代表 GI 的 GPU 成本**。
  GI 的 GPU 成本应以分项计时为准，合计约 **3.3–4.2 ms / 帧**（1080p，本机）。
- `FrameTimingManager` 在编辑器里报告的 GPU 帧时间约 1.27 ms，GI 开关前后不变，明显没有包含 `ExecuteCommandBuffer` 里的计算工作，**判定为不可靠，未采用**。
- 开发过程中也测到过追踪 kernel 6–8 ms 的情况。那时 Unity 窗口在后台、GPU 可能降频，探针更新也是全量的，未做受控对比；上表是最终配置下的结果。
- 没有测试独立 Player 构建，也没有测试其他 GPU。
- 分项耗时（开发期，单次采样）：追踪约占 85%，混合约 1.0 ms，重定位和边框 < 0.1 ms。追踪中阴影光线约占 1 ms，命中点探针采样约 0.5 ms，其余是主光线遍历。

### 内存（GPU）

| 资源 | 尺寸 |
|---|---|
| 辐照度图集 RGBA16F | 1024×1016，约 7.9 MiB |
| 距离图集 RG16F | 1536×1524，约 8.9 MiB |
| 光线结果缓冲 | 16196 × 192 × 16 B，约 47.5 MiB（按最大 192 条光线分配；当前 128 条，实际只用 2/3） |
| 探针数据 | 0.25 MiB |
| 合计（统计值） | **64.6 MiB** |
| BVH 节点 + 实例 + 三角形 + 材质 | 约 0.5 MiB |
| RenderGraph 临时 MRT（1080p） | RGBA8 + RGBA16F + RGBA32F，约 57 MiB，每帧由 RG 池分配。这是为了简化实现而付出的带宽/内存代价，可改为深度重建坐标、八面体编码法线 |

### 质量 / 性能取舍

- `raysPerProbe` 128 降到 96：追踪约减少 1.7 ms，噪声相应增加。
- `probeUpdateFraction = 0.5`：GPU 开销减半，但收敛时间翻倍。
- 细级联间距 0.4 m：再细则开销按体积的立方增长，再粗则开口处的间接光会更模糊，薄墙两侧也更依赖 Chebyshev 测试。

---

## 8. 已知限制与问题（可复现条件）

1. **低频与斑块**：间接光的空间分辨率受探针间距（细级联 0.4 m）限制。小光斑反弹出的光是平滑的大块，墙角和小物体缺少接触阴影（没有接入 SSAO）。
   在 `lighting_modes_step4.jpg` 的灯开纯间接光图中，B 间开口附近的墙面能看到轻微的探针格状斑块。
2. **时间噪声**：最暗区域（床下）逐帧亮度起伏约 9%。变化后第一帧及随后约 0.5 s 内，画面有明显的斑驳噪声，因为此时历史被截断，每个探针只有 96 条光线。
3. **探针体需要配置**：两个级联的包围体按本建筑手动设置（属于场景参数）。物体超出粗级联范围时，只能拿到最粗级联边缘的外推值；
   细级联之外的室内区域会退化为 1 m 精度。没有实现自动适配或相机跟随的级联。
4. **薄几何**：厚度远小于 0.4 m 的遮挡物（如 2 cm 的线缆、单面叶片）能正确遮挡光线，但无法阻止探针插值跨越它们。
   本场景的墙厚 ≥ 0.22 m，加上重定位和 Chebyshev 测试，未观察到漏光（封闭状态的数值为 0）。更薄的墙在理论上可能漏光，没有测试。
5. **材质**：命中点使用纹理平均色；不支持间接高光和自发光；金属材质只按 (1 − metallic) 衰减漫反射；骨骼网格、透明和体积散射不支持（题目不要求）。
6. **几何输入**：只读取 `MeshRenderer + MeshFilter`（不包括 SkinnedMeshRenderer 和地形）。原地修改顶点必须发布 `MeshChanged`。
   新建或销毁物体靠 0.5 s 一次的重新扫描发现，最多晚 0.5 s；Added/Removed 事件则立即生效。
7. **灯光**：最多 8 盏实时灯参与 GI，按扫描顺序截断；不支持面光源。聚光灯按 URP 公式计算，本场景没有聚光灯，**未测试**。
8. **跨房间传播偏弱**：步骤 4 灯关时，B 间接收点只有 2.5e-4。物理上它只能接收多次反弹，所以量小是合理的；但没有参考解（离线路径追踪）做对比，无法定量判断精度。
9. **性能**：只在 RTX 4080 Laptop 和编辑器中测试过；独立 Player、低端 GPU 以及 4K 下的开销**未测**。
10. **夹具界面**：夹具左侧面板仍显示 "GI NOT IMPLEMENTED"。这是夹具自身的固定文字，本实现没有修改夹具代码，GI 状态以右上角的 DYNAMIC GI 面板为准。

---

## 9. 运行方式与文件

### 运行

1. 打开 `Assets/GIEvaluation/Scenes/GI_TestScene.unity`，场景中已经有 "Dynamic GI System" 对象。
2. 菜单 **GI Evaluation > Start Interactive Test**。夹具原有的按键全部保留（1–7、R、V、L、E、C、B、I、T、P）。
3. 新增按键：**G** GI 开/关；**H** 调试视图（Final → Indirect only → Irradiance only）；**J** 显示/隐藏 GI 面板。
4. 如果是新场景，可以用菜单 **GI Evaluation > Dynamic GI > Install In Open Scene** 添加组件，之后调整 `cascades` 包围体。

### 主要参数（`DynamicGISystem` Inspector）

| 参数 | 默认值 | 作用 |
|---|---|---|
| `giEnabled` / `debugView` / `indirectIntensity` | true / Final / 1 | 开关、调试视图、间接光倍率（评测中固定为 1） |
| `cascades` | 见 §2.2 | 探针体范围与间距 |
| `raysPerProbe` / `fixedRays` | 128 / 32 | 每帧每探针的光线数 / 其中用于分类的固定光线数 |
| `probeUpdateFraction` / `coarseUpdateFraction` | 1 / 0.25 | 细级联每帧更新比例 / 粗级联的额外比例 |
| `hysteresis` | 0.97 | 稳态历史滞后系数 |
| `largeChangeHysteresis` / `globalBoostHysteresis` / `dirtyRegionHysteresis` | 0.55 / 0.85 / 0.30 | 变化窗口内的滞后系数 |
| `boostFrames` / `dirtyRegionPadding` / `changeThreshold` | 45 / 2.5 m / 0.3 | 变化窗口长度、失效区域外扩距离、大幅变化阈值 |
| `normalBias` / `viewBias` / `chebyshevPower` | 0.25 / 0.10 / 3 | 采样偏移（单位为格距）与可见性锐度 |
| `skyMip` / `bounceEnergy` | 1 / 1 | 天空采样 mip、多次反弹能量系数（1 = 物理） |

### 文件

| 路径 | 内容 |
|---|---|
| `Assets/DynamicGI/Runtime/DynamicGISystem.cs` | 场景采集、变化检测、BVH 上传、探针调度、全局参数、面板 |
| `Assets/DynamicGI/Runtime/GIBvhBuilder.cs` | 分箱 SAH BVH 构建器 |
| `Assets/DynamicGI/Runtime/DynamicGIRenderPass.cs` | URP RenderGraph 接入 |
| `Assets/DynamicGI/Resources/DynamicGIProbes.compute` | 追踪、重定位、混合、边框、诊断采样 |
| `Assets/DynamicGI/Resources/DynamicGICommon.hlsl` | 数据布局、八面体编码、探针采样 |
| `Assets/DynamicGI/Resources/DynamicGIGBuffer.shader`、`DynamicGIComposite.shader` | albedo MRT 与合成 |
| `Assets/DynamicGI/Runtime/Evaluation/DynamicGIEvaluationRunner.cs` | 自动评测（截图、逐帧接收点数据、性能）。纯测试工具，GI 本身不依赖它 |
| `Assets/DynamicGI/Editor/DynamicGIEditorTools.cs`、`DynamicGIDev.cs` | 安装菜单、开发用单步操作 |
| `Assets/DynamicGI/Editor/DevBridge/` | 开发用编辑器命令桥（独立 asmdef）。只有存在 `Logs/gi_bridge/ENABLED` 文件时才启动，用于在编辑器位于后台时下发编译、Play 和调用命令；不属于 GI 运行依赖 |
| `Tools/DynamicGI/make_report_assets.py` | 生成对照拼图与收敛统计 |
| `Docs/DynamicGI/` | 对照图、原始数据 |

### 对现有工程的修改

- `GI_TestScene.unity`：**只新增**一个 "Dynamic GI System" GameObject，带 `DynamicGISystem` 组件（diff 为纯新增）。
  没有修改任何原有物体、几何、相机、光源、材质、Volume 或测试条件。
- 夹具脚本（`Assets/GIEvaluation/**`）、`TASK.md`、URP 资源和工程设置**均未修改**。
