# 性能分析方法论 Playbook（源自团队内部性能分析 wiki，已转译为 AI Profiler 独立口径）

> 方法论来源：团队内部性能分析 wiki 及其 20 篇子文档（内部链接已脱敏移除）
>
> **定位**：AI Profiler 是**独立工具**——一切分析、判断都从自身采样数据（帧时间线 / C#·Lua 热点 / GPU·内存计数器 / GC）得出，**不依赖 PerfDog、Perfetto、SimplePerf 等外部采集**。原 wiki 中依赖外部工具的方法，本文已转译为「采样数据内可独立判定的信号 + 启发式规则」；采样数据覆盖不到的维度，报告**如实说明边界并给代理判断**，不把「去跑某外部工具」当作分析结论。
> 本文供 profiler-analysis 在 triage（Step 2）与 subagent 深挖（Step 3）时套用：「看到什么信号 → 优先怀疑什么 → 用什么数据定性」。

## 〇、五模块分诊框架（triage 的第一层分类）

对任何性能问题，先归入五个模块之一。每个模块都列出 **AI Profiler 数据内的独立判定信号**：

| 模块 | 严重性定位 | AI Profiler 内独立判定信号 |
|---|---|---|
| **内存** | 最高优——唯一会产生阻断性影响（闪退）的模块 | MEMORY 计数器 min/avg/max/last：总量水位、录制期间趋势是否持续上升（泄漏疑点） |
| **CPU-卡顿耗时**（一次性：启动/切场景/UI 打开） | 直接可感知，按流程节点分段 | 帧尖刺榜 + 尖刺帧关联的热点 marker（shader-compile / load-burst / teardown-storm 模式） |
| **CPU-持续耗时**（稳态：逻辑/渲染/物理/UGUI/动画） | 高端机常良好，**主要关注低端机** | 热点榜稳态项（calls≈walkedFrames 的每帧常驻）+ 常驻浪费模式命中 |
| **GPU** | 看临界水位 | GpuFrameTime vs CpuFrameTime、`Gfx.WaitForPresent*` 等待、三角形/DC 计数器 vs 预算 |
| **功耗发热** | 前四者的综合结果 + 降频/分档问题 | **代理判定**（§五）：稳态帧均 CPU 水位、WaitForTargetFPS 喘息占比、常驻浪费模式、渲染计数器超预算 |

脚本的「独立瓶颈 / 功耗代理画像」段已把上述信号自动汇总成启发式判断，triage 从那里起步。

两条通用原则：
- **一次性 vs 稳态分开治理**：同样字节/耗时，稳态每帧成本远重于一次性 init（对应 SKILL.md 复发闸）。
- **场景切换类问题先钉死「切换窗口」三个数**：帧区间 + 窗口均帧 vs 基线均帧 + 尖刺帧清单（例：主城→某玩法场景 135-190 帧/56 帧/4.57s，均帧 81.6ms vs 基线 40.3ms）——全部可从帧时间线直接算出。

## 一、内存分析角度

**采样数据内可独立判定**：
- MEMORY 计数器的 min→last 走向：录制覆盖「反复进出 UI / 挂机」时 Total Reserved / GC Reserved 单调上升 = 泄漏疑点，直接给判断。
- 泄漏观察窗口就三个：**新手流程、UI 反复进出、长时间挂机**——建议用户按这三个窗口采样，而不是无差别全量扫。
- `GC Allocated In Frame` 高位 + GC 归因榜 = 分配源独立定位（本 skill 主战场）。

**旁证推断**（数据内信号 → 内存问题假设，报告给假设与依据）：
- C# 榜命中 `shader-compile` 模式（Shader.CreateGPUProgram 现场编译）= **Shader 变体冗余**的旁证——同一 shader「场景材质引用一份 + AB 加载一份」双份驻留时，往往同时出现重复编译卡顿。内存与卡顿双证据互相印证时优先级高。
- 已知高危驻留对象类型：变体数百上千的 shader、RW（Read/Write enabled）纹理（CPU 侧双份，地表 T2DArray、TMP 静态大图集是常客）——报告可列为「建议核查项」，给出为什么怀疑。
- 「预留/碎片占比高」≠ 对象泄漏，趋势判断时分开表述。

**边界**：对象级归属（哪个纹理/网格/Native 对象占了多少）超出采样数据，报告如实说明「本数据只能判趋势与分配源，对象级归属需内存快照类深挖」，并把旁证假设写清楚——不能因为有边界就不给判断。

## 二、启动 / 加载 / 场景切换分析角度

