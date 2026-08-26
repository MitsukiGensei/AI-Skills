# profiler-analysis / unity — Unity 工程侧采样与导出配套

[English](README.md) | **中文**

本目录是 `profiler-analysis` 技能"导出之前"的那一半：在 Unity Editor 里一键录制 Unity 原生 Profiler（C# CPU / GPU / 内存 / GC / 帧时间线）+ 可选的 Lua 后端（Lua CPU + Lua VM GC）+ 运行时采集器（界面打开 / 点击响应 / 开屏帧率 / 节点使用率 / 场景切换 / 脚本 VM 内存），合并导出成 `AI-Profiler-v1` 多 section 文本，交给技能脚本分析。

**通用性**：纯 C#，不绑定任何游戏框架；**没有 Lua 的工程也能直接用**（Lua 维度为 NO DATA）。Lua 通过后端抽象接入，Lua 侧打点通过纯 Lua 适配器桥接。文件按 `Assets/...` 镜像存放，合入时整目录复制到目标工程即可（不含 `.meta`，Unity 自动生成）。

## 目录

| 路径 | 程序集 | 作用 |
|---|---|---|
| `Assets/AIProfiler/Runtime/AIProfilerCapture.cs` | 运行时 | **通用采集器**。工程在 UI / 场景流程里打点：`MarkClick / MarkViewLoadStart / MarkViewResourceLoaded / MarkViewShown / BeginViewFpsWindow / ScheduleViewNodeStats / BeginSceneSwitch / EndSceneSwitch`，或直接 `RecordViewOpen / RecordViewNodes / RecordSceneSwitch / RecordScriptMemory / RecordLine`。面板 StartRecord 时 `BeginCapture()`，StopRecord 时 `EndCapture()` 取回文本 → `VIEW_STATS / SCENE_SWITCH / LUA_MEM_TREND`。Play 中自带隐藏驱动每帧 Tick（帧率窗口）并按 `ScriptMemoryMBProvider` 周期采内存。阈值在 `AIProfilerCapture.Thresholds` |
| `Assets/AIProfiler/Runtime/LuaProfilerBackend.cs` | 运行时 | **Lua 后端抽象** `ILuaProfilerBackend` + Null 实现 + `LuaProfilerBackend.Current/Register`；`AIProfilerDeviceControl.OpenLuaProfiler()` 设备侧门面（接到 GM 菜单） |
| `Assets/AIProfiler/Runtime/Miku/MikuLuaProfilerBackend.cs` | 运行时（`#if AI_PROFILER_MIKU`） | MikuLuaProfiler 适配：基于上游公开 API，扩展点反射探测。详见 [Miku-LuaProfiler/README.md](Miku-LuaProfiler/README.zh-CN.md) |
| `Assets/AIProfiler/Runtime/DeviceFrameRecorder.cs` | 运行时（`#if UNITY_EDITOR \|\| AI_PROFILER_DEVICE`） | **真机无限帧采集器**：设备按 600 帧 / 32MB 双重闸分段写 Profiler binary log 到 `persistentDataPath/ai_profiler_frames`，段关闭后发布 `.ready`；Editor 经 `persistentDataPath/ai_profiler_control/command.txt` 下发 start/stop、`state.txt` 回报。Lua 采样开关经后端委托（可无） |
| `Assets/AIProfiler/Editor/AIProfilerWindow.cs` | Editor | **AI Profiler 面板** `Window/Analysis/AI Profiler`：Editor 本地 / 真机连接两种模式；打开即自动开 Unity Deep Profile（+ Lua 深度采样，若有后端）并调用 `AIProfilerCapture.DisableCompetingLuaProfiler` 关掉工程自带的冲突插桩；无上限录制（分段 binary log 落盘 `<项目>/ProfilerLogs/raw/<时间戳>/seg_*.raw`，约 256MB / Deep 16 帧 / 非 Deep 600 帧三重闸）；录制期采样流污染监听；ADB 一键连接（Unity 34999 + Lua 2333 端口转发）与设备段后台 pull。附带菜单 `Window/Analysis/AI Profiler Dump Suspect Frames`（定位 `BeginSample` 早退泄漏） |
| `Assets/AIProfiler/Editor/AIProfilerExporter.cs` | Editor | **统一导出器**：逐段 `ProfilerDriver.LoadProfile` 累加，用 `RawFrameDataView` 线性扫描主线程原始样本流聚合 C# 热点（不逐帧构树）；合并 Lua 聚合、`ProfilerRecorder` 内存/渲染计数器（含头尾窗口趋势）、`FrameTimingManager` GPU 帧耗时、运行时采集文本；写 `Assets/ProfilerLogs/yyyy_MM_dd_HH_mm_ss.txt`。工程自带插桩的噪声特征可追加到 `ExtraInstrumentCsMarkers / ExtraInstrumentLuaLocations` |
| `Assets/AIProfiler/Editor/AIProfilerAutomation.cs` | Editor | **无人值守驱动**：把面板私有的 StartRecord / StopRecord / ExportForAI / CleanRecord 及 Deep / Lua 开关暴露成 `Tools/AI Profiler Auto/*` 无参 MenuItem，供远程菜单执行类工具做批量采集 |
| `Assets/AIProfiler/Editor/Integrations/AIProfilerSkills.cs` | Editor（可选） | unity-skills（MCP）扩展：`aiprofiler_connect_adb / start / stop / export / status`，让 Agent 全自动跑 A/B 采样。依赖 unity-skills 的 `[UnitySkill]` 特性，不用 unity-skills 就不合入 |
| `Lua/AIProfilerCapture.lua` | Lua（可选） | **纯 Lua 适配器**，无框架依赖（不用 class / 自定义事件 / 定时器）：把 Lua 侧打点桥到 C# 采集器，周期上报 `collectgarbage("count")`；附 pcall 守卫 `installErrWatch`（上报被吞掉的 lua error——error unwind 会打断原生 Profiler Begin/End 配对，是采样流污染嫌疑） |
| `Miku-LuaProfiler/` | — | 上游链接、适配器设计、真机链路扩展点的参考补丁（不含 Miku 源码） |

