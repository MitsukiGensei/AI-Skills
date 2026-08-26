# profiler-analysis — Unity 性能采样分析

分析 Unity Profiler 导出数据，多维度定位热点，由主 Agent triage、并行 subagent 深挖根因，产出带 `文件:行` 修复方案的中文报告。

本目录除技能本体外，还随附了**Unity 工程侧的采样/导出配套**（AI Profiler 面板、导出器、通用运行时采集器、无人值守菜单、Lua 后端抽象与 Miku 适配、纯 Lua 打点适配器），见 [unity/README.md](unity/README.md)。技能负责"导出之后"的分析，`unity/` 负责"导出之前"的采样——两者配套才是完整链路。

## 做什么

把 Unity 导出的性能采样文本（`Assets/ProfilerLogs/*.txt`）预处理成多维度热点榜单（CPU C#/Lua、GC Mono/Lua VM、高频调用、GPU 计数器、内存趋势、帧尖刺、界面打开、场景切换），再由 Agent 阅读对应工程源码，给出具体到 `文件:行` 的根因分析与优化建议。**分析阶段全程只读不改代码**，把报告落成代码要显式进入落地闭环（见「架构与机制」）。

脚本自动识别两种导出格式：`AI-Profiler-v1`（多源整合）与旧 Lua Profiler 纯 Lua 采样，默认过滤 Lua 后端 / EditorLoop / 仅编辑器存在的插桩噪声（工程自带插桩的特征配在 `scripts/profiler_config.json`），识别 `[Target]` 采样拓扑（Editor 本地 vs 真机连接）按对应口径解读，并对 C# 热点打 `pattern=` 模式标签指向分析角度库对应章节。

## 何时使用

- 想知道哪个 C# / Lua 函数耗时高、哪个函数被高频调用（含"单次便宜但每帧狂刷"的隐形热点）
- 排查 GC 分配源（Mono GC + Unity 拿不到的 Lua VM GC），或判断内存是否在泄漏/累积
- 定位帧率尖刺帧及其关联事件（加载、实例化、切场景）
- 排查哪个界面打开慢 / 点击无响应 / 开屏卡顿 / 节点浪费，哪次场景切换慢
- 优化落地后想验证收益是否真实到账（`--diff` 与优化前采样对比）
- 刚跑完一次采样，想要一份按 P0/P1/P2 排好的优化建议报告

## 何时不该用

- 逐 Pass / Shader 级 GPU 瓶颈 → Unity FrameDebugger / 真机 GPU profiler（本数据 GPU 是计数器级）
- 纹理 / 网格等对象级内存泄漏归属 → Memory Profiler 快照（本数据只判趋势与分配源）
- Render Thread / Job 线程热点 → 本 skill 只遍历 Main Thread 样本流，"C# 榜干净" ≠ "CPU 没问题"
- 包体大小、真机崩溃卡死 → 各自的专用 skill

## 使用方式

### 安装

技能本体（`SKILL.md` + `scripts/` + `references/`）复制到 Claude Code 技能目录。**推荐项目级安装**——脚本按自身路径反推项目根（`<项目根>/.claude/skills/profiler-analysis/scripts/` 往上 4 级），默认的 `Assets/ProfilerLogs` 相对于该根解析：

```powershell
# 项目级（推荐；默认路径自动生效）
Copy-Item -Recurse profiler-analysis "<项目根>\.claude\skills\profiler-analysis"

# 用户级也可以，但每次调用要显式传 --dir / --src-root（或在 profiler_config.json 里写绝对路径）
Copy-Item -Recurse profiler-analysis "$env:USERPROFILE\.claude\skills\profiler-analysis"
```

然后把 `scripts/profiler_config.example.json` 复制为 `scripts/profiler_config.json`，按项目填 Lua 源码根、工程自带插桩的噪声特征、框架分发入口（全部可选，不填就用内置通用默认）。

`unity/` 子目录是 Unity 工程侧配套，不需要进技能目录；按 [unity/README.md](unity/README.md) 合入工程即可（复制技能时可一并带上、也可剔除，不影响技能运行）。

### 触发

对话中说"分析最新的 profiler 采样"、"这次采样哪里慢"、"哪里 GC 高"、"看看最新那次采样有什么问题"即可触发完整流程；显式 `/profiler-analysis` 亦可。

### 脚本用法

