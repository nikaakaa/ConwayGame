## Context

需求动机见 [proposal.md](proposal.md)。行为分别见 [演化规格](specs/life-simulation/spec.md)和[演示规格](specs/life-demo/spec.md)。

项目当前只有 Unity 模板场景，版本为 6000.3.3f1，使用 URP 17.3.0 和 UGUI 2.0.0。包锁文件已解析 Burst 1.8.27、Collections 2.6.2 和 Mathematics 1.3.3，尚未安装 Entities。`openspec/specs/` 为空，没有现行功能规格或旧实现需要迁移。

演示画面只保留黑底白色细胞。预置选择、演化控制和调速通过键盘完成，鼠标负责预置摆放、擦除与观察窗口导航，不加入按钮、文字、字体或悬停对象。

## Goals / Non-Goals

**Goals:**

- 由棋盘 Entity 的组件和缓冲区保存唯一正式世界状态，System 处理指令并推进演化。
- 使用 Entities、Jobs、Burst 和 Collections，同一套稀疏算法处理全部图案。
- MonoBehaviour 负责输入和显示，计算工作量由活细胞及其邻域决定。

**Non-Goals:**

- 不建立 AOI、空间分块、休眠调度、周期缓存或第二套模拟器。
- 不为每个细胞创建 Entity 或 GameObject，不引入 Entities Graphics、Unity Physics、SubScene 或烘焙流程。
- 不加入随机生成、文件导入导出、撤销或无级缩放，不承诺未经测量的性能。

## Decisions

### 1. 直接引用 Entities，使用一个棋盘 Entity

Entities 负责组件数据、System 生命周期和作业依赖，Jobs 与 Burst 负责具体计算，Collections 提供工作容器。职责对应 [Unity DOTS 包说明](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/ecs-packages.html)。

本次为棋盘创建一个 Entity，细胞坐标存入动态缓冲区。ECS 的实体粒度由业务决定，不要求一格一个 Entity。这样保留无预设边界的稀疏数据，也避免逐细胞实体的管理开销。