## 宏

| 宏 | 加在哪 | 作用 |
|---|---|---|
| `AI_PROFILER_DEVICE` | 真机 Development 包 | 编入 `DeviceFrameRecorder`（真机无上限 Unity 帧采集）。正式包不加 |
| `AI_PROFILER_MIKU` | 用 Miku 的工程（Editor + 真机包） | 启用 `MikuLuaProfilerBackend` |
| `USE_LUA_PROFILER` | 用 Miku 的真机包 | Miku 自己的宏，gate 其 `HookLuaSetup` |

## 接入步骤

1. 复制 `Assets/AIProfiler/` 到工程。无 Lua 的工程到此即可打开 `Window/Analysis/AI Profiler` 录制导出（Lua section 为 NO DATA）。
2. **打点**（想要 VIEW_STATS / SCENE_SWITCH 有数据）：在 UI 框架的"点击 → 开始加载 → 资源加载完成 → 显示完成"和场景切换流程里调 `AIProfilerCapture.Mark*/Begin*/End*`；打点在非采集期是空操作，可常驻。C# 工程直接调；Lua 工程用 `Lua/AIProfilerCapture.lua`：
   ```lua
   local Capture = require("AIProfilerCapture")
   Capture.init({ cs = CS.AIProfiler.AIProfilerCapture })   -- ToLua 写生成的绑定类
   -- 每帧 Capture.update(dt)；UI/场景流程里 Capture.markClick / markViewLoadStart / ... / endSceneSwitch
   ```
3. **脚本 VM 内存趋势**（LUA_MEM_TREND）：C# 侧 `AIProfilerCapture.ScriptMemoryMBProvider = () => ...`，或 Lua 适配器每帧 `update` 自动周期上报。
4. **工程自带冲突插桩**（如引擎原生 Deep Lua）：`AIProfilerCapture.DisableCompetingLuaProfiler = () => {...}`、`IsCompetingLuaProfilerActive = () => ...`（写进 META `deepLuaNative`）。没有就不设。
5. **Lua 后端**：见 [Miku-LuaProfiler/README.md](Miku-LuaProfiler/README.zh-CN.md)。
6. **真机**：打包加 `AI_PROFILER_DEVICE`（要 Lua 再加 `AI_PROFILER_MIKU` + `USE_LUA_PROFILER`）；GM 菜单接 `AIProfilerDeviceControl.OpenLuaProfiler()`（返回 false = 当前包无 Lua 后端）；设备 USB 授权 adb 后面板切「真机连接」→ `ADB 一键连接`。
7. 工程自带插桩的噪声特征同时配到导出器（`AIProfilerExporter.ExtraInstrumentCsMarkers` 等，启动时追加）和分析脚本（`scripts/profiler_config.json`），两边口径一致。

## 使用流程速记

- **Editor 本地**：开面板（保持打开）→ 进 Play（有 Lua 后端时确认其 Hook 已装）→ `StartRecord` → 操作 → `StopRecord` → `ExportForAI` → 技能脚本分析 `Assets/ProfilerLogs/<最新>.txt`。`CleanRecord` 清 `ProfilerLogs/raw`。
- **真机**：Development 包 → （需要 Lua 时）设备触发 `OpenLuaProfiler()` → 完整重启 → USB 连电脑 → 面板「真机连接」→ `ADB 一键连接` → 同上。易崩场景先关"同时采集 Lua"走原生安全模式。
- **批量/无人值守**：`Tools/AI Profiler Auto/*` 菜单或 unity-skills 的 `aiprofiler_*`；Deep / Lua 开关必须在非 Play 期改（会触发重编译 / 下次 Play 生效）。

## 已验证

四种配置用 Roslyn 对着 Unity 2022.3.62f2 的引用程序集编译通过：Editor 无 Lua、Editor + 上游 Miku 源码（`AI_PROFILER_MIKU`）、Player `AI_PROFILER_DEVICE`、Player + Miku。未做 Unity 内的运行验证——面板与导出器逻辑是从源工程原样迁出（只替换了 Lua 桥与后端耦合），运行行为以源工程为准。

## 已知约束

- 导出是主线程同步，期间 Editor 无响应属正常，可随时取消，已解析帧照常导出
- `ProfilerDriver.LoadProfile` 单次约 2000 帧上限是 Unity 硬限制，无上限录制靠分段绕开；任意段加载失败 / walked 0 都意味着该时序不可恢复，脚本会标 critical
- 录制期 Console 若在刷 `Missing Profiler.EndSample`，当前段已被污染（反序列化必失败），先消灭污染源再重采；`Dump Suspect Frames` 菜单用于点名泄漏的 `BeginSample`
- `AppDomain.FirstChanceException` 在 Unity 2022.3 Mono 不派发，被吞的脚本层异常只能靠脚本侧守卫（Lua 适配器的 `installErrWatch`）
- 行格式契约（`[ProfilerUtils][<Type>] <label> [<subject>] - ...`、`mikuDeep=` / `hookReady=` 等 META 键）沿用 v1，分析脚本按此解析，改动需两边同步