```bash
# 默认分析 Assets/ProfilerLogs 下最新导出，自动识别格式
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --top 25

# 其他常用参数
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --file "Assets/ProfilerLogs/2026_05_25_15_00_00.txt"
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --list       # 列出可分析文件
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --json        # 机器可读输出（含 health 块、每条 noise 标签）
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --raw         # 关闭插桩过滤，看未过滤全貌
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --dir <ProfilerLogs> --src-root <Lua 源码根> --config <profiler_config.json>

# 基线对比：优化落地验证 / 版本回归，热点按帧归一后出回归榜与改善榜
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --file "<优化后导出>" --diff "<优化前导出>"
```

脚本仅依赖 Python 3 标准库，Windows 上 `py -3 <脚本>` 或 `python <脚本>` 均可。

## 文件结构

| 文件 | 说明 |
|---|---|
| `SKILL.md` | 技能主体：数据来源与口径、工作流（Memory 预检 → 完整性闸 → 归责保护 → 预处理 → triage → 扇出深挖 → 对抗验证 → 汇总）、12 类改法陷阱、报告质量红线、代码落地复核闸 |
| `scripts/analyze_profiler.py` | 确定性预处理脚本（纯 stdlib）：解析两种导出格式、过滤插桩噪声、信噪比体检、完整性诊断、role/pattern 标签、瓶颈与功耗代理画像、`--diff` 基线对比、解析 Lua 源文件真实路径 |
| `scripts/profiler_config.example.json` | 项目配置样例：`log_dir` / `src_root` / 工程自带插桩噪声特征 / Lua 框架分发入口（`role=framework-*`）。复制为 `profiler_config.json` 生效 |
| `references/perf-analysis-playbook.md` | 分析角度库：五模块分诊框架 + 内存/启动/切场景/渲染 CPU/UGUI/动画/物理/GPU/功耗代理各域「信号 → 优先假设 → 定性手段」；脚本 `pattern=` 标签直接指向对应章节 |
| `unity/` | Unity 工程侧配套（采样 + 导出），详见 [unity/README.md](unity/README.md) |

## 架构与机制

skill 是一条 **预处理 → triage → 扇出深挖 → 对抗验证 → 汇总** 的多 Agent 流水线，用户明确要求时再延伸出一条**落地闭环**。脚本只做确定性预处理（"把数据摆好"），定性与改法交给 Agent；每个独立问题派一个**只读 `Plan` 类型 subagent** 并发深挖，主 Agent 最后整合排序。两条扇出是该 skill 的核心——不是把榜单扁平照搬，而是按"一问题=一根因"切分后并行根因分析，再由**独立视角**逐条尝试推翻。

```
   Step 0  Memory 预检
      │   搜 profiler/performance/性能/采样/模块名/热点入口，复用已有经验；无命中也要记录
      ▼
   Step 0.5 数据完整性闸
      │   识别 walked 0、原生分段失败、Lua NO DATA；critical 缺失维度用灰灯/重采
      ▼
   Step 0.6 归责保护
      │   role=framework-* 入口（事件分发器 / Scheduler / Timer / Update 入口）只作链路证据
      ▼
   Step 1  脚本预处理（确定性，纯 stdlib）
      │   解析榜单 · 过滤插桩噪声 · 信噪比体检 · 完整性诊断 · role/pattern 标签
      │   · 独立瓶颈与功耗代理画像 · 解析 Lua 源文件真实路径
      ▼
   Step 2  主 Agent triage（只轻量定位，不深读）
      │   榜单 → N 个相互独立的问题（同根因 marker 合并）
      │   每问题记录：现象/指标 · 入口 文件:行 · 调用栈线索 · 样本证据片段 · 劣化程度初判
      ▼
   Step 3  扇出：每问题一个只读 subagent（一条消息并发派发）
      ├─ Lua CPU 根因      ├─ Lua VM GC 分配源     ├─ 业务 Mono GC 分配源
      ├─ 帧尖刺关联事件    ├─ 界面开启超标         └─ 场景切换超标
      │   （Plan 类型 = 无 Edit/Write，天然只读）
      │   （GPU 计数器与内存趋势不派 subagent，主 Agent 直接给趋势判断）
      ▼
   Step 3.5 对抗验证：改行为/依赖收益假设的 P0/P1 候选，各派一个独立 skeptic（refute-first）
      │   攻 真机收益证伪 / 可行性·合规证伪 / 正确性证伪
      │   → VERDICT_HOLDS · 降级 not-worth · 降级 owner-decision
      │   （提案者 ≠ skeptic：同一 agent 既提案又自检会为自己辩护，故独立视角来推翻）
      ▼
   Step 4  主 Agent 汇总
      │   去重 · 收益真实性闸 · 按「收益 × 确定性」排 P0/P1/P2 · 输出中文 markdown 报告
      │
      └─ Step 3.6（仅用户明确要求落地时）────────────────────────┐
             跨报告去重 → 重读 HEAD 判 MODIFY/REJECT/RESAMPLE/DEFER │
             → 修改 Agent → 独立 Reviewer(refute-first) ↺ 修正      │
             → 复审至 PASS → 独立 Checkout Agent 建单项 pending CL   │
             （REJECT/撤回也要 clean re-review 确认 0 残留）─────────┘
```