实施时直接声明 Entities 1.4.8、Burst 1.8.29、Collections 2.6.8 和 Mathematics 1.3.3。Unity Registry 中 Entities 1.4.8 和 Collections 2.6.8 的依赖均要求 Burst 1.8.29，不能继续固定旧的 Burst 1.8.27 和 Collections 2.6.2。依赖以 [Entities 包元数据](https://packages.unity.com/com.unity.entities)和 [Collections 包元数据](https://packages.unity.com/com.unity.collections)为依据，其余传递依赖由 UPM 解析并写入锁文件。

### 2. 正式状态存于组件，哈希集合只是工作数据

棋盘 Entity 持有以下组件和缓冲区：

| 数据 | 职责与写入者 |
| --- | --- |
| `LifeSettings : IComponentData` | 保存每秒推进代数，由 System 根据调速指令修改 |
| `LifeState : IComponentData` | 保存运行状态、代数、累计时间和显示版本，由 System 修改 |
| `DynamicBuffer<AliveCell>` | 保存当前已公布状态的全部活细胞坐标，由 System 加载、手工编辑或整代写回 |
| `DynamicBuffer<LifeCommand>` | 接收界面操作，控制器追加，System 按顺序处理并清空 |

`AliveCell` 是包含 `int2` 坐标的 `IBufferElementData`。不在缓冲区中的位置表示死亡。活细胞数量直接使用缓冲区长度，不再存一份计数。动态缓冲区的职责见 [Entities 官方说明](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/components-buffer.html)。

`LifeSimulationSystem : ISystem` 私有持有并复用以下原生工作容器：

| 容器 | 职责 |
| --- | --- |
| `AliveLookup`，`NativeParallelHashSet<int2>` | 从当前缓冲区生成的只读邻居查询索引 |
| `NextAlive`，相同类型 | 本次演化的下一代输出 |
| `CandidateSet`，相同类型 | 对活细胞自身及其八邻域去重 |
| `Candidates`，`NativeList<int2>` | 供并行索引访问的候选坐标 |

`AliveLookup` 不是第二份可写世界，不供界面读取或修改；每代从正式缓冲区重新生成。`NextAlive` 只有完成计算并写回缓冲区后才成为新一代。

坐标支持正负整数，没有棋盘宽高、裁剪或环绕。无限表示无预设边界，实际范围仍受整数坐标、内存和算力限制。

### 3. System 负责整代演化和作业依赖

`LifeSimulationSystem` 在 `SimulationSystemGroup` 中更新，`OnCreate` 创建唯一棋盘 Entity、工作容器并加载暂停的 Block 第零代。`OnDestroy` 释放工作容器，组件和动态缓冲区随 ECS World 释放。

设当前活细胞数量为 `L`，候选数量为 `C`，有 `C <= 9L`。一次推进依次完成：

1. 从正式缓冲区取得当前坐标输入，清空工作容器，在调度边界准备邻居索引、候选集合和列表容量。
2. 立即运行 Burst 编译的 `CollectCandidatesJob : IJob`。遍历当前坐标，生成 `AliveLookup`，对自身和八邻域去重，并输出连续候选列表。
3. 根据实际 `C` 准备 `NextAlive` 容量。并行写入期间不扩容。
4. 调度 Burst 编译的 `EvolveJob : IJobParallelFor`。每项只读一个候选坐标和邻居索引，按 B3/S23 决定生死，通过正式并行写入器加入下一代。
5. 完成相关作业，将 `NextAlive` 坐标写回 `DynamicBuffer<AliveCell>`，随后增加代数。写回前的所有规则计算均读取同一代数据。

System 管理作业依赖，读取、扩容和写回缓冲区前完成相应作业。缓冲区的原生数组视图只在本次作业期间使用，不跨扩容或下一次更新持有。本次在同一轮更新中完成演化并发布结果，接受完成作业时的等待成本，不保留跨帧在途作业。

候选收集使用单个写入者，生死计算使用并行作业，不做并行共享计数累加。加载图案的主线程路径读取图案目录，目录中的名称和托管数组不传入 Burst 作业。System 的职责和生命周期见 [ISystem 官方说明](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/systems-isystem.html)。

### 4. 控制器通过指令缓冲区操作世界

`LifeController.selectedPattern` 是当前摆放工具索引的唯一来源，初始为 Block。数字键 1–8 按 `LifePatterns.Items` 的原顺序改变工具索引，不发送世界修改指令，也不改变演化状态。System 不再保存所选图案或已编辑标记。

左键每次按下时，控制器读取当前工具的 `Cells`，逐格把局部坐标加到鼠标世界坐标上，再向 `LifeCommand` 缓冲区追加 `SetCell` 存活指令。已有世界保留，重叠坐标仍然唯一。右键擦除使用相同的 `SetCell` 指令，目标状态为死亡。控制器不调用 System 私有推进方法，不直接改写正式细胞数据。

System 每次更新先按到达顺序处理全部控制指令，再根据运行状态决定是否自动推进。单步指令先暂停并清空累计时间，再推进一代。同一轮收到两次单步就分别推进两代，使用缓冲区保留两次操作，不用一个布尔标志覆盖请求。指令处理完后清空缓冲区，不增加另一套消息框架。

按 R 时，控制器使用当前工具索引追加 `LoadPattern` 指令，并恢复原点中心和默认缩放。System 的图案加载路径清空世界，再把当前工具的预置坐标加载到原点，恢复第零代与暂停状态。选择工具本身不调用此加载路径，不保留另一条 `Reset` 指令入口。

System 处理 `SetCell` 时先暂停并清空累计时间，再把指定格子设为存活或死亡，维持存活坐标唯一。摆放和擦除不增加代数，重复设为相同状态也不发生翻转。

按 C 追加的 `Clear` 指令清除全部细胞、将代数归零并暂停，保留控制器的摆放工具、观察位置和缩放。之后可以叠加摆放新的图案。摆放、擦除、清空、重置和单步按同一个指令缓冲区的顺序执行，摆放后紧接单步会使用编辑后的状态。

Space 追加开始或暂停指令，N 追加单步指令。上下方向键每次把速度增加或减少一代，范围为每秒一至三十代，默认每秒十代。控制器读取已公布的运行状态和速度来生成指令，System 使用 ECS 时间累积器推进。暂停、单步和调速时清空累计时间，控制器不持有演化计时器。

### 5. 图案只提供初始数据

`LifePatterns` 是八种图案名称、类别、已知周期、位移和初始坐标的唯一来源，数字键工具、叠加摆放和原点重置读取这份目录。图案资料只保留在目录与交付文档中，不生成画面文字。

Block、Beehive 的周期为一，Blinker、Toad 的周期为二，Pulsar 的周期为三，Pentadecathlon 的周期为十五。Glider 与 Lightweight Spaceship 都在四代后恢复形状并产生位移。周期是图案资料，不实现自动周期识别，不按周期播放动画。资料参考 [LifeWiki](https://conwaylife.com/wiki/)。

### 6. 鼠标输入与显示共用观察窗口坐标

`LifeView` 持有整数窗口原点和可见格子数，负责世界坐标、纹理像素和屏幕坐标之间的转换。`LifeController` 使用这份转换结果提交编辑指令，不另建坐标换算路径。

键盘与鼠标操作约定如下：

| 操作 | 行为 |
| --- | --- |
| 数字键 1–8 | 按目录原顺序选择摆放工具，不改变世界 |
| 左键点按 | 在鼠标世界坐标叠加摆放当前预置一次，自动暂停 |
| 右键点按或拖动 | 把经过的格子设为死亡，自动暂停 |
| 中键拖动 | 平移观察窗口，不暂停或修改世界 |
| 滚轮 | 以指向的格子为锚点，在有限档位间缩放 |
| Space | 开始或暂停 |
| N | 暂停并推进一代 |
| C | 清空、暂停并归零代数，保留工具和视野 |
| R | 清空后在原点加载当前工具预置的第零代，暂停并恢复默认视野 |
| Home | 恢复原点中心与默认缩放，不修改世界 |
| 上下方向键 | 每次增加或减少每秒一代，范围为 1–30 |

使用项目已有的 Input System 1.17.0，通过 `EventSystem`、`InputSystemUIInputModule` 和 UGUI 棋盘指针事件接入鼠标，见 [官方 UI 支持说明](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.17/manual/UISupport.html)。键盘由控制器读取，鼠标编辑事件只挂在棋盘区域，棋盘外操作不提交细胞编辑。

屏幕位置先通过棋盘 `RectTransform` 转成区域内坐标，再根据窗口原点和可见格子数映射到整数世界格子，向下取整以支持负坐标。左键按下只摆放一次，不沿拖动路径重复摆放。右键拖动按相邻采样点之间的整数线段补齐擦除格子，采用 Bresenham 步进，不为每格分配对象或集合。擦除轨迹离开棋盘时中断，重新进入后从新点继续，不连接离开期间的轨迹。

视口固定为方形，可见范围采用每边 32、64、128、256 格四档，默认 128 格。缩放保持鼠标下的世界格子不变，缩放到最小或最大档后保持该档。平移、缩放和 Home 复位只改变显示。

`LifeView` 使用一张预分配的 `256 × 256` 纹理和 `NativeArray<Color32>`，点过滤，一像素对应一格。像素作业使用固定纹理行跨度写入当前窗口，通过 `RawImage.uvRect` 选取对应大小的显示区域，切换缩放档位不重建纹理。

视图在 `LateUpdate` 只读 System 已公布的细胞缓冲区。单个 Burst 像素作业先将复用像素填为纯黑，再遍历活细胞，只把窗口内坐标写成纯白。作业完成后由主线程上传纹理，缓冲区视图不跨帧保留。没有 AOI 时，显示仍遍历全部活细胞并剔除离屏坐标。

每帧最多刷新一次棋盘纹理，暂停且没有世界或窗口变化时不重复上传。System 的 `Revision` 与视图窗口变化控制失效更新，纹理和像素缓冲区由视图创建与释放。`LifeView` 不再持有文本、字体、悬停框或操作提示。

### 7. 文件按数据、演化和界面职责组织

| 文件 | 输入、处理与输出 |
| --- | --- |
| `Assets/GameOfLife/Runtime/LifeComponents.cs` | 定义棋盘配置、运行状态、活细胞和控制指令的数据结构 |
| `Assets/GameOfLife/Runtime/LifeSimulationSystem.cs` | 创建棋盘、处理指令、管理工作容器与作业，将结果写回 ECS |
| `Assets/GameOfLife/Runtime/LifePatterns.cs` | 提供八种图案数据，供选择入口和 System 加载共同读取 |
| `Assets/GameOfLife/Runtime/LifeView.cs` | 持有观察窗口与坐标转换，只读正式缓冲区，生成黑白棋盘纹理 |
| `Assets/GameOfLife/Runtime/LifeController.cs` | 接收键盘和棋盘鼠标事件，维护摆放工具，把世界修改追加为既有指令，连接窗口平移与缩放 |
| `Assets/GameOfLife/Editor/LifeSceneBuilder.cs` | 创建 Camera、Canvas/Board、EventSystem 并装配 Controller 与 View，保存演示场景 |

正式入口为 `Assets/GameOfLife/Scenes/GameOfLife.unity`，场景仅包含 Camera、Canvas/Board、EventSystem、Controller 和 View 所需装配。画面没有面板、按钮、文字、字体和悬停框。使用默认 ECS World 和自动创建的 System，不增加自建 World、烘焙或 SubScene 流程。

### 8. 分开说明计算、写回和显示成本

候选收集生成查询索引，演化检查 `C` 个候选的八邻域，结果写回遍历下一代活细胞。在通常的哈希访问成本下，工作量随活细胞和候选数量增长，与坐标跨度无关。动态缓冲区和原生容器保留已分配容量，常驻内存取决于历史容量。

预置坐标叠加、擦除采样、线段补点和指令追加使用值类型及复用缓冲区，不产生逐格托管对象。手工编辑不会运行另一套演化算法。

稳态路径复用动态缓冲区、原生容器和像素缓冲区。不使用 LINQ、每代 `ToNativeArray`、托管集合重建或逐格字符串。初始化、图案加载和原生扩容单独记录，扩容没有托管 GC 也仍有分配与搬移成本。

技术说明分别记录索引与候选生成、并行演化、结果写回、像素生成、纹理上传和托管分配。实际测量须注明环境、规模、预热和采样范围，未测量时明确标注。Entities 的引入用于正式的数据和执行组织，不作为自动获得加速的依据。

## Risks / Trade-offs

- 邻居索引生成和结果写回增加线性处理成本 → 接受这项成本，使 ECS 缓冲区保持唯一正式数据源，不增加持久双写状态。
- 稀疏哈希访问的局部性弱于稠密数组 → 保持无边界存储和较小实现规模，不增加第二条计算路径。
- 世界增长会扩大计算、显示遍历和内存成本 → 记录实际资源限制，不裁剪离屏细胞或静默改变规则。
- 小图案的调度成本可能高于并行收益 → 将 DOTS 使用方式和实际性能分开说明。
- “循环震荡”不是明确的独立分类 → 同时提供移动图案和较长周期原地震荡，文档解释区别。

## Migration Plan

这是新增演示，没有既有玩法和序列化数据迁移。实施时先添加 Entities 及配套直接依赖并更新锁文件，再添加组件、System、界面、演示场景和交付文档。保留模板场景和其他项目资源，回退范围仅限本次内容及包依赖变化。

本轮只更新已有规划文件，不修改包配置、代码、场景或主规格。主规格为空，无冲突、修改或删除项；后续同步和归档仍按明确请求执行。