### loading 窗口耗时构成（采样数据可直接算，核心角度）
- **统计 loading 窗口内主线程耗时的「与加载相关 vs 无关」占比**：加载相关 = 加载/实例化/初始化入口；无关 = 渲染、物理、`WaitForTargetFPS` 空转。实测曾发现 300 帧主线程 19s 中只有 9s 在干正事——**其余全是可回收的 loading 时长**。
- **loading 窗口出现大量 `WaitForTargetFPS` / 低加载占比 = 异步加载权限过低的强信号**（`backgroundLoadingPriority` 恒 Low、每帧合并时间片 1ms 之类）。实测 Low→High 登录 34s→15s。loading 期无画面可保帧，加载优先级应拉满。
- **loading 期渲染占比高 = 白画**：黑屏/静态 loading 图期间主相机仍在画完整场景，纯浪费，可屏蔽。
- 此角度同样适用于进出 3D 战斗场景。

### 启动白屏（进程起动 → 首场景）
- 分段思路：把总耗时切成「首包拷贝（仅首次）→ Shader 预热 → 引擎 init → 首场景加载」，**用屏蔽对照定性**：临时关掉候选环节看总耗时降多少（Shader 预热曾被此法证实占 ~52s）。
- 大头假设序：Shader 变体预热 > 首包拷贝 > 引擎 init。Debug/Release、首次/非首次分开测（曾出现 debug 13s / release 33s 的反直觉差异）。
- 引擎 init「空窗」期第三方 SDK 线程可能在抢 CPU（曾测出 ~7s），采样若覆盖该窗口，看热点榜里的非引擎条目。

### 场景切换（A→B）三件套（多场景实测的共性结构，脚本已打 pattern 标签）
1. **`shader-compile`**：新场景首次渲染某材质（尤其透明物体）触发变体现场编译卡主线程（单条可达 235ms）——变体未被预热 SVC 覆盖。修法是补收集变体（参照 shader-variant-stripping 规则），不是改 pragma。
2. **`load-burst`**：地形/FOD chunk 等资源引用加载挤在一帧（26 chunk/118ms 一帧）——目标是「摊平尖峰」（按帧预算分摊）而非消除总量。
3. **`teardown-storm`**：`Destroy` 上千次 + 逐对象 Addressables Release + 伴生逻辑耗时，对象数越多越重。
- **`passive-wait`（Semaphore.WaitForSignal）是被动等待**（等加载线程/等 GPU present），不是独立问题，随主因缓解而降——不要单独立项。

### UI 打开延迟（热点榜上无痕迹的感知卡顿）
- 模式识别：点击 → `Request` → **等回包 → 才 ShowView** 的串行等网（2-12 帧不稳定）。CPU 上看不到，靠「交互后无热点但有可感延迟」的现象 + 代码走查定位：grep「回包回调里才 Open/Show」的入口。
- 修法方向：先用缓存渲染立即 ShowView、回包后刷新（View 往往本就按此设计）。
- 警惕失效守卫：`if not self.isOpen then return end` 而 `isOpen` 从未赋值之类的死代码。

## 三、渲染 CPU / 合批 / UGUI / 动画 / 物理分析角度

### 渲染压力判定（采样数据内信号）
- **主线程等待 marker 是渲染/GPU 压力的独立探针**：`Gfx.WaitForPresentOnGfxThread`（等 GPU 出帧 = 渲染/GPU 侧 bound）、`Semaphore.WaitForSignal`（等加载线程或 GPU）。配合 GpuFrameTime vs CpuFrameTime 计数器给 bound 类型判断（脚本画像段已自动做）。
- **渲染压力高 ≠ DC 多**：DC 不到 200 时渲染侧仍可高压，大头可能是 **MeshSkinning**（大量角色蒙皮，`skinning-pressure` 模式）。定性法完全独立可做：**清屏对照**（画面无角色时是否恢复）+ **开关 CPU/GPU Skinning 各采一次对比**。改 CPU Skinning 可让蒙皮与下帧逻辑并行、不 bound。
- **开关 ABTest 是渲染归因的基本功**（游戏内画质/模块开关 + 分别采样，不需要任何外部工具）：逐次开关 UI 表现、特效、模型、角色、地表、阴影、后处理、各 gameplay。**严谨做法是「关一个→采样→打开→再关下一个」**，不是累积式全关——否则差值无法归因到单模块。