### 它编排的能力

| 触点 | 谁发起 | 说明 |
|---|---|---|
| `scripts/analyze_profiler.py` | skill 主动调（Step 1） | 确定性预处理；输出 META 各源状态 + 信噪比体检 + 各维度榜单 + 瓶颈画像 + 待阅读 Lua 源文件 |
| `Plan` 类型 subagent ×N | 主 Agent 主动派（Step 3） | 每个独立问题一个，并行深读源码定根因、给 `文件:行` 修复方案 |
| `Plan` 类型 skeptic ×M | 主 Agent 主动派（Step 3.5） | 对改行为/依赖收益假设的 P0/P1 候选，独立视角 refute-first 对抗验证（≠ 提案者），据结论调级 |
| 修改 / Reviewer / Checkout Agent | 主 Agent 主动派（Step 3.6） | 落地闭环三角色分离，修改者不得自审；Checkout 独立建 CL，禁止自动 submit |
| [references/perf-analysis-playbook.md](references/perf-analysis-playbook.md) | triage 与 subagent 查阅 | 分析角度库；脚本 `pattern=` 标签直接指向对应章节 |

### 决断契约（这条流水线的失败/止步规则）

- **决断，不搪塞**：能从代码判定的一律给结论 + 具体改法，禁止"考虑缓存一下""需谨慎评估"把问题甩回用户。
- **止步只有三类，处置不同**（详见 SKILL.md §决断三类止步）：① 对外契约（网络协议/结算/序列化/跨端格式/服务端）→ 给改法 + 标"需 <谁> 确认"；② 内部硬约束（项目 CLAUDE.md 明令禁止的写法）→ 不提违规改法，找合规替代或判不可落地；③ 可行性硬阻塞（数据/时序在执行处不可得、依赖不可读 native 语义、需新增 C# 绑定）→ 判不可纯静态/纯 Lua 落地 / 需 runtime probe。
- **排 P 级前过收益真实性闸**（真机收益 / 成本窗口 / 复发 / 可行性·合规，详见 SKILL.md Step 4）：热点定位对了不代表值得改——编辑器伪影、无交互 loading 期的分帧、一次性 init 当每帧排 P0，都要降级。
- **数据不足要明说**：某源 `NO DATA`、采样太短、热点全在框架底层 → 如实说明并建议重采，不放"能查而没查"的偷懒项。
- **框架入口不背锅**：事件分发器 / 包装调用 / 调度组件 / Timer / UI 基类 Update / PlayerLoop 只作为链路证据；必须继续追具体 handler/组件/业务函数。项目里的 Lua 侧入口配在 `profiler_config.json` 的 `lua_framework_dispatchers`。
- **建议要可推进**：每条 P0/P1/P2 必须带样本证据片段、调用链、当前劣化程度、优化后预期收益与边界、风险和验证方式。
- **落地闭环的止步**：`REJECT` / `RESAMPLE` / `DEFER` 是合法终态；改法涉及行为轨迹变更、换公式的浮点漂移、prefab 手术、或被对抗验证互拆的候选，一律不混入批量落地（见 SKILL.md「不做闸门」）。

## 数据从哪来（两种导出 · 两种采样模式）

`AI Profiler` 面板（`Window/Analysis/AI Profiler`，源码见 [unity/](unity/README.md)）顶部可切**采样模式**：

