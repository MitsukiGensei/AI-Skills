---
name: profiler-analysis
description: 分析 Unity 性能采样导出数据，从耗时(CPU)、GPU、内存、GC、调用次数多维度定位热点，结合工程源码给出文件:行级别的具体优化建议和分析报告。覆盖两种导出：①「AI Profiler」面板（Window/Analysis/AI Profiler）导出的多源整合数据（C# CPU / Lua CPU / GPU / 内存 / GC / 帧时间线，格式 AI-Profiler-v1）；②旧「Lua Profiler」窗口 Export For AI 的纯 Lua 采样。两者都落地在 Assets/ProfilerLogs/YYYY_MM_DD_HH_MM_SS.txt。当用户提到分析性能采样、分析 profiler、AI Profiler、profiler 日志分析、ProfilerLogs、分析 Lua 性能、分析 C# 性能、分析耗时、分析 GC、内存分析、GPU 热点、找性能热点、看哪个函数慢、哪里 GC 高、帧率尖刺、性能优化建议、profiler 报告、analyze profiler 时触发。未指定文件时默认分析 ProfilerLogs 中时间最新的导出（脚本自动识别格式）。即使用户只说「看看最新那次采样有什么问题」「这次 profiler 跑出来哪里慢」，只要对象是 Profiler 导出数据，都应触发此 skill。
---

# profiler-analysis — Profiler 采样数据分析