### 合批
- 计数器独立判断：DC / SetPass / Batches avg·max 水位 + 「Batches ≈ DC」= 合批率低的信号。
- 结构性漏批的已知形态（报告作为核查项给出，附依据）：小物件/角色未走 SRP Batcher（shader 不兼容，如 LitLite）逐个画；图标未合图集一图一 DC；**TMP 符号未打进字体图集导致同一段文本拆多个 DC**（中文图集已含符号、其他语言不合批）；世界空间 HUD 气泡/红点可与内容合成一张小图。
- 「DC 不多但 CPU 高」的项也要看（树 PreDepth 32DC 但开销高）。
- 逐 Pass/逐事件细节超出采样数据——报告给出上述结构性假设与计数器依据即可，标注属深挖方向。

### UGUI 更新（三类变化 → 三个 marker 的映射，采样数据直接命中）
| UI 变化类型 | 触发的耗时节点 |
|---|---|
| 1. Vertex 更新（换图、Text 内容、透明度） | `Canvas.SendWillRenderCanvases` + `Canvas.BuildBatch` |
| 2. Transform 更新（位移/旋转/缩放） | `Canvas.BuildBatch` + `CanvasRenderer.SyncTransform` |
| 3. 显隐（SetActive / Instantiate） | `CanvasRenderer.SyncTransform` |

- `SendWillRenderCanvases` 高 → 找每帧变内容的大 Vertex 元素；`BuildBatch` 高 → 动静分离差；`SyncTransform` 自身极便宜但 **calls** 高会顶起父节点（每帧 106 次 = 有东西每帧动 Transform）。
- 反查角度：世界空间 UI 理论上拖相机时 Transform 不该变——若在变，查是不是 **Scale 每帧被改**（距离缩放类设计）。曾靠此把 UI 更新 8ms 定位到 bubble/HUD 拖拽缩放，改 2D 方案后 3-4ms。
- 高危场景：大量 UI Particle 和 TMP 的界面。

### 动画
- `Animators.*` 稳态耗时 + calls 规模 = 独立信号；激活 Animator 数量是低端机敏感指标（~50 已偏高）。
- 优先假设：常驻场景对象（牛、车类）Culling Mode 配成 Always Animate 白烧——应 CullUpdateTransforms / CullCompletely。
- 动画**同时吃主线程和 Job 线程**（mecanim 曲线求值 + 蒙皮），是稳态功耗代理的重要一项（§五）。
- 量大的系统性方案：低分档动画对象上限、视域外停更/隔帧更新。

### 物理
- **场景里只要有 Static Collider，Unity 就会跑常驻 3D 物理模拟**（挂机 0.9-1.3ms/帧），即便 Overlap 事件恒为 0。`Physics.*` 稳态出现在热点榜即命中（`physics-idle-sim` 模式）。
- 判定「真需要模拟」只有两类：3D 物理回调、刚体效果——扫代码枚举刚体使用点即可独立定性。
- 修法：`Physics.simulationMode` 改 Script（点击交互 Raycast 不受影响，模拟耗时清零）；需要刚体的玩法内代码调用模拟或临时切回。
- **QA 验证清单随修法一起给**：所有点击交互、射击命中、各核心玩法交互、寻路。

## 四、GPU 分析角度

**采样数据内可独立判定**：
- **同屏三角形预算**：低端机 10-15w。Triangles avg/max 直接对照（曾测某玩法场景 42w 远超、高画质加阴影 90w 高端机也撑不住、主城拉远 25-30w 短停留可接受）。
- **GPU bound 判定**：GpuFrameTime avg 逼近/超过 CpuFrameTime，或主线程 `Gfx.WaitForPresent*` 显著——真机（device）模式下这两项相对可信；Editor 口径 GPU 仅供参考，判断时脚本会自动附注。
- 阴影使三角形近似翻倍——Tri 高时先问阴影覆盖面。

**旁证推断**：Tri 超预算时的归因假设是「一类模型 × 数量」的乘积大户（7k 顶点的石头摆 7 个 = 10w tri）——报告给出假设与计数器依据，指明按场景资产核查；已做 shader 分级的部分（低端水体已极简）先排除，别重复怀疑。

## 五、功耗发热 —— 采样数据的代理判定（AI Profiler 独立口径）

功耗没有硬件功率计可读，但**功耗问题的根源就是采样数据里的稳态负载**——本节把功耗分析转译为四类代理信号，全部可从本采样独立判定（脚本画像段自动输出）：

### 四类代理信号
1. **芯片喘息占比**：`WaitForTargetFPS` 是 CPU 的「空闲呼吸」。稳态帧里它占比健康 = 芯片有余量；**帧均 CPU 高且几乎无 WaitForTargetFPS = 芯片持续满载画像 → 发热/降频风险最高的场景**。
2. **常驻浪费模式**：`physics-idle-sim` / `animator-cull` / `ugui-vertex·transform·rebatch` / `skinning-pressure` 命中 = 每帧白烧的功耗点。**功耗优化的第一杠杆就是清这些稳态浪费**——它们不制造卡顿感知，却持续产热。
3. **渲染计数器超预算**：Tri/DC 高位 = GPU 与内存带宽侧的功耗压力（GPU 计算 + 带宽是移动端功耗大头之二）。
4. **GpuFrameTime 高位**：GPU 侧持续接近满载（device 模式相对可信）。

