## 1. 声明直接依赖

- [x] 1.1 在 `Packages/manifest.json` 中直接声明 Entities 1.4.8、Burst 1.8.29、Collections 2.6.8 和 Mathematics 1.3.3，并更新依赖锁文件。

## 2. 实现稀疏演化

- [x] 2.1 创建 `LifeComponents` 和 `LifeSimulationSystem`，由唯一棋盘 Entity 保存配置、状态、活细胞与指令缓冲区，由 System 完成初始化、顺序指令处理、计时和工作容器释放。
- [x] 2.2 实现 Burst 候选收集作业，从正式活细胞缓冲区生成邻居查询索引，对活细胞及八邻域去重，输出连续候选列表并在调度边界管理容量。
- [x] 2.3 实现 Burst 并行演化作业，统一执行 B3/S23，由 System 管理依赖并在完成后把整代结果写回活细胞缓冲区，支持负坐标和离屏持续演化。

## 3. 接通图案与演示

- [x] 3.1 创建唯一图案目录 `LifePatterns`，录入八种图案的初始坐标、名称、类别、周期和位移，数字键选择、摆放和重置均读取该目录。
- [x] 3.2 实现 `LifeView`、共享坐标转换和像素作业，使用固定纹理与复用缓冲区显示黑底白色细胞，支持窗口平移、四档缩放和鼠标锚点，不保留文字与悬停对象。
- [x] 3.3 实现 `LifeController`，接通数字键摆放工具、左键叠加预置、右键连续擦除、Space/N/C/R/Home/上下键控制和中键平移、滚轮缩放；世界修改统一提交既有指令缓冲区。
- [x] 3.4 在 System 中处理设定格子和清空指令，维持坐标唯一与编辑自动暂停；R 统一使用 `LoadPattern` 恢复预置初始坐标、第零代和暂停状态，删除无消费者的图案选择、已编辑组件字段及 `Reset` 指令。
- [x] 3.5 创建并装配 `Assets/GameOfLife/Scenes/GameOfLife.unity`，仅创建 Camera、Canvas/Board、EventSystem、Controller 和 View，画面只有黑底白色细胞，进入场景时显示暂停的 Block 第零代。

## 4. 编写交付文档

- [x] 4.1 编写根目录 `README.md`，列出 Unity 版本、演示场景路径、启动方式、键盘与鼠标操作、预置叠加摆放及重置规则和图案说明。
- [x] 4.2 编写 `Docs/GameOfLife.md`，说明规则、稀疏世界、ECS 数据归属、界面至 System 和 Job 的调用链、内存与执行成本，以及实际执行过的检查和未执行项。
