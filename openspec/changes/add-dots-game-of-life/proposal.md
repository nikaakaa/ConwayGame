## Why

为笔试交付一个可直接操作的 Unity 生命游戏演示，展示统一规则如何产生稳定、震荡和移动图案。使用 Entities、Jobs、Burst 和原生容器实现无预设边界的稀疏计算，并提供基本技术分析和使用文档。

## What Changes

- 实现 Conway B3/S23 规则，整代同步更新，棋盘支持正负整数坐标，不设置固定宽高或环绕边界。
- 使用一个棋盘 Entity，以组件保存运行状态、动态缓冲区保存正式活细胞坐标；每代仅计算活细胞及其八邻域，屏幕外继续演化。
- 由 `LifeSimulationSystem : ISystem` 管理生命周期、计时和作业依赖，使用 Burst Job 收集候选并并行计算下一代，完成后写回 ECS 缓冲区。
- 键盘和鼠标操作通过指令缓冲区按顺序交给 System，MonoBehaviour 负责输入和显示，不另存或独立推进世界。
- 提供 Block、Beehive 两种稳定图案，Blinker、Toad 两种二周期震荡图案，Glider、Lightweight Spaceship 两种移动图案。
- 题目中“循环震荡”的含义未被严格定义，额外提供 Pulsar 和 Pentadecathlon 两种较长周期震荡图案。所有图案仅提供初始坐标，统一执行生命游戏规则。
- 画面只显示黑底白色活细胞，不显示按钮、面板、文字、字体或悬停框；操作说明放在交付文档中。
- 从结构库选择摆放工具，不改变世界。左键点按把所选预置相对鼠标世界坐标叠加放入已有世界；右键点按或拖动连续擦除单格。编辑自动暂停并保留代数。
- 使用 Space 开始或暂停、N 单步、C 清空、R 在原点重置当前工具预置、Home 复位视野、上下方向键调速；中键平移，滚轮采用四档缩放并保留鼠标指向的世界格子。
- 提供 README 和技术说明，解释规则、数据流、DOTS 使用范围、效率取舍和实际检查结果。
- 本次范围不包含 AOI、分块调度、稳定区域休眠、HashLife、Compute Shader、逐细胞 Entity 或 GameObject，不引入 Entities Graphics、Unity Physics 或 SubScene。

## Capabilities

### New Capabilities

- `life-simulation`：无预设边界的 B3/S23 稀疏演化、同步更新、演化执行和内存管理。
- `life-demo`：黑白棋盘、预置摆放工具、鼠标擦除、键盘演化控制、观察窗口导航和交付文档。

### Modified Capabilities

无。当前 `openspec/specs/` 为空，也没有 `openspec/project.md` 或已有生命游戏实现需要迁移。

## Impact

- 目标项目使用 Unity 6000.3.3f1，保留现有 URP 和 UGUI。
- 实施时在 `Packages/manifest.json` 中直接声明 Entities 1.4.8、Burst 1.8.29、Collections 2.6.8 和 Mathematics 1.3.3；按 Entities 的依赖要求更新锁文件。
- 新实现集中在 `Assets/GameOfLife/`，演示入口为 `Assets/GameOfLife/Scenes/GameOfLife.unity`。
- 交付文档为根目录 `README.md` 和 `Docs/GameOfLife.md`，技术说明记录计算、显示及原生扩容各自的成本。
- 本轮仅维护变更规划，不修改现行 specs、项目代码、包配置或场景。