分析 Unity 性能采样导出，定位 **CPU(C#/Lua) / GPU / 内存 / GC / 调用次数** 热点，
阅读对应工程源码，产出结构化报告与**具体到文件:行**的优化建议。脚本自动识别两种格式。

## 数据从哪来（两种导出）

### A. AI Profiler 面板（推荐，多源整合 · `AI-Profiler-v1`）
Unity 菜单 `Window/Analysis/AI Profiler`。面板顶部有 **采样模式** 切换：**Editor 本地** / **真机连接(手机)**。

- **Editor 本地**（默认）：打开面板即自动开启 Unity Deep Profile + Lua 后端深度采样（本文以 MikuLuaProfiler 为参考后端，见 `unity/` 的 `LuaProfilerBackend`；无 Lua 的工程 Lua 维度为 NO DATA），并**主动关闭工程自带的冲突 Lua 插桩**（`AIProfilerCapture.DisableCompetingLuaProfiler` 接入点）——Lua 数据全部来自单一后端（含 Unity 拿不到的 Lua VM GC），避免两套 hook 对同一调用重复插桩。流程：**先保持面板打开** → 进 Play 并确认绿色 `OnStartGame` → `StartRecord` → 操作几秒 → `StopRecord` → `ExportForAI`；工具会在 StartRecord 前检查 Play、Hook 与 Lua VM 就绪状态，不满足时直接阻止录制。**面板默认勾「无上限录制」**：录制期把 Unity 帧分段流式落盘到 `<项目>/ProfilerLogs/raw/<时间戳>/seg_*.raw`，导出时逐段加载累加，**CPU/GC/时间线总帧数不再受 Unity 原生 ~2000 帧上限**（`CleanRecord` 会清掉这些 `.raw` 段；取消勾选则回退有上限的 live 录制）。
- **真机连接(手机)**：设备跑 **Development 包（含 `USE_LUA_PROFILER` 宏，见 `BuildPackage.cs`）**，USB 插入电脑并授权 ADB。需要 Lua 时，在设备上触发 `AIProfilerDeviceControl.OpenLuaProfiler()`（接到 GM 菜单）写入**一次性**启动标记，完整退出并重启游戏，让 Lua 后端在 Lua VM/业务脚本加载前安装 Hook；再由面板点 **ADB 一键连接**。Hook 启动后保持休眠，只有 `StartRecord` 的 ADB 命令才打开函数采样，`StopRecord` / TCP 断线 / 窗口关闭会关采样、恢复 Lua GC 并清队列；独立 1Hz 状态包只上报 `mainL` 与采样开关，不再用空 Sample 冒充心跳。若目标场景易崩，可在连接前关闭“同时采集 Miku Lua”，进入**原生安全模式**：仅建立 Unity Profiler 通道并采 C#/GPU/内存/GC，Lua 空数据是预期而非重采错误。真机帧仍由设备按 32MB / 600 帧滚段并实时 pull；导出 META 带 `[Target] Capture Mode: device`、ADB serial 与真实 `hookReady`。

> 🎞️ **无上限录制实现要点**：① Unity 帧遍历 API 主线程独占，且 `ProfilerDriver.LoadProfile` 单次最多回放 ~2000 帧（Unity 硬限制）——Editor 本地走"分段 binary log（约 256MB / Deep 16 帧 / 非 Deep 600 帧三重闸）+ 导出时逐段 `LoadProfile` 累加"；Deep 的 16 帧硬闸不依赖缓冲中的磁盘文件长度，防止极端样本形成 1GB+ 段。② 内存/渲染计数器走 `ProfilerRecorder`（容量提到 6 万，覆盖长录制）；GPU 走 `FrameTimingManager`，均不受 2000 帧约束。③ Lua（Miku 通道）改为**到达即聚合**；设备未录制或 TCP 未连接时立即回收 Sample，发送队列硬限 128 包，避免启动期/断线期无界积压。④ 真机由 Editor 经 ADB 命令文件自动启停设备 `DeviceFrameRecorder`，设备按 32MB / 600 帧双重闸滚小段；段关闭后发布原子 `.ready`，Editor 后台单队列 `adb pull` 到 `.part`，长度校验通过后转 `.raw` 并删除设备副本，Stop 只收尾最后段。⑤ 导出侧帧解析（2026-07 起）用 `RawFrameDataView` **线性扫描主线程原始样本流** + markerId→聚合项缓存，self 耗时在出栈时按「自身 inclusive − 直接子级 inclusive」结算——不再逐帧构建 `GetHierarchyFrameDataView` 合并层级视图（Deep 百万级样本帧下原生构树单帧可达几十秒，是历史上"解析帧 N/M"卡半天的根因）；进度条每 16 帧刷一次。导出仍是主线程同步（期间 Editor 无响应属正常），可随时取消，已解析帧照常导出。

导出由 `AIProfilerExporter.cs`（见 `unity/`）生成，**多 section**：
- `META`：导出时间、帧区间、各数据源是否捕获到数据（含 `deepLuaNative` 是否为 True——正常应 False，True 说明工程自带的冲突 Lua 插桩漏关了）、**`[Target]` 采样拓扑**（editor 本地 / device 真机连接 + 连接目标 + ADB serial）、单位与采样说明、**`[Health]` 插桩自身占比**（C# self / Lua self）+ 重采建议。
- `FRAME_TIMELINE`：`frame | cpuMs | gcAllocB`（找尖刺帧）。含两个子块：`## TIMELINE ##`（前 N 帧顺序采样，N=`MAX_TIMELINE_ROWS`）+ `## TOP_CPU_FRAMES ##`（全程按 cpuMs 降序的尖刺榜，覆盖长录制时被 TIMELINE 顺序截断的后段尖刺）。脚本对两块去重后合并出 cpuMs 尖刺榜。
- `CS_HOTSPOTS`：`rank | selfMs | totalMs | calls | gcAllocB(incl) | marker`（C#/引擎，来自 Unity 原生 Profiler）。
- `LUA_HOTSPOTS`：`rank | selfMs | totalMs | calls | luaGcB | monoGcB | location | name`（来自 MikuLuaProfiler，含 Unity 拿不到的 **Lua VM GC**）。
- `GPU`：渲染计数器（Draw Calls / SetPass / Batches / 三角面 / 顶点）+ GPU 帧耗时（Editor 本地 best-effort；真机模式为设备 `GPU Frame Time(ms)` / `CPU Total Frame Time(ms)` 计数器，相对可信）。
- `MEMORY`：内存计数器 min/avg/max/last + **headAvg/tailAvg/trend 三列**（录制前/后各 300 样本窗口均值与变化率——判上升趋势/泄漏信号；样本不足为 `-`）。
- `GC`：大 GC 帧 Top + Mono GC.Alloc 调用路径 Top + Lua VM GC Top。
- `VIEW_STATS`：**界面打开性能统计**（Editor 本地模式限定；录制期由面板开启运行时采集器 `AIProfilerCapture`，工程在 UI 流程里打点；有 Lua 的工程经 `unity/Lua/AIProfilerCapture.lua` 桥接）。逐条日志 `time|frame|flag|message`（`frame`=运行时 Time.frameCount，与 FRAME_TIMELINE 帧号**非同一体系仅近似对齐**，基准见 section 头注释；`flag` `!`=超标）：**ViewOpen** 资源加载/显示完成/点击响应耗时(ms)、**ViewFPS** 界面打开后统计窗口内 FPS/SmallJank/Jank/BigJank/Stutter/Freeze/Drop（PerfDog 前三帧口径，附逐帧 fps/time 续行）、**ViewNode** 节点总数/未使用数/未使用率。超标行正文自带阈值提示（如「超过阈值: 400ms」「slow」）。分析时把超标界面与 CS/LUA 热点、尖刺帧交叉归因（界面打开慢 → 查该 View 的 OnOpen/资源加载链路）；注意采集自身开销（ViewNode 全树扫描）计入热点属测量噪声。**边界**：点击响应只配对「点击→开界面」，纯逻辑按钮（点了不开界面）的无响应测不到——无 slow 记录 ≠ 点击全流畅。真机模式此 section 为 NO DATA。
- `SCENE_SWITCH`：**场景切换耗时**（Editor 本地模式限定，与 VIEW_STATS 同通道采集）。`SwitchScene` 调用 → `SwitchToSceneOver`（loading 已关、场景生命周期走完）的用户可感总耗时，行格式同 VIEW_STATS，`!`=超 3000ms。超标切换按「六段分解」定位（前摇/Unity 场景加载/最小 loading 时长白等/业务资源/业务初始化/揭幕）——先算结构性等待占比（白等、固定 Delay、被串行推迟的加载）再优化真实加载；项目若有场景加载性能规则文档，按其诊断 SOP。
- `LUA_MEM_TREND`：**脚本 VM（Lua）总内存周期采样**（Editor 本地模式限定；`AIProfilerCapture.ScriptMemoryMBProvider` 或 Lua 适配器的 `collectgarbage("count")` 每 5s 一发 + 起止各一发，`time|frame|luaVmMB`）。持续上升是 Lua 侧泄漏/累积信号，与 MEMORY 的 Mono/Native trend 列互补（Lua VM 存量 Unity 计数器拿不到）；结论前先排除录制期业务本身该涨（进新场景/加载新模块）。

### B. 旧 Lua Profiler 窗口（仅 Lua · 无 Format 头）
`Window/Analysis/Lua Profiler` 工具栏的 `Export For AI`，只含 Lua 聚合树（`SECTION 1` 热点表 + `SECTION 2` 调用树）。脚本会自动按旧格式解析（向后兼容）。

> 落地路径都是 `Assets/ProfilerLogs/YYYY_MM_DD_HH_MM_SS.txt`。

## 适用与不适用

**适用**：定位哪个 C#/Lua 函数耗时高、哪里 GC 分配多（Mono + Lua VM）、哪个函数高频调用（**高频调用画像**：calls/帧归一，抓「单次便宜但每帧狂刷」的隐形热点）、内存趋势异常（**MEMORY trend 列 + LUA_MEM_TREND** 判泄漏斜率）、渲染计数器（DrawCall 等）偏高、哪几帧是尖刺、**哪个界面打开慢 / 点击无响应 / 开屏卡顿 / 节点浪费（VIEW_STATS，报告独立成章）**、**哪次场景切换慢（SCENE_SWITCH）**、**优化前后对比 / 版本回归（`--diff` 基线对比）**；**瓶颈类型与功耗发热的独立代理判定**（脚本「独立瓶颈/功耗代理画像」段：GpuFrameTime vs CpuFrameTime、等待类 marker、喘息占比、常驻浪费模式、三角形预算——全部从本采样数据得出，见 playbook §〇/§五）。

**不适用**（如实说明边界 + 给旁证假设，不写「无法分析」，也不把「去用某外部工具」当分析结论）：
- 渲染管线逐 Pass / Shader 级 GPU 瓶颈：本数据 GPU 维度是计数器级——报告给结构性假设（合批漏点/预算超标）与计数器依据，标注逐 Pass 细节属深挖方向。
- C# native/纹理/网格对象级内存泄漏归属：本数据只能判趋势与分配源——报告给趋势判断与高危对象假设（playbook §一）。
- 硬件级功耗定量（mW/频率/温度）：用采样数据做**代理判定**（playbook §五），不依赖 PerfDog/Perfetto 等外部采集。
- **Render Thread / Job 线程热点**：CS_HOTSPOTS 只遍历 Main Thread 原始样本流，渲染线程与 Job 的 CPU 开销不在榜上——「C# 榜干净」≠「CPU 没问题」。渲染侧压力用 GPU section 的 CPU/GPU Frame Time 与瓶颈画像兜（`Gfx.WaitForPresent` 高即渲染/GPU 侧压力信号），逐线程细节属 Unity Profiler Timeline 深挖方向。
- 单纯包体大小 → `/apk-analysis`；真机崩溃/卡死根因 → `/android-device-debug`。

> 📗 **分析角度库**：[references/perf-analysis-playbook.md](references/perf-analysis-playbook.md)（《7月性能分析》wiki 树方法论，已转译为 **AI Profiler 独立口径**——一切判断从自身采样数据得出）——五模块分诊框架、内存/启动加载/场景切换/渲染 CPU/UGUI/动画/物理/GPU/功耗代理各域的「信号 → 优先假设 → 定性手段」。triage 与 subagent 深挖时按需查阅对应章节；脚本 C# 榜命中特征 marker 时会打印 `pattern=` 提示并指向章节，「独立瓶颈/功耗代理画像」段自动输出启发式判断。

> ⚠️ **采样误差**：AI Profiler 开 Unity Deep + Lua 后端 hook（工程自带的冲突插桩已被面板关闭），后端插桩仍会**放大绝对耗时**，分析一律看**相对占比 / 量级对比 / 尖刺帧**，不要把 ms 当线上真实值。GC 字节数、调用次数、渲染计数器相对可信。Editor 内 GPU 逐 marker 不可靠（GPU section 以计数器为主）。
>
> 🤳 **真机模式（device）特殊口径**：连接设备采样时，Miku 仍放大 Lua 绝对耗时（同上看相对占比）；但 **GC 不含编辑器工件**（`FileUtil.GetPhysicalPath` / `UnityEditor.*` 真机本无，出现的都是真机真实分配），**GPU/渲染计数器与帧耗时相对可信**。C# 为设备 marker 层级，完整 deep C# 取决于打包是否开 deep profiling。脚本读到 `[Target] device` 会自动打印真机口径提示横幅。

> 🧹 **插桩噪声**：榜首常被"测量工具自身"占据——主要是 Miku（`MikuLuaProfiler`、`reimport`）+ `EditorLoop` + "仅编辑器存在"的 Mono GC（`UnityEditor.*` / `FileUtil.GetPhysicalPath` 等，真机没有）。**若还看到工程自带插桩的条目（META `deepLuaNative=True`），说明冲突插桩漏关了**（面板应已关，检查 `DisableCompetingLuaProfiler` 接入）。**分析脚本已默认过滤通用噪声（工程自带插桩特征配在 `scripts/profiler_config.json`）并给出信噪比体检**——直接看【过滤后】视图，`--raw` 看全貌。注意 C# 过滤只能去掉名字含工具特征的 marker，Miku 内部的 `Stack/Dictionary` churn 仍可能混在 C# 榜里，结合 `[Health]` 占比判断。
>
> 🚫 **具名误归因黑名单（示例：事件框架的逐帧 beat 分发器 `_event.__call`）**——这类分发器函数体本身零分配（迭代器零分配，handler 经包装调用单独计量），其名下的大额 self/luaGc 是后端跨界/相邻归因误差。**不要据此立项"排查某模块高频事件源"**——业务事件根本不经过它；排查方向看 beat 内各 handler 自己的条目（调度组件 / Timer 等）。项目里的这类入口配在 `scripts/profiler_config.json` 的 `lua_framework_dispatchers`，脚本会打 `role=framework-*` 标签。（实证：曾有 49.49MB Lua GC 被误归因到该分发器）

### 采更干净的数据（信噪比过高时）

面板默认 = Unity Deep + Lua 后端（工程自带的冲突插桩已关）。体检占比仍偏高时：
- **Lua 耗时 / GC** → 用默认采样即可（Lua VM GC 唯 Miku 可得），看相对占比 / 尖刺，勿把 ms 当真机值。
- **干净的 C# / 引擎 CPU 或易崩真机场景** → 真机连接前关闭“同时采集 Miku Lua”，用原生安全模式采 C#/GPU/内存/GC；该模式 META 中 `mikuDeep=False`，Lua 为空是预期。
- **看到工程自带插桩的条目 / META `deepLuaNative=True`** → 冲突插桩漏关了，关掉后重采。
- 通用：缩短采样窗口、只覆盖目标操作；必要时用后端的白名单机制只插桩目标模块。
- 内存/泄漏归属、真机数值另用 Memory Profiler / 真机复测（见"不适用"）。
- **Lua 榜 0 函数（Miku 0 unique funcs）**：先看 META；`mikuDeep=False` 表示用户主动用了原生安全模式，不判数据损坏。`mikuDeep=True` 时才检查 Play、独立 Hook 心跳与采样窗口；真机 Hook 只在 `StartRecord` 到 `StopRecord` 之间产 Sample，若该区间仍为空再标灰重采。
- **原生分段有任意 LoadProfile 失败或 walked 0 → 原生 C#/GC/帧尖刺维度残缺**：缺失段的时序不可恢复，不允许按失败比例猜测影响；分析脚本统一标 critical，标题用灰灯并重采。新工具用约 256MB / Deep 16 帧 / 非 Deep 600 帧三重闸并显式 flush；导出后仍需核 META `failed=0`、`empty=0`、`walked > 0` 再交付分析。META 现已透传逐段失败的底层原因（如 `Deserializer encountered error`）——按原因分流重采动作，勿一律当"内存不足"。
- **META 出现「⚠ 采样流污染」→ 失败段成因是录制期 Begin/End 配对断裂**：污染窗口内落盘的段反序列化必失败——**成簇失败 + 失败与段体积无关**是其签名（小段失败、更大的段反而成功即可排除内存不足）。重采动作是先消灭污染（Console 只要还在刷 `Missing Profiler.EndSample`，段就还会写坏），不是减小段体积/加内存。
- **归因陷阱（2026-07-13 实证）：告警 Previous samples 的尾部 ≈ 帧内最后执行的 Update（BehaviourUpdate 收尾校验点），不是泄漏源**。当日两台机器的告警尾部都是某绘线插件的常驻 Manager，曾被误定为根因；逐帧聚类后发现其轻/重两种分支完美交替（41/39）、轻分支帧只跑 3 个 getter 也照样告警——它只是常驻且恰好最后执行的旁观者。
- **泄漏通道排查台账（2026-07-13~14，最终定案）**：① 绘线插件常驻 Manager——帧末校验点旁观者，证伪；② 工程自带的 Lua 侧 BeginSample/EndSample 插桩——加配平守卫后静默证伪，守卫保留为长效防线；③ Lua 后端——不进原生流，排除；④ lua error/经绑定层转换的 C# 异常——双仪器归零证伪（Lua 适配器的 pcall 守卫 + 后端的 `lua_error` hook 标记在全部导出零出现）；⑤ **真凶（已修）：列表插件 `LoopGridView.GetNewItemByRowColumn` 两条 early-return 在 `Profiler.BeginSample` 后未配 `EndSample`**——某界面网格每帧命中越界分支泄漏一个 Begin，`LoopGridView.Update` 方法采样永不闭合（后续兄弟 Update 被嵌成其子样本），BehaviourUpdate 收尾每帧告警、时段内 .raw 段全损。
- **定案方法（下次直接用，10 分钟内点名）**：泄漏的 Begin 会被 Unity 在校验点强制闭合并照常记录——**live 帧的树形结构里它表现为"吞掉了后续兄弟系统的异常长样本"**。菜单 `Window/Analysis/AI Profiler Dump Suspect Frames`（污染现场用 Unity Profiler 窗口 Record[Deep] 复现后执行，勿用无上限 binlog——live 环无数据）输出 `Assets/ProfilerLogs/suspect_frames_dump.txt`，找"兄弟 Update 被嵌套"的父节点即泄漏方法，再查该方法内的手动 BeginSample 早退路径。教训：**静态 Begin/End 计数配平 ≠ 无泄漏（控制流早退是盲区），`AppDomain.FirstChanceException` 在 Unity Mono 不派发（勿用）**。

### 工程自带脚本插桩的出包剔除（通用原则）

很多工程在 Lua 侧有自己的 `BeginSample/EndSample` 手动插桩、调试日志等**只存在于 Editor 与 profiler 包**的代码，正式出包由构建管线按标记（如行尾 `--only debug` tag、特定调用前缀）逐行剔除。分析时这部分开销**不立项、不派 subagent**（真机正式包不存在），修复也无需手动删除，只需新写法可被剔除。

**新加插桩的写法约束**（按行剔除的管线普遍适用，违反会导致剔除后语法/语义损坏）：

- 插桩调用写在单行内，不拆多行（按行删除会留悬空括号）
- 不要 `return end_sample(...)` 传值（行被删后丢返回值）
- 表达式位置引用插桩对象必须带剔除 tag，否则只匹配调用形式的规则删不到、行残留半句
- 成对结构（`if ... then` / `end`）要么全 tag 要么全不 tag
- tag 必须逐字（多一个空格就漏删）

**验证方法**：复刻管线的剔除规则跑全量 Lua 源码 → 用 Lua 编译器 `-p` 逐文件语法检查，与原始基线对比只看"剔除后新增"的语法错误。把工程自带插桩的特征配进 `scripts/profiler_config.json`（`noise_cs_substr` / `noise_lua_loc_substr`），脚本会把它们归入"测量工具自身"噪声。

## 工作流

> **总览**：Step 1 预处理 → Step 2 主 Agent 梳理问题清单（triage，只轻量定位） → Step 3 **每个问题派一个只读 subagent 深挖根因 + 给出具体修复** → Step 3.5 **对改行为/依赖收益假设的 P0/P1 候选派独立 skeptic 对抗验证（refute-first）** → Step 4 主 Agent 汇总报告。
>
> 两条贯穿始终的硬规则：
> - **C# 的后端插桩直接忽略**：`MikuLuaProfiler::*` / 工程自带插桩桥 / `EditorLoop` / 后端的 `Stack·Dictionary·StringBuilder` churn / 绑定层 update 都是测量工具开销，**已知问题，不立项、不派 subagent、报告里一句带过即可**。要干净的 C# 业务 CPU 就另采一次 Miku-off（见"采更干净的数据"）。聚焦**真正的代码问题**：Lua 耗时 / Lua VM GC / 业务 Mono GC 分配源 / 帧尖刺关联事件。
> - **决断，不要搪塞**：能从代码判定的就给结论和具体改法，禁止用"需谨慎""考虑缓存一下""建议评估"把问题甩回用户。合法的"止步/标注"只有三类，**各自处置不同**（其余一律给结论）：
>   1. **对外契约**（网络协议 / 结算 / 序列化格式 / 跨端数据格式 / 服务端约定）→ 仍先给推荐改法，只额外标注哪一处需谁拍板。
>   2. **内部硬约束**（项目 CLAUDE.md 明令禁止的写法，如禁用 `loadstring`、禁止新增某类全局、禁改生成的绑定/配置目录）→ 撞了就**不提这条改法**：先确认是否真有合规路径（有原生编译/缓存 API 则用之），无合规路径则判"本项不可合规落地"。**绝不要"给个违规改法再标需拍板"——那是在提议违规**。(2026-06-29：某条件表达式模块的编译缓存唯一改法是 `loadstring`，撞禁令、无合规替代 → 应判不落地，而非提议+标拍板。)
>   3. **实现可行性硬阻塞**（修复所需数据/时序在执行处不可得、依赖不可读 native 语义、需新增 C# 绑定）→ 判"不可纯静态/纯 Lua 落地"或"需 runtime probe 验证后再定"，别硬给一个时序上跑不通的方案。

### Step 0 — Memory 预检（必须做）

分析前先查当前环境可用的 memory，避免重复犯已知误判。最少检索这些关键词：`profiler` / `performance` / `性能` / `采样` / 目标模块名 / 热点入口名（例如 `event.lua`、`ScheduleComponent`、`BaseComponent`）。优先读 `MEMORY.md` 命中的 profiler 相关条目；若 memory 没有 profiling 相关内容，也要在报告"采样说明/存疑"里写明"本次 memory 未命中性能分析经验"，不要假装已参考。

### Step 0.5 — 数据完整性闸（先判可分析维度）

先看脚本输出的【数据完整性诊断】与 META：

- `NATIVE_SEGMENT_LOAD_FAILED` / `NATIVE_DATA_MISSING` 为 critical 时，**原生 C#/Mono GC/帧尖刺维度不可下结论**；标题用灰灯/需重采，只能继续分析仍有数据的 Lua/Miku 维度。
- `LUA_DATA_MISSING` 为 critical 时，**Lua CPU/Lua VM GC 维度不可下结论**；但真机 META 明确 `mikuDeep=False` 是主动原生安全模式，不应把预期缺失升级成重采错误。其余常见原因是先进 Play 后开面板、Miku hook 未安装、真机远程未连接。
- 任意分段失败或 walked 0 都意味着对应时序不可见，不要把"没有 C# 热点"写成"没有 C# 问题"；统一标 critical/灰灯并重采。
- 数据不完整时仍可产出局部结论，但每条结论必须标明它依赖的可用数据源，缺失维度放入"需重采"而不是臆测。

### Step 0.6 — 归责保护（框架入口不是根因）

脚本会把框架分发/包装入口打 `role=framework-*` 标签。命中这些入口时，**只把它们当调用链证据，不直接立项到框架文件**：

- 事件框架的帧 beat 分发器（源工程为 `_event.__call`）不归责到事件框架文件，继续追具体 beat handler。
- 事件框架的包装调用（源工程为 `_xpcall.__call`）不归责到事件框架文件，继续追被包装函数。
- 调度组件的 `_performUpdate`、`Timer:OnTimer` 是定时/调度入口，不归责到 Scheduler，继续追注册的任务和业务函数。
- UI 基类组件的 `_performUpdate` / `OnUpdate`、C# `PlayerLoop` / `ScriptRunBehaviourUpdate` / `UpdateBeat` 是全局/组件 Update 入口，不归责到入口本身，继续追具体组件、view、marker。
- Lua 侧入口按项目配在 `scripts/profiler_config.json` 的 `lua_framework_dispatchers`；C# 入口内置。

报告里若出现这些入口，必须写出"入口 → 下游 handler/组件 → 具体业务函数/代码行"的链条；没有追到下游时只能列为"需补充调用链证据"，不能给 P0/P1。

### Step 1 — 定位并预处理数据

```bash
# 默认分析最新文件；脚本自动识别 AI-Profiler-v1 / 旧 Lua-only 两种格式
# 项目特征（Lua 源码根、工程自带插桩噪声、框架分发入口）配在脚本旁的 profiler_config.json（样例 profiler_config.example.json）
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --top 25

# 指定文件 / 列出可分析文件 / JSON
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --file "Assets/ProfilerLogs/2026_05_25_15_00_00.txt"
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --list
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --json --top 30

# 关闭插桩过滤，看未过滤全貌（默认会过滤 Miku/Profiler/EditorLoop/编辑器工件等噪声）
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --raw --top 25

# 基线对比（优化落地验证 / 版本回归）：当前文件（--file 或最新）vs 基线文件，热点按帧归一后出回归/改善榜
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --file "<优化后导出>" --diff "<优化前导出>"
```

> Windows 上 `py -3 <脚本>` 或 `python <脚本>` 均可，仅依赖标准库。

脚本对 AI-Profiler-v1 输出：META 各源状态 + **信噪比体检** + 帧尖刺(cpu/gc) + C# 热点 + Lua 热点 + Lua VM GC + **高频调用画像**（calls/帧 ≥1 的条目按频率降序——「单次便宜但每帧狂刷」在耗时榜上隐形，修法方向是**降频**而非降单次成本）+ GPU/内存计数器（**含 trend 趋势警示**）+ **界面打开统计 / 场景切换耗时 / Lua VM 内存趋势** + GC 归因（**真实分配 / 仅编辑器工件 分组**）+ 「待阅读 Lua 源文件」（已解析到 Lua 源码根下的真实路径，**已剔除插桩自身**）。

**`--diff` 基线对比**：P0/P1 落地后重采一次，与优化前导出对比验证收益是否真实到账（改善榜里找对应条目——已消失/显著下降即生效）；也可用于两个版本间的性能回归检测。结论口径：回归榜非空 ≠ 一定劣化，先核对两次采样的操作路径是否一致。

**默认即【过滤后】视图**——C# / Lua / GC 各榜已隐去测量工具自身与仅编辑器存在的条目，每榜末尾标注隐去条数。**两件事先看**：① META 各源状态，某源标 `NO DATA` 则说明并取舍；② 信噪比体检，占比过高时优先按"采更干净的数据"重采，再深入归因。`--json` 输出含 `health` 块与每条热点的 `noise` 标签。

### Step 2 — 主 Agent 梳理问题清单（triage，只轻量定位，不深读）

主 Agent 把脚本【过滤后】榜单**归并成 N 个相互独立的问题**，每个问题记录三样：

- **现象 / 指标**：脚本给的 `selfMs / calls / luaGc / monoGc` + 关联的帧尖刺。
- **入口位置**：`文件:行`（脚本"待阅读源文件"已解析；未解析的用 Grep/Glob 定位）。
- **调用栈 / 上下文线索**：从 `total vs self`、caller、模块归属推断的调用路径（供 subagent 起步）。
- **样本证据片段**：复制热点榜/GC 榜/帧尖刺中的原始行（含 `role` / `pattern` 标签、frame、calls、GC 字节），供报告回填。
- **劣化程度初判**：按 `calls / walkedFrames`、GC 总量、是否每帧/每次打开/一次性 init 标出频率，不报伪精确线上 ms。
- **已知模式优先套用**：脚本给 C# 热点打了 `pattern=` 标签（shader-compile / passive-wait / idle-wait / gpu-bound-wait / ugui-* / skinning-pressure / physics-idle-sim / teardown-storm / load-burst / animator-cull）——命中即按 [playbook](references/perf-analysis-playbook.md) 对应章节的「优先假设 + 定性手段」立项，不要绕开成熟路径自创归因。特别注意：`passive-wait`（Semaphore.WaitForSignal）不单独立项；场景切换类尖刺先按「切换三件套」（shader 现场编译 / 集中加载 / 销毁风暴）归并。
- **先看画像段**：脚本「独立瓶颈 / 功耗代理画像」已给出 bound 类型、喘息占比、常驻浪费、三角形预算的启发式判断——triage 用它定方向（哪个模块该立项、功耗风险如何），再落到热点/源码证据。

立项原则：

- **一个问题 = 一个独立根因**。同根因的多个 marker 合并成一个问题（如 `__index:203` 与 `:212` 同属"继承查找"；map chunk 相关多函数同属"chunk 流式"）。
- **只立真实代码问题**：Lua 耗时 / Lua VM GC / 业务 Mono GC 分配源 / 帧尖刺关联事件 / 界面开启超标。
- **界面开启超标独立立项**：脚本「界面打开统计」块（VIEW_STATS）的超标条目（flag=`!`）**按界面归并**立项——现象取该界面的 ViewOpen/ViewFPS/ViewNode 超标行原文，入口定位到对应 View 的脚本文件（按项目的 UI 目录 / 界面定义查 prefab 路径），并用 frame 列与 LUA_HOTSPOTS、帧尖刺交叉印证。无超标不立项（报告界面开启章一句"全部达标"带过）；该 section NO DATA 时该章标灰，不臆造。
- **场景切换超标独立立项**：脚本「场景切换耗时」块（SCENE_SWITCH）的超标条目（>3000ms）按切换路线立项，先按六段分解定方向（结构性等待占比高先修结构，低才优化真实加载），并与 `pattern=` 切换三件套（shader-compile/load-burst/teardown-storm）交叉。
- **高频调用维度独立扫一遍**：脚本「高频调用画像」块（calls/帧 ≥1）里 selfMs 榜没出现过的条目单独审——高频+可观 GC/耗时的立「降频」项（dirty 化/缓存/事件化），与耗时榜条目同根因的合并。calls 数不受插桩放大影响、相对可信。
- **内存趋势信号立项**：MEMORY trend 列 +10% 以上、或 LUA_MEM_TREND Δ≥20MB 且 ≥15% 时立「泄漏/累积排查」项——先排除录制期业务本身该涨（进新场景/加载新模块），Lua 侧结合 TOP_LUA_VM_GC 分配源，Mono/Native 侧标注需 Memory Profiler 复核。
- **不立项**：C# 后端插桩、`EditorLoop`、后端的 `Stack/Dictionary` churn、仅编辑器工件（`UnityEditor.*`）——已知是测量/编辑器开销，跳过（见总览硬规则）。
- **不把框架分发器当根因**：带 `role=framework-*` 的条目只能作为链路起点，必须追到具体 handler/组件/业务函数后才可立项。
- 主 Agent 此步**只轻量定位**（扫一眼入口确认归属与是否同根因），**深读与定性交给 subagent**，不要自己把所有源码读完。

### Step 3 — 每问题派一个只读 subagent 深度分析（并行）

对 Step 2 的每个问题，用 **Agent 工具**起一个 **`Plan` 类型 subagent**（只读：无 Edit/Write，天然不改代码），**在一条消息里并发派发所有 subagent**（彼此独立，无依赖）。每个 subagent 的 prompt 用下面模板：

```
[性能问题] <一句话现象>
[指标]    self=<>ms calls=<> luaGc=<> monoGc=<>；关联帧尖刺 frame <n>
[入口]    <文件:行>
[调用栈线索] <total/self 关系、caller、所属模块>
[背景]    这是 Unity AI Profiler 采样（Lua 后端单 hook，工程自带的冲突插桩已关）。后端/编辑器插桩噪声已过滤，
          不要再分析插桩本身。绝对 ms 受 Miku 放大，按相对量级/调用次数/GC 字节判断。
          若入口带 role=framework-*，它只是分发/包装入口，必须继续追下游 handler/组件，不能归责到入口文件。
[任务]    深度阅读相关源码与上下文（项目若有知识路由/模块文档规则，按其路由读模块文档，
          读取最小化），独立定位根因，给出具体到 文件:行 的修复方案。
[要求]
 - 根因落到读过的具体代码行，说清"为什么慢 / 为什么 GC"（循环规模、每帧调用、每帧建表/闭包、
   跨 C# 边界、字符串拼接、同步加载、重复计算…）。
 - 输出完整调用链：采样入口/marker → 框架分发器（如有）→ handler/组件 → 具体业务函数/代码行。
 - 引用样本证据：保留热点榜/GC 榜/帧尖刺中的原始指标片段（self/total/calls/GC/frame/role），用于报告复核。
 - 量化劣化程度：说明频率（每帧/每次打开/一次性）、调用规模、GC 量级、是否覆盖关键交互窗口。
 - 修复方案具体到可直接落地：改哪个文件哪几行、改成什么、为什么这样改能降耗时/GC。
 - 给优化后提升预期（"消除每帧建表""调用从每帧 N 次降到 dirty 时触发""N 万次查找降为 O(1)"），说明收益边界和可能回归风险，不报伪精确 ms。
 - 决断，不要搪塞：能从代码判定就给结论，禁止"考虑缓存一下""需谨慎评估"。只有三类可止步且处置不同：
   ① 触及对外契约（网络协议/结算/序列化/跨端格式/服务端）→ 仍给推荐改法 + 标"需 <谁> 确认契约"；
   ② 撞内部硬约束（项目 CLAUDE.md 明令禁止的写法，如 loadstring / 改生成的绑定目录）→ 不提该违规改法，找合规替代或判不可合规落地；
   ③ 实现可行性硬阻塞（修复所需数据/时序在执行处不可得、依赖不可读 native 语义、需新增 C# 绑定）→ 判不可纯静态/纯 Lua 落地或需 runtime probe 后再定。
 - **改法准确率自检**（详见 §改法准确率，逐条过相关项）：缓存 getter 先验无副作用、断言跨边界 GC 先排除 Lua shim、
   延迟同步操作先查调用方是否同帧读其结果、立项前 re-read HEAD 确认未已优化、事件失效枚举全部触发路径、
   需新增 C# 重载先标"需重生成 ToLua 绑定"、"前移 loading"先验 hook/资产身份/常驻三件事；
   先定位成本所在窗口（无交互 loading 期分帧零收益）、核修复所需数据在执行时序是否可得、裁剪全量预建要枚举所有补建入口、依赖 native 语义先 runtime probe 再断言等价。决断仍要决断，但建议必须经得起这几条。
 - 只读不改：只产出分析与方案，不修改任何代码。
[返回结构] 样本证据 / 调用链 / 根因 / 修复方案(文件:行 + 改法) / 当前劣化程度 / 预期收益与边界 / 风险与验证 / (可选)需确认的契约点
```

**subagent 各维度深挖要点**（按问题类型在 prompt 里点明）：

- **Lua CPU**：用 location `文件:行` 打开源——循环规模、每帧调用、重复计算、同步 require/加载、可缓存/可降频。对照项目 Lua 编码规范的性能段（如有）。
- **带 `pattern=` 标签的 C#/引擎热点**：在 prompt 里附上脚本打印的模式提示，并指明 [playbook](references/perf-analysis-playbook.md) 对应章节（如 ugui-* → §三 UGUI 三类变化映射表；physics-idle-sim → §三 物理 + QA 验证清单；load-burst → §二 摊平尖峰而非消除总量）。
- **Lua VM GC**：找 Lua 分配源——循环内 `{}` 建表、`..` 拼接、闭包/`handler` 每帧新建、`table.insert` 扩容、`string.format`、临时数组返回；改法常是复用表 / `table.clear` / 缓存闭包 / 预分配。
- **业务 Mono GC**：脚本「真实业务/引擎分配」组才看——装箱、`params`/数组、字符串 marshal、`GetComponent`、每帧 `ToString`、闭包/委托分配。资源检查类走 Editor `FileUtil` 的若疑似编辑器放大，方案里写明"需真机复测确认幅度"，但仍给出可落地改法（如结果缓存）。
- **帧尖刺**：关联当帧事件（加载、实例化、切场景、UI 打开），根因常是"一次性重活在可见帧"，改法多为前移到 loading / 预热 / 分帧。
- **界面开启（VIEW_STATS 超标）**：先按超标维度拆方向——**资源加载耗时高**（prefab 体积/依赖链/同步加载，查界面定义的 prefab 路径与依赖资产）、**显示完成耗时高**（OnOpen/OnRefresh 重逻辑、SubView 链式创建、首帧全量刷新，交叉该 View 在 LUA_HOTSPOTS 的条目）、**点击响应 slow**（点击回调里开界面前的同步重活：同步计算/等回包才 ShowView）、**ViewFPS 开屏卡顿**（结合逐帧 time 续行与当帧尖刺定位卡顿源）、**ViewNode 超标**（prefab 隐藏节点堆积——属资产层问题，修复归属 UE/裁剪 prefab，报告标转出，脚本侧不做补偿）。注意「已合并(父吞)」「未配对」不是异常标记，是点击配对语义。
- **场景切换（SCENE_SWITCH 超标）**：按**六段分解**（前摇/Unity 场景加载/最小 loading 时长白等/业务资源/业务初始化/揭幕）定位——**先算结构性等待占比**（最小 loading 时长白等 + 固定 Delay + 被加载完成回调串行推迟的加载），占比高先修结构（加载前移到 loading 开始、Delay 改事件驱动、双条件门），占比低才优化真实加载本身；需要分段数据时插日志埋点复测。实证参照：某场景冷态 3.86s→1.79s。
- **高频调用（calls/帧画像条目）**：确认调用源（每帧 Update 轮询 / 事件风暴 / 列表逐项刷新），修法优先「降频」——dirty 标记才算、结果缓存、轮询改事件驱动；判断收益看 `luaGc×频率`（稳态 GC 源）与跨 C# 边界次数（interop 成本），单次 µs 级但每帧 ×N 的条目值得改。注意：降频/合并 timer 不得引入跨 timer 同帧数据依赖（项目若有 timer 顺序规则，按其约束）。

> GPU 渲染计数器与内存趋势**不派 subagent**（计数器级）——由主 Agent 在报告里给趋势判断即可。判断口径用 [playbook](references/perf-analysis-playbook.md)：bound 类型与三角形预算（§〇/§四，脚本画像段已算好）、DC 结构与合批漏点假设（§三）、内存趋势与泄漏观察窗口（§一）；用户若问功耗/发热/降频，按 §五 的**代理判定**给独立判断（喘息占比、常驻浪费模式、渲染预算、满载画像），不要答「无法分析」，也不要把「先去跑外部功耗工具」当分析结论。

### Step 3.5 — 对抗性验证（refute-first，独立于提案者）

> **为什么加这一步**：2026-06-29 一轮复核中，抓出"不值得/不可落地"的 3 个假阳性（启动期同步 require 分帧收益≈0、红点按需建时序不可行、条件表达式编译缓存撞 loadstring 禁令）**全部来自一个独立的 skeptic 对抗验证**——做法正是让 skeptic **系统性套用 §收益真实性闸 / 12 条陷阱去攻击提案**（闸与陷阱是"武器"，独立视角是"扣扳机的人"），而非提案 subagent 自己过一遍陷阱自检——**同一个 agent 既提案又自检，会系统性地为自己的结论辩护**。所以在采纳前插一道"另一个人专门来推翻"的闸——即"独立多镜头 review / 对抗验证"在性能分析流程里的落点。

**谁要过这一步**：Step 3 产出的 P0/P1 候选里，凡**改行为/生命周期/契约**，或**收益依赖"窗口/复发/真机"假设**的，必过——**多数 P0/P1 会命中这条，这是有意为之：报告落地前的确定性值得这道成本**。只有纯局部、零行为变更的微优化（如给纯函数结果加侧缓存、复用临时表）可跳过，直接进 Step 4。

**怎么做**：对每条要过的候选，用 **Agent 工具**另起一个 **`Plan` 类型只读 subagent**（**不是原提案 subagent**，独立视角），**一条消息里并发派发**。skeptic 的 prompt 目标是 **REFUTE**（默认"存疑就降级"，不是默认"成立"）：

```
[对抗验证] 提案：<候选一句话 + 提案的改法与预期收益>
[任务] 独立读真实源码，尽最大努力推翻这条"值得改且能安全落地"。重点攻击三条，任一成立即降级：
 (a) 真机收益证伪：收益是不是只在编辑器 Lua 后端下存在（出包会剔除工程自带插桩与后端 hook）？成本是不是落在无交互 loading 黑屏期（分帧无帧可保、总 CPU 不变）？是不是一次性 init 被当每帧成本？（对应 §收益真实性闸 + trap 9）
 (b) 可行性/合规证伪：改法在其必须执行的时序/环境落地得了吗（修复所需数据/时序在执行处可得？依赖不可读 native 语义需 probe？需新增 C# 绑定？trap 10/12）？撞内部硬约束吗（项目 CLAUDE.md 明令禁止的写法）？
 (c) 正确性证伪：会不会在某路径改语义/引入 nil/破坏确定性迭代/红点或事件静默失效/网络回包触达未注册模块？裁剪全量预建有没有漏枚举补建入口（trap 11）？
[返回] 结论(VERDICT_HOLDS / DOWNGRADE_TO_NOT_WORTH / DOWNGRADE_TO_OWNER_DECISION) + 最强反驳（无法 refute 就明说"无法 refute，成立"）+ 新增风险。只读不改。
```

**主 Agent 据 skeptic 结论调级**：`VERDICT_HOLDS` → 保留原 P 级进 Step 4；`DOWNGRADE_TO_NOT_WORTH` → 移出 P0/P1，进 §八 存疑并写明证伪理由；`DOWNGRADE_TO_OWNER_DECISION` → 保留但按 §决断三类止步标真实阻塞（对外契约/内部硬约束/可行性），不硬塞 P 级。**skeptic 与提案者冲突时，冲突本身就是"需在报告里如实呈现"的信号，不许主 Agent 私自和稀泥选一边。**

### Step 3.6 — 从报告建议进入实际代码落地

当用户明确要求“执行优化 / 落地 P0/P1/P2 / 逐项 review / checkout”时，必须先读
本文下方的 `## 性能建议代码落地复核闸`，再开始修改。
分析阶段也要用其中的“动态配置、事务提交、可变所有权、事件因果、收益复杂度”闸过滤建议；
无法满足的候选应直接标为 `REJECT / RESAMPLE / DEFER`，不要先把高风险方案写成“可直接落地”。

实际落地必须执行“修改 Agent → 独立 Reviewer → 原 Agent 修正 → 独立复审至 PASS → 独立 Checkout”的闭环；
撤回的候选也必须做 clean re-review，确认源码、格式和 P4 状态无残留。

### Step 4 — 主 Agent 汇总报告

收齐所有 subagent 返回（含 Step 3.5 对抗验证结论）后，主 Agent 去重、按 **收益 × 确定性** 排 P0/P1/P2，整合成中文 markdown 报告（不写飞书、不建文件，除非用户要求）。**经 Step 3.5 存活（VERDICT_HOLDS）或未触发对抗验证的**修复方案**直接采纳**为"优化建议"；被对抗验证降级的按其结论移出或标阻塞。主 Agent 只做整合、对抗调级与排序，不把已被 subagent 定性且未被对抗证伪的结论重新打回"需确认"。

> **排 P 级前每条 P0/P1 候选必过「收益真实性闸」**（任一不过 → 降级或不立项，并在报告里写明真实状态，别硬塞 P 级）。这四闸是 2026-06-29 一轮逐项复核里把 4 个候选中 3 个判成"不值得/不可落地"的直接原因——热点定位对了不代表值得改：
> - **真机收益闸**：成本真机出包后还在吗？（出包会剔除工程自带插桩与 Lua 后端 hook；`UnityEditor.*` 等编辑器工件真机本无）。只编辑器存在 → 不立项。
> - **窗口闸**：成本落在哪类帧——「可交互稳态帧 / 可交互一次性帧（首屏·切场景）/ 无交互引擎 loading 黑屏 / 编辑器伪影」？只有前两类有真机可感收益。**无交互 loading 期的同步活做"分帧/异步化"近零感知收益**（无帧可保、总 CPU 不变，见 trap 9），不进 P0/P1。
> - **复发闸**：是「每帧稳态热点」还是「一次性 init/启动」？同样字节，一次性 init 的 GC ≠ 每帧 GC——按复发频率给权重，别把一次性 865KB 当每帧成本排 P0。
> - **可行性/合规闸**：改法在其必须执行的时序/环境落地得了吗（trap 10/12）？撞内部硬约束吗（`loadstring` 等，见上 §决断三类止步）？不可行/不合规 → 标真实状态，不硬给 P 级。

```
## 性能采样分析报告

**数据来源**：<文件名>（格式 AI-Profiler-v1 / Lua-only）| 导出时间 | 帧区间 | 各源状态
**采样说明**：Miku 插桩绝对耗时偏大，下文以相对占比 / 尖刺为准；C# Miku 插桩已忽略；Editor GPU 为计数器级。

### 一、概览
- 一句话结论 + 最该优先处理的 1-3 个点（按收益/确定性排序）。

### 二、Lua 热点（耗时 + 高频调用）
- 每项一个 subagent 结论：**样本证据片段** + **调用链** + **为什么慢**（具体代码行）+ 影响面。C# Miku 插桩一句带过，不展开。
- **高频调用小节**（独立固定小节）：脚本「高频调用画像」里 selfMs 榜未覆盖的高频条目——`calls/帧` + 单次成本 + GC 量 + 调用源 + 「降频」改法；无值得立项的高频条目时一句带过。

### 三、GC（业务 Mono + Lua VM）
- 每项：**样本证据片段** + **分配源在哪一行** + 频率/量级 + 根因（subagent 结论）。

### 四、帧尖刺
- 尖刺帧原始行 + 关联事件 + 调用链（subagent 结论）。

### 五、界面开启与场景切换性能（VIEW_STATS / SCENE_SWITCH · 各自独立小节）
- **界面开启（逐界面小节）**：每个超标界面一条 subagent 结论——超标行原文（样本证据）+ 维度拆分（资源加载 / 显示完成 / 点击响应 / 开屏 FPS·卡顿 / 节点使用率，各自现状值 vs 阈值）+ 归因到 `文件:行`（或标"资产层问题转 UE"）+ 改法。达标界面汇总一行；全部达标一句带过。
- **场景切换（逐路线小节）**：每次超标切换（>3000ms）一条结论——路线与耗时原文 + 六段分解方向（结构性等待占比 vs 真实加载）+ 改法或"需按 scene-loading-perf.md 诊断 SOP 埋点复测"；无超标一句带过。
- 两块任一为 NO DATA（真机模式 / 运行时采集器未打点或未在 Play 中录制）时对应小节标灰，写明原因与补采方式（Editor 本地重采），不得臆造。

### 六、GPU / 渲染 与 内存（主 Agent 趋势判断，不派 subagent）
- 渲染计数器是否偏高 + 可能成因。
- **内存趋势**：MEMORY trend 列（前/后窗口均值变化）+ LUA_MEM_TREND（Lua VM 存量 Δ）——有上升信号时给「泄漏/累积 vs 业务合理增长」的判断与排查方向（Lua 侧结合 TOP_LUA_VM_GC；Mono/Native 侧标注需 Memory Profiler 复核）；无信号一句"内存趋势健康"带过。趋势列为 `-`（样本不足）时如实说明，不硬判。

### 七、优化建议（P0/P1/P2）
- 直接采纳 subagent 的修复方案：`文件:行` + 样本证据 + 调用链 + 现状劣化程度 + **具体改法** + 预期收益（相对措辞）+ 风险/验证。每条标清**成本窗口**（可交互稳态/可交互一次性/无交互 loading/编辑器伪影）与**复发**（每帧/一次性），P 级须与收益真实性闸自洽。
- **验证闭环**：建议落地后同场景同操作重采一次，`--diff` 对比本次导出验证收益到账（改善榜找到对应条目才算生效），并把结论回填对应 CL/复盘。
- 标题健康灯规则：存在 P0 为红灯；无 P0 有 P1 为黄灯；无 P0/P1 为绿灯；数据源 critical 缺失或需重采为灰灯。灰灯报告不得用缺失维度推导 P 级。
- 止步三类按 §决断处置：① 对外契约 → 给改法 + 标需谁拍板；② 内部硬约束（loadstring 等）→ 不提违规改法，找合规替代或判不可落地；③ 可行性硬阻塞 → 判不可纯静态落地 / 需 runtime probe。其余一律给结论，不甩回用户。

### 八、存疑 / 需进一步验证
- 仅放真正无法从代码定论的：需真机复测幅度、需 FrameDebugger / Memory Profiler 的维度。不放"能查而没查"的偷懒项。
```

## 改法准确率 — 提案落地前必验的 12 类陷阱

> 这些不是"多加犹豫"，而是让**决断的建议本身正确**的核查点——热点定位对了，改法仍可能错。每条都源于 2026-06-26 / 2026-06-29 两轮逐项复核里**被复核打回 / 实测回退 / 对抗性证伪**的真实假阳性建议。subagent 给改法前、主 Agent 采纳前都过一遍。1-8 来自 06-26（多为"读取/缓存/前移"的微观陷阱），9-12 来自 06-29（多为"收益方向/可行性/合规"的宏观陷阱，与 Step 4 的收益真实性闸配套）。

1. **缓存 getter 结果前，先确认 getter 无副作用**。若 getter 内部惰性创建/注册下游依赖的状态（`NeedXxxItem` / `GetOrCreate` / lazy-init），缓存返回值会跳过该副作用 → 状态被清/重建后读到空。**验法**：grep getter 调用链有无 `Need*/GetOrCreate/_create*/lazy`，有则缓存须配套失效或放弃。*反例*：红点模块 `_getRuntimeKey` 缓存——`GetRedDotKey→NeedRedDotItem` 每帧惰性重建 item，`GetRedDotValue(runtimeKey)` 只读不建；缓存后清空红点/重连致红点静默消失。（注：同模块的 `GetRedDotValueByID(configKey,dynamicID)` 反而**内含** NeedRedDotItem，别拿它当"读不建"反证。）

2. **断言"跨边界 marshal / monoGc"前，核实读的是不是 C# 真值**。`UnityEngine.Time.*`（realtimeSinceStartup / frameCount / deltaTime）等在源工程是绑定层 `Time.lua` 的 **Lua shim 表字段**（每帧由 shim 侧刷新，如 `SetDeltaTime`/`SetFrameCount`；realtimeSinceStartup 仅周期性从 C# 校准），读它是纯 Lua 取值、**非每次跨边界 marshal**——按"240B/次跨边界、N 万次 = X MB"立项会高估收益。**验法**：可疑 `xx.yy` 先看绑定层目录有无对应 shim 定义。*反例*：Time:GetServerTime 缓存 realtimeSinceStartup 被复核指出 GC 前提不成立（真值是 shim）。

3. **"延迟同步操作到下帧 / 合帧"不是零风险**。把 `ForceRebuildLayoutImmediate` / 立即 reflow / 立即写状态改成 `SetNextFrame` 合并前，查**调用方是否同帧同步读取该操作的结果**（典型 LoopListView 同帧读 cell 尺寸 pin 边缘）。**验法**：grep 紧随调用后是否同步读被延迟操作写的状态（尺寸/位置/controller 值）。*反例*：聊天 cell `RebuildLayout` 合帧 → 列表插件同帧取尺寸拿到旧值 → 高个消息滚入错位（实测回退）。

4. **立项前 re-read 当前源码，别拿假想旧基线下结论**。说"每帧无条件 X / 无 Y 闸 / 没池化 / 各自独立读"前先读 HEAD——优化可能已落地，剩余增量比报告想象的小。**验法**：grep 目标函数里有无 `if ... return` 早退 / 计数器 / dirty 标记 / `Blocked` / 已有缓存字段。*反例*：某地图 FOD 报告称"无静帧总闸"，实际粗闸 + 阻断开关 + 相机快照已存在，真实可做的只剩"移动帧少读一次相机"。

5. **事件驱动缓存失效，必须枚举所有触发路径**。一个 Proxy 常有多套刷新入口（`OnDataUpdate` 全量快照 / `OnExtraUpdate` 增量 / `ClientUpdate` 按秒），只订其一会漏失效。**验法**：grep 该 Proxy 全部 `InvokeEvent*` 对照订阅集；看同类消费方惯例（多数 belt-and-suspenders 两套都订）。*反例*：某活动气泡只订全局开启/结束事件，而 `OnDataUpdate` 只发 per-activity 的开启/结束事件 → 该路径脏缓存。

6. **改法需新增 C# 方法/重载 = 需重生成 ToLua 绑定，不是纯 Lua 改**。Lua 要调的 C# 新增重载/方法，必须重生成对应 `*Wrap.lua` 绑定才调得到，且需核实 ToLua 版本支持按参数个数分派重载。这类**无法在纯分析/Lua 环境落地**，标"需 C#/ToLua owner + Unity 重生成绑定"，别当纯 Lua 提案。*反例*：某绳索特效 `SetPositionAt(int,float,float,float)` 标量重载。

7. **"前移到 loading 黑屏期"前，验证三件事缺一不可**：① loading 阶段有对应预载 hook（消费方不是深层交互才打开的弹窗/按钮）；② 资产身份在 loading 期**可知**（不是打开时才现场解析、且会被运行时事件重解析）；③ 预载不会对"可能整局不打开"的 UI 造成常驻内存回归。**验法**：找消费方的打开路径 + 资产 id 解析时机 + 有无 loading seam。*反例*：某弹窗的皮肤 id 打开时才由数据现场解析 + 随服务器数据更新重解析、且只经场景内按钮点击打开（无进场 hook）→ 前移不可行（实测回退）；某玩法 NPC 无 spawn 消费的池、首批在流程遍历时才定 → 前移=owner-gated 重构。

8. **`:ToString():find("X")` 子串匹配 ≠ `:Equals(typeof(X))` 精确匹配**：前者命中子类/同名串，后者仅精确类型。换精确匹配前确认目标类型集封闭（无派生轨道/子类）。想保留"含子类"语义又零 GC 用 `IsAssignableFrom`——但**先确认它在工程已绑定**（源工程 System.Type 未绑 `IsAssignableFrom`，`:Equals(typeof(...))` 才是惯例）。

9. **成本已在"无交互窗口"时，"分帧/异步化"收益≈0**。把同步重活改分帧/异步前，先定位它落在哪类帧：① 可交互稳态帧（有帧要保）② 可交互一次性帧（首屏/切场景，分帧有感）③ **无交互引擎 loading 黑屏期**（无帧可保）④ 编辑器伪影。只有 ①② 有真机可感收益；③ 处的同步活分帧只是把一坨拆成多帧、**总 CPU 不变、还可能因调度更慢**——零感知收益，不该排 P0/P1。**验法**：看入口在 `Application:Prepare`/引擎 loading 链 vs 登录后/可交互后；看完成 cb 是否 gate 在"全做完才继续"（gate 住 → 异步窗口内无业务帧、收益归零）。*反例(06-29)*：基础模块包约 647 文件同步 require 在启动准备阶段（引擎 loading 期），提议异步分帧，对抗复核证伪收益≈0。

10. **修复所需的数据/时序，必须在"修复执行处"已就绪**。"按 X 裁剪 / 过滤 / 按需建"前，确认计算 X 的数据在该修复必须执行的生命周期阶段（Ctor / Init / AfterInit / 事件回调）已可得。结构在 Ctor 建、而判定数据要 AfterInit 才有 → Ctor 处根本算不出过滤集，"按需"改造被迫挪时序 + 跨多入口，复杂度暴涨。**验法**：对齐"修复点的生命周期"与"它依赖数据的就绪时机"。*反例(06-29)*：红点某类对象结构在 `Init`(Ctor 期)遍历全集建，而"玩家拥有/可获得"集要 `AfterInit` 才算得出 → 无法静态裁剪，报告"按需建"方案时序上跑不通。

11. **裁剪"登录期全量预建"前，枚举所有惰性补建入口，否则静默破正确性（非性能问题）**。把"AfterInit 遍历全集建树"改成"按需建"，必须把幂等补建前置进 *所有* 后续推值/更新入口；漏一个，该对象后来变相关时其**多父 / 跨 dynamicId 边（BindingCP 类）不会被自动补** → 红点 / 聚合静默不亮。**验法**：grep 该系统全部推值入口（`OnXxxChange` / `UpdateXxx` / `*ByMsg` / `NewXxx`），对照"建结构"只在哪几处；多父边是否只在某个 `Ensure*` 里建。*反例(06-29)*：红点裁剪后某组父子边只在一个 `_Ensure*Structure` 里建，而 4 个增量入口都不调它 → 某列表红点不亮。与 trap 5 互补（5 管缓存失效，11 管结构补建）。

12. **改法正确性 hinge 在 native / 不可读语义时，等价性要 runtime probe 而非读码断言**。当改法成立与否取决于 native 函数 / ToLua 绑定 / 引擎黑盒的实际行为（读不到源），"它等价于 Y / 它内部是 Z"是 guess 不是 evidence——必须真机/Editor probe 一次再断言；且别把"碰巧对"的结论顺手推广到别处。**验法**：可疑原语先 `assert(eval(c) == 等价写法)` 跑一次。*反例(06-29)*：把 native `eval(x)` 当 `loadstring(x)()`，实则 `eval` 是"先试 `return x` 取值、失败再退回当语句执行"的两段 REPL；裸读码断言等价，会误导把同款"加 `return` 前缀缓存"推广到 action 语句而编译失败。

### 落地复盘结论

- P1 只代表候选优先级；落地前必须重读 HEAD，已优化、灰灯需重采、缺少局部等价改法的项要降级或转专项。
- 红点优化优先做订阅范围收敛；不要缓存带 `Need*`/lazy-init 副作用的 runtime key。
- UI layout 只能消重复 rebuild；不能把同帧尺寸依赖盲目延后，延迟回调必须加消息身份或版本 guard。
- 延迟刷新要使用源码确认过的取消 API，例如 `ed.Timer:RemoveTimer(handle)`，不要假设 handle 自带 `Stop`。
- 列表 delegate 不应捕获可能过期的局部快照；快照放 `self` 或证明 delegate 生命周期严格绑定。
- 每帧 Vector3/Quaternion 优化必须同时证明数学等价与返回对象生命周期安全。
- 首帧整屏 UI、跨 view/资源/列表生命周期的 P1 不混入综合落地，单独专项复测和设计。

**2026-07-13 追加（一次批量落地：25 项落地 / 9 项评估后不做）**

批量落地阶段的「不做闸门」——候选命中任一条即不混入批量落地，转专项或直接放弃：

- **行为轨迹闸**：报告自认"会改变运行时行为/轨迹"的候选（如寻路 agent 集中 pair pass 改群体轨迹），静态 review 无法闭环"语义一致"，必须配行为回归专项才可落。
- **浮点等价闸**（细化上一组"每帧 Vector3/Quaternion 优化须证数学等价"条，给出可判定两分法）：纯**标量展开**（同一运算序列去掉 Vector 中间对象，可逐位一致实测）无漂移可做（某玩法 FOV / CameraFrustum 已落）；**换公式**（三角恒等式合并、角度加法替代两次反三角）有浮点漂移，无 golden test + 视觉验收前置的不做（绳索三角核即此类）。
- **prefab 手术闸**：改法涉及 prefab 结构调整 + 异步槽位 generation guard 的，需 UE/QA 配合，不做纯代码批量落地。
- **对抗互拆信号**：同一候选在多份报告出现、且各自方案被对方的对抗验证发现不同漏洞（实例：漏刷按钮/特效时序/教程检查）→ 方案空间本身不稳，低频窗口的回归面大于收益，整条放弃而非择一硬落。
- **帧缓存形态闸（传参优先）**：同帧多处重复取值（如城市时间）的候选，若改法是引入**全局帧缓存** → 该形态不做，改走"调用方算一次、显式传参"落地（昼夜/时间表两处即此模式）——帧缓存的失效时机与跨系统读序都是新风险面。闸的是缓存实现形态，不是优化目标本身。
- **真机取证前置项不硬做**：报告自身标注"需真机确认命中率/幅度后再落"的候选（FOD 静帧谓词、某列表 cell 池化），批量落地阶段不替它补前置取证，保持不做留给专项。

执行期实证（落地改代码时自查，均为批量落地 review BLOCK 抓出的实错）：

- 绑定层 fake-null（ToLua 等）判空必须显式 `== nil`（走 `__eq` 元方法到 C# `op_Equality`）；`x and x.y` truthy 短路漏掉已 Destroy 的 userdata。
- 删/改 Lua 私有方法签名，grep 范围必须含技能/自动化脚本里的内嵌 Lua 字符串——自动化驱动脚本内嵌 Lua 调用私有方法，漏改会**静默降级**而非报错；自动化驱动应改走公开 API。

## 报告质量红线

- **结论必有源码依据**：每个「慢/GC 高」判断落到 subagent 读过的具体代码行，禁止泛泛而谈。
- **结论必有样本证据**：每条 P0/P1/P2 必须带热点榜/GC 榜/帧尖刺原始片段、调用链、当前劣化程度、优化后预期收益与边界；缺任一项不得升级到 P0/P1。
- **框架入口不得背锅**：事件分发器 `_event.__call`、包装调用 `_xpcall.__call`、调度组件/Timer 入口、UI 基类 Update 入口、PlayerLoop/UpdateBeat 只作为链路证据；报告必须继续追到业务 handler/组件。追不到就标"调用链证据不足/需重采或补采"，不能把框架文件列为优化对象。
- **建议决断且可落地**：给 `文件:行` + 具体改成什么，而非「考虑缓存一下」「需谨慎评估」。能从代码判定的一律给结论。
- **止步只有三类，处置不同**（见 §决断三类止步 / Step 4 可行性·合规闸）：① **对外契约**（网络协议/结算/序列化/跨端格式/服务端约定）→ 仍先给推荐改法 + 标「需 <谁> 确认契约」；② **内部硬约束**（项目 CLAUDE.md 明令禁止的写法）→ 不提违规改法，找合规替代或判不可合规落地，**别"给个违规改法再标需拍板"**；③ **可行性硬阻塞**（数据/时序在执行处不可得、依赖不可读 native 语义、需新增 C# 绑定）→ 判不可纯静态/纯 Lua 落地或需 runtime probe。其余一律给结论，不甩回用户。分析阶段产出的是方案（不改代码），实际落地由用户拍板——这与项目规则的"不擅自改代码"不冲突。
- **C# 后端插桩不分析**：`MikuLuaProfiler::*` / `EditorLoop` / 工程自带插桩桥 / 后端的 `Stack·Dictionary·StringBuilder` churn 已知是测量开销，报告里一句带过；要 C# 业务 CPU 另采 Miku-off。
- **不夸大数字**：插桩放大的绝对 ms 用相对措辞（「占比最高」「比次高项高一个量级」），不报「省 X ms」伪精确收益。
- **数据不足要明说**：某数据源 `NO DATA`（如 Lua 后端未捕获、未进 Play、工程自带插桩漏关与后端冲突）、原生分段加载失败、walked 0、采样太短、热点全在框架底层 → 如实说明并建议重新采集（进 Play、采够帧、覆盖目标场景）。critical 缺失维度对应报告用灰灯，不得臆造。

## 性能建议代码落地复核闸

当性能报告要转成实际代码、Prefab 或 P4 changelist 时执行本节。目标不是“尽量多改”，而是只落地收益有证据、语义可证明、维护成本合理的候选。

### 逐项闭环

1. 先按“同一目标 + 同一根因”跨报告去重，保留一条权威候选。
2. 重读当前 `HEAD`、完整调用链和所有 mutation/lifecycle 入口，判定 `MODIFY / REJECT / RESAMPLE / DEFER`。
3. `MODIFY` 由一个 Agent 实现；另一个独立 Reviewer 以 refute-first 方式审查，不得由修改者自审代替。
4. Reviewer 的问题必须返回原修改 Agent；修正后重新独立复核，直到明确 `PASS`。
5. `REJECT` 或试改撤回后也要独立 clean review，确认 0 hunk、无残留字段/文件、格式和 P4 状态恢复。
6. `PASS` 后再由独立 Checkout Agent 创建单项 pending CL；禁止自动 submit。

### 语义与状态闸

1. **可观察顺序与状态必须等价**：不仅比较返回值，还要比较公开可读状态、后续分支可见副作用、getter 调用次数与顺序、惰性创建、迭代顺序、日志/翻译调用、异常时点和同步重入。查询/判定命名不代表无写入；提前返回或跳过尾部前，审计被跳过调用的所有 mutation 与公开 reader。保持原 guard 的求值域，不要把 `floor`、格式化或 getter 从“控件存在/可见/功能开启”分支内提到外面。标量化、内联、常量提取或去 wrapper 前，沿真实调用链核实工程中实际存在且可达的 metatable/operator/getter/template method/hotfix 替换路径；冻结数学等价不等于动态派发等价，只有该动态派发无法证明等价时才 `DEFER / REJECT`，不得用假想 proxy 阻塞。所谓“异常时 fallback 到旧算法”不够：若 fallback 前已额外读取配置或 getter，语义已经改变。
2. **缓存必须事务提交**：解析、合并、Reload、UI setter 或异步回调全部成功后，才能提交缓存内容和“已渲染”标记。候选结果先放局部变量；失败时保留可重试状态。不得让已展示 delegate 读到半成品表，也不得在可能抛错的 setter 前提交 dirty/cache 标记。
3. **配置索引必须与运行时数据同源**：`bundleId`、行数、cell count 或生成期静态 index 都不是内容版本。检查 `cfg` 是否会被 AB、region、backdoor、hotfix 的 `set/patch/replace` 改写，以及索引是否随之重建。无法证明同源和失效时，保留对当前运行时表的遍历或拒绝缓存。
4. **可变对象不能靠旁路索引假装不可变**：若公开 API 暴露树、节点、DTO 或可变 key，必须覆盖所有 Add/Remove/Replace/Clear/原地改 key 路径。检查重复键首/末命中、旧 key 强引用、删除后的 GC 和热更新旧实例。靠 miss 扫描“自愈”若破坏 O(1) 收益或仍留陈旧引用，应拒绝索引方案。
5. **事件与 RPC 不能假设顺序**：删除预刷新、增加监听或跳过刷新前，确认事件携带目标身份和单调 revision/request generation。RPC 允许多请求在途时，没有版本的全量回包可能覆盖更新后的增量结果；无法丢弃旧响应就不要改状态机。
6. **全量刷新可能承担隐式职责**：逐字段阅读 `SetData/Reload/Refresh`，确认它是否顺带更新员工、红点、动画、布局、事件解绑或异常恢复。远端请求与本地刷新要拆开审查；去掉重复 RPC 时不能一起跳过初始化、监听、已有数据渲染或本地刷新。
7. **公共 API 先做全仓调用矩阵**：Lua 旧函数可能默默接收额外参数；新增第二参数会撞已有调用。核对 `nil/false/0/table`、枚举值 0、对象池复用和调用者对 DTO identity 的依赖。即便结果相同，也要保留参数解释和状态提交时点。
8. **池化与异步回调必须有唯一 ownership**：证明创建、部分初始化、异常、Abort、OnDisable、OnClose、回池和 manager 失败的每条路径只释放一次且不泄漏；cleanup 不得覆盖原始异常。覆盖同一/重叠输入 List、重复 Perform、Perform/Abort 交错和部分创建失败。缺完整 owner/cancel 协议时，不把池化作为微优化落地。
9. **稳定 callback 必须读取调用时状态**：只捕获槽位或 owner identity，调用时从 `self` 读取最新 hero/skill/object；触发可重入外部回调前先清 pending，且 Hide/Remove/Revert/Recycle/Destroy 都要清理。
10. **复用 UI cell 必须完整 reset**：先恢复 active/show 再写新数据；不足才创建，多余只隐藏，最终 Close 才销毁。保留原 next-frame layout、尺寸回调、选择态和其他跨 item reset 语义。
11. **Prefab 必须在正确 Editor 中原子修改**：确认 Unity Editor 打开的是当前 P4 workspace。Controller state、visible object、节点和序列化引用必须在一次受控 `LoadPrefabContents/Save` 中一致删除，并验证父 Prefab、Animator/Timeline/字符串反射引用和 Missing Object。没有原子工具时不手改 YAML。

### 收益与复杂度闸

- 优先看目标函数的 self 成本，不把 inclusive 子树全部算成候选收益。
- 可消除调用量只按能归属到目标 callsite 的样本/调用链与实际命中分支计算；若只有同名 helper 的全局聚合 calls，只能作为上界或判 `RESAMPLE`，不得充当该处可消除次数。
- 计算调用频率、集合上界、成本窗口和复发频率；一次性小表不等于每帧热点。
- 安全实现若需要新增大块状态协议、内容指纹或跨模块版本机制，先比较可消除成本。几十到上百行同步协议只为小额 P1/P2 时，通常应 `REJECT / RESAMPLE`。
- 性能方向正确不等于当前改法值得落地；“没有小而严格等价的实现”是有效结论。

### 最小验证矩阵

- 数据：`nil / false / 0 / empty / malformed / duplicate / reorder / missing / key drift`。
- 生命周期：首次、重复刷新、A→B pending→A、Open/Close、Disable/Recycle/Destroy、快速重入、多实例。
- 异常：每个解析、getter、setter、Reload、回调点注入异常，验证重试和部分提交。
- 动态源：AB/backdoor/hotfix、全量与增量更新、乱序 RPC、配置原地 mutation。
- 一致性：以独立 legacy oracle 做随机增删改差分，同时比较返回值、调用轨迹、公开可读状态和异常时点；不要让测试复用新实现的 helper。

### P4 与文件卫生

- 先确认目标没有用户改动；以当前 client 的 `LineEnd` 与 `haveRev` 对应 depot 内容为编辑基线。若 forced diff 仅显示编码/EOL 漂移，先判定来源；只有能明确归因于本任务/Agent 且确认无用户改动时才恢复基线，否则停止并保留现状。禁止把等价转码混入 CL，也不要拿另一个 client 的工作文件判格式。
- 每个独立项一个 pending CL，只包含该项文件；default changelist 保持为空，不 submit。
- 同一 client 中，同一文件不能同时属于两个独立 CL；需要先落前置 CL，或明确 `DEFER`，不要混项。
- 目标被他人 pending CL、过期 shelf 或 Jenkins 占用时，交原 owner resolve/rebase；不要叠改。
- 修改前及 Checkout 前都验证 `headRev == haveRev`、forced diff 无编码/EOL 全文件噪音；若评审期间 `headRev` 变化，先同步并基于新 `HEAD` 重读调用链、重跑候选复核，不得直接重放旧补丁。Checkout 后验证 `p4 describe -s`、default、文件数与描述。
