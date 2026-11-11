# 可破坏环境的动态全局光照

任务要求见 **[TASK.md](TASK.md)**。在本工程内实现可响应外部几何变化的实时 GI 系统。

Unity **6000.5.4f1**，URP **17.5.0**。

打开 `Assets/GIEvaluation/Scenes/GI_TestScene.unity`，使用顶部 **GI Evaluation > Start Interactive Test** 运行。左侧按钮或数字键 1–7 切换测试状态。

T 自动执行流程，R 复位，P 保存截图与状态。其他测试开关见任务手册中的测试表。

动态 GI 已实现：`G` 切换 GI，`O` 循环显示模式（正常/仅 GI/仅反弹/伪彩），右上角面板有开关和参数状态。算法、接入、实测性能和完整测试记录见 **[IMPLEMENTATION.md](IMPLEMENTATION.md)**；验证截图与 JSON 在 `Reports/DynamicGI_Final/`。