- **Editor 本地**（默认）：先保持面板打开，再进 Play；StartRecord 会检查 Play 与 Lua 后端 Hook 就绪状态，不满足时阻止录制。默认开 Unity Deep + Lua 深度采样（若有后端）并关闭工程自带的冲突插桩（`AIProfilerCapture.DisableCompetingLuaProfiler` 接入点），且勾选**无上限录制**——把 Unity 帧分段流式落盘到 `<项目>/ProfilerLogs/raw/<时间戳>/seg_*.raw`，按约 256MB / Deep 16 帧 / 非 Deep 600 帧三重闸并显式 flush，导出时逐段累加，**突破 Unity 原生 ~2000 帧上限**（`CleanRecord` 清掉这些 `.raw` 段）。任意失败或空段都按 critical/灰灯处理。
- **真机连接(手机)**：设备跑含 `AI_PROFILER_DEVICE` 宏的 Development 包（要 Lua 再加后端宏），USB 插入电脑。需要 Lua 时在设备上触发 `AIProfilerDeviceControl.OpenLuaProfiler()`（接到 GM 菜单），完整退出并重启；该标记只消费一次。Hook 启动后休眠，`StartRecord` 才打开采样，`StopRecord` / TCP 断线 / 关窗会关采样并清空有界发送队列；Hook 就绪由独立 1Hz 状态包上报（需后端补丁，否则按连接即就绪）。易崩场景可在连接前关闭"同时采集 Lua"，进入原生安全模式，仅采 C#/GPU/内存/GC；此时 META 为 `mikuDeep=False`，Lua NO DATA 不判重采。设备帧按 32MB / 600 帧实时滚段与 pull。

导出由 `AIProfilerExporter.cs` 生成，多 section：`META`（含 `[Target]` 采样拓扑 + `[Health]` 插桩自身占比 + 各源是否捕获）、`FRAME_TIMELINE`（`TIMELINE` 顺序采样 + `TOP_CPU_FRAMES` 全程尖刺榜，脚本去重合并）、`CS_HOTSPOTS`、`LUA_HOTSPOTS`（含 Lua VM GC）、`GPU`、`MEMORY`（含 headAvg/tailAvg/trend 趋势三列）、`GC`，以及三个 **Editor 本地限定** section：`VIEW_STATS`（界面打开耗时 / 开屏 FPS 与卡顿 / 节点使用率）、`SCENE_SWITCH`（发起切换 → 用户可感"切完"的总耗时，>3000ms 标超标）、`LUA_MEM_TREND`（脚本 VM 总内存周期采样，判 Lua 侧泄漏）——这三个由运行时采集器 `AIProfilerCapture` 产出，工程需在 UI/场景流程里打点（有 Lua 的工程用 `unity/Lua/AIProfilerCapture.lua` 桥接）。两种导出都落地在 `Assets/ProfilerLogs/YYYY_MM_DD_HH_MM_SS.txt`。

旧「Lua Profiler」窗口的 `Export For AI`（纯 Lua 聚合树，无 Format 头）：脚本保留向后兼容解析以处理历史文件。

## 前置依赖

| 依赖 | 说明 |
|---|---|
| Unity「AI Profiler」面板 | `Window/Analysis/AI Profiler`，多源整合导出；支持 Editor 本地 / 真机连接两种采样模式。源码与合入方式见 [unity/README.md](unity/README.md) |
| Lua 后端（可选） | 有 Lua 的工程接 `ILuaProfilerBackend`；参考实现为 MikuLuaProfiler 适配（上游 <https://github.com/leinlin/Miku-LuaProfiler>，本仓库不附带其源码，见 [unity/Miku-LuaProfiler/README.md](unity/Miku-LuaProfiler/README.md)）。无 Lua 的工程 Lua 维度为 NO DATA |
| 真机采样（可选） | 设备需 Development 包（含 `AI_PROFILER_DEVICE`）+ USB/ADB |
| `AI Profiler Dump Suspect Frames` 菜单（可选） | 排查 `BeginSample` 泄漏污染时用；污染现场需先用 Unity Profiler 窗口 Record(Deep) 复现 |
| Python 3 | 运行预处理脚本，纯标准库 |

## 按项目要配的东西

SKILL.md 与 playbook 是从一个具体的 Unity + Lua 手游工程里长出来的，正文里的具体函数名、反例都是那个工程的实证（已做脱敏），方法论本身通用。换工程时要配/要知道的：