### 三条推理规律（与工具无关的物理事实，决定优化排序）
1. **非线性**：芯片频率越接近额定，功耗上升越陡——**把持续满载的场景压下来一点，收益最大**；反之压力越集中（如全压主线程/单模块）越容易触发降频。
2. **木桶短板**：CPU/GPU/带宽各自的「预算」不可互相挪用——总量看似合理但单一模块持续高压，仍会独立触发该模块过热保护并带着周围升温。判断时对每类代理信号**分别**给结论，不做总量抵消。
3. **一次性 vs 稳态**：功耗只由稳态决定。尖刺帧不影响发热，帧均水位与常驻模式才影响——这与卡顿分析的权重相反，报告里分开表述。

### 优化策略三板斧（产品级机制，不依赖任何工具）
1. **清理高压/浪费模块**：按上面四类信号逐项处理（本文各节的产出）。
2. **负载降级机制**：检测到持续高负载时降画质/帧率（滞后防御）。
3. **动态帧率**：对帧率不敏感的场景（挂机/静态界面）高画质档也降 30 帧；同理「不敏感场景降清晰度」「长时间无操作降帧」。注意流程视角：新手流程若持续覆盖满载场景，积热降频会更早出现。

### 边界（如实说明，不作为分析路径）
硬件级功耗定量（整机 mW / 分模块功率 / 实时频率与温度）超出采样数据边界。报告基于代理信号给独立判断与优化方向即可；若用户明确要硬件数值，说明这是本工具边界外的验证手段，**不要把「先去测功耗」写成分析结论**。

## 六、多设备对比（device 模式的独立玩法）

- 真机连接（device）模式下，**同一场景、同一操作在不同设备各采一份**，对比：帧均 CPU、GpuFrameTime、尖刺分布、模式命中差异——独立完成设备间定性。
- 某设备独有的异常（如渲染等待占比畸高）→ 先怀疑设备特性（核配置/散热），**同一现象至少两台设备复现再定性为项目问题**。
- 测试环境规范：清后台、室温启动、满电（排除低电量降频干扰），否则设备间不可比。

## 七、特征 marker → 优先假设速查（脚本已自动打 pattern 标签）

| pattern | 特征 marker | 优先假设（章节） |
|---|---|---|
| `shader-compile` | `Shader.CreateGPUProgram` | 变体现场编译，未预热/未收集（§二 三件套1；内存冗余旁证 §一） |
| `passive-wait` | `Semaphore.WaitForSignal` | 被动等待，跟随主因不单独立项（§二） |
| `idle-wait` | `WaitForTargetFPS` | 喘息帧；loading 窗口出现 = 加载权限过低（§二）；稳态占比是功耗代理（§五） |
| `gpu-bound-wait` | `Gfx.WaitForPresent*` | 主线程等 GPU 出帧 = 渲染/GPU bound（§三/§四） |
| `ugui-vertex` | `Canvas.SendWillRenderCanvases` | 每帧变内容的大 Vertex 元素（§三 UGUI 表） |
| `ugui-transform` | `CanvasRenderer.SyncTransform` | 有 UI 每帧动 Transform/显隐，看 calls（§三） |
| `ugui-rebatch` | `Canvas.BuildBatch` | 动静分离差（§三） |
| `skinning-pressure` | `MeshSkinning.*` | 蒙皮压渲染侧，非 DC 问题（§三） |
| `physics-idle-sim` | `Physics.*` | Static Collider 常驻模拟（§三 物理） |
| `teardown-storm` | `*.OnDestroy` / `CoroutinesDelayedCalls` | 拆旧销毁风暴（§二 三件套3） |
| `load-burst` | `LoadAssetAsync` / `RefChunk` / `ChunkTerrain` | 集中单帧加载，摊平尖峰（§二 三件套2） |
| `animator-cull` | `Animators.*` / `Animator.*` | Always Animate / cull mode / 数量（§三 动画；功耗代理 §五） |

**边界总则**：采样数据覆盖不到的维度（对象级内存归属、逐 Pass 事件、硬件功耗定量、服务器回包延迟）——报告**给出代理判断 + 旁证假设 + 如实的边界说明**，不写「无法分析」，也不把「去用某外部工具」当结论。
