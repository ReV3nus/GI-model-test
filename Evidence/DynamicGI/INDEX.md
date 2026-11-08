# 最终实测证据索引

正式对照：1920×1080，原 Room A 相机，SunAndSky，室内灯 OFF，L 预览 OFF；曝光与太阳参数不变。GI 480×270，4 spp/frame，2 bounces。on=65 dispatch（260 spp），off 不提交与不合成 GI。时间为每张 JSON 中 secondsSinceOperation。

| 步骤 | 首帧 | GI on | GI off | on 时间 s | on/off 帧均值 ms |
|---|---|---|---|---:|---:|
| 1 Sealed | [first](step1-first.png) | [on](step1-on.png) | [off](step1-off.png) | 0.701 | 8.528 / 5.052 |
| 2 SmallBreach | [first](step2-first.png) | [on](step2-on.png) | [off](step2-off.png) | 0.719 | 8.650 / 5.036 |
| 3 LargeBreach | [first](step3-first.png) | [on](step3-on.png) | [off](step3-off.png) | 0.708 | 8.106 / 4.804 |
| 4 ConnectedRooms | [first](step4-first.png) | [on](step4-on.png) | [off](step4-off.png) | 0.706 | 8.464 / 5.383 |
| 5 SmallRoofBreach | [first](step5-first.png) | [on](step5-on.png) | [off](step5-off.png) | 0.841 | 10.380 / 5.336 |
| 6 LargeRoofBreach | [first](step6-first.png) | [on](step6-on.png) | [off](step6-off.png) | 0.781 | 9.228 / 5.216 |
| 7 Restored | [first](step7-first.png) | [on](step7-on.png) | [off](step7-off.png) | 0.626 | 7.428 / 6.391 |

完整/恢复 on PNG 的 SHA256 相同：1889B0F9DF5A4FC19B4B922680AF5623BBAF464C658BA2DA16A95482CA21EFA0。

| 屋顶完整、SunOnly、I OFF 的 B 对照 | 开 | 遮挡首帧 | 遮挡稳定 | 重开 |
|---|---|---|---|---|
| 步骤 2 | [open](blocker-step2-open.png) | [first](blocker-step2-closed-first.png) | [closed](blocker-step2-closed.png) | [reopened](blocker-step2-reopened.png) |
| 步骤 3 | [open](blocker-step3-open.png) | [first](blocker-step3-closed-first.png) | [closed](blocker-step3-closed.png) | [reopened](blocker-step3-reopened.png) |
| 步骤 4 | [open](blocker-step4-open.png) | [first](blocker-step4-closed-first.png) | [closed](blocker-step4-closed.png) | [reopened](blocker-step4-reopened.png) |

- 三种室外模式：lighting-SunOnly / SkyOnly / SunAndSky-on.png 和 -off.png。
- 室内灯比较：interior-lamps-on-gi-on.png / -gi-off.png 与 interior-lamps-off.png。
- 相机：camera-0-view0、camera-1-view1、camera-2-view0，各有 first / 稳定图；相机变化影响采样噪声，不用于等视角 on/off 比较。
- 额外 J 单独显示：[indirect-only.png](indirect-only.png)，1250 dispatch / 5000 spp，条件见同名 JSON。该图不是正式 260 spp 对照或性能测量。
- 每张图同名 JSON 记录状态、相机、分辨率、灯光、GI 开关和变化后帧数/时间。*-indirect.exr 保留 HDR GI 与 alpha eye-depth。GI OFF 的 EXR 是保留但未合成的缓存，不表示关闭后仍渲染 GI。
- [summary.json](summary.json)：50 条图像观测与性能窗口。
- [transport-checks.json](transport-checks.json)：25 组世界空间诊断、28/28 数值检查通过；与相机无关，BounceOnly 排除直接天空。同步诊断成本不属于逐帧成本。
- [geometry-checks.json](geometry-checks.json)：九项通用事件/数据检查通过。
- [GI_Validation.json](GI_Validation.json)：原夹具几何/太阳可见性审计通过；它本身不证明 GI 正确。

质量限制、未可靠测得项目、完整逐项状态见工程根目录 IMPLEMENTATION.md。所有 PNG 未调曝光或额外增亮；暗图与恢复图按原样保留。