- **`scripts/profiler_config.json`**：Lua 源码根（`src_root`）、工程自带插桩的噪声特征（`noise_cs_substr` / `noise_lua_loc_substr` 等）、Lua 侧框架分发入口（`lua_framework_dispatchers`，脚本据此打 `role=framework-*` 标签、SKILL.md 的"归责保护"据此不把框架文件当根因）。样例见 `profiler_config.example.json`。
- **项目规则文档**：SKILL.md 多处提到"项目若有场景加载性能规则 / Lua 编码规范 / timer 顺序规则 / 知识路由，按其执行"。有就配到项目的 `.claude/rules/`，没有就按 SKILL.md 正文口径执行。
- **项目硬约束**：SKILL.md 的"内部硬约束"止步条件指的是项目 CLAUDE.md 明令禁止的写法（如禁用 `loadstring`、禁改生成的绑定目录），按项目实际填。
- **Unity 侧**：宏、打点、Lua 后端、冲突插桩关闭接入点见 [unity/README.md](unity/README.md)。

## 维护面：改哪里要同步哪里

- **导出格式变（`AIProfilerExporter.cs` 改 section / 列 / META 键）** → 同步 `scripts/analyze_profiler.py` 的解析逻辑（section 名、列顺序、`[Target]`/`[Health]`/`mikuDeep=`/`(Lua VM` 识别）+ SKILL.md「数据从哪来」+ 本 README 同名段。
- **采集行格式变（`AIProfilerCapture.cs` 的 `[ProfilerUtils][<Type>] ...` 契约）** → 同步脚本的 VIEW_STATS / SCENE_SWITCH / LUA_MEM_TREND 正则与 `--diff` 的界面/场景对比。
- **脚本新增 CLI flag / 配置键** → 同步「脚本用法」代码块、`profiler_config.example.json` 与 SKILL.md Step 1。
- **脚本新增 `pattern=` 模式标签** → 必须在 [playbook](references/perf-analysis-playbook.md) 有对应章节的「优先假设 + 定性手段」，否则 triage 拿到标签无处可查；同时补 SKILL.md Step 2 的模式清单。
- **规则库落点**（高频迭代，新经验往这里加，不要散落到本 README）：改法陷阱进 SKILL.md §改法准确率 12 类陷阱；批量落地的不做条件进「不做闸门」；落地时的语义/状态/P4 卫生要求进 §性能建议代码落地复核闸。每条新增都应带实证日期与反例（脱敏后再进仓库）。
- **Unity 侧脚本变** → 同步 [unity/README.md](unity/README.md) 的文件清单与接线点；面板私有方法改名时 `AIProfilerAutomation.cs` / `AIProfilerSkills.cs` 的反射调用会显式报错，一并修。

## 已知限制

- Lua 后端插桩会放大绝对耗时，报告一律以相对占比 / 量级对比为准，不报伪精确 ms；`calls` 数与 GC 字节相对可信
- C# 榜上的后端自身开销（`MikuLuaProfiler::*`、`EditorLoop` 等）是已知测量噪声，不立项分析；要干净的 C# CPU 或先排易崩场景，真机连接前关闭"同时采集 Lua"使用原生安全模式
- Editor 内 GPU 维度是计数器级，逐 marker 不可靠；真机模式下 GPU / GC 口径相对可信
- `VIEW_STATS` / `SCENE_SWITCH` / `LUA_MEM_TREND` 仅 Editor 本地模式产出，真机导出为 NO DATA；且依赖工程接入打点，未打点即 NO DATA
- `VIEW_STATS` 的点击响应只配对"点击→开界面"，纯逻辑按钮的无响应测不到——无 slow 记录 ≠ 点击全流畅
- META `deepLuaNative=True` 说明工程自带的冲突 Lua 插桩漏关，应关掉后重采
- META 出现"采样流污染"时，失败段成因是录制期 `Begin/End` 配对断裂，重采前须先消灭污染（减小段体积/加内存无效）
- Unity 侧代码经 Roslyn 对 Unity 2022.3 引用程序集编译验证（含/不含 Lua 后端、Editor/Player 四种配置），未做 Unity 内运行验证；面板与导出器逻辑从源工程原样迁出，只替换了 Lua 桥与后端耦合
