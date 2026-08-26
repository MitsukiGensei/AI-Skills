# MikuLuaProfiler 接入说明（第三方，不随本仓库分发）

[English](README.md) | **中文**

AI Profiler 的 Lua 维度（Lua CPU / Lua VM GC / 真机 Lua 采样）默认以 **MikuLuaProfiler** 作为参考后端。它是第三方开源库，本仓库**不附带其源码**：

- 上游仓库：<https://github.com/leinlin/Miku-LuaProfiler>（MIT）
- 本目录 `patches/` 基于的上游基线见 [patches/UPSTREAM_BASE.txt](patches/UPSTREAM_BASE.txt)

## 用不用得上

| 工程情况 | 做法 |
|---|---|
| 没有 Lua | 什么都不用做。`LuaProfilerBackend.Current` 是 Null 实现，导出 LUA_HOTSPOTS 为 NO DATA |
| 有 Lua，用 Miku | 装上游 Miku → Player Settings 加 Scripting Define `AI_PROFILER_MIKU` → `Assets/AIProfiler/Runtime/Miku/MikuLuaProfilerBackend.cs` 自动生效（Editor 本地采样即可用）。要真机无上限采样再打下面的补丁 |
| 有 Lua，用别的 profiler | 实现 `ILuaProfilerBackend`（`Assets/AIProfiler/Runtime/LuaProfilerBackend.cs`），启动时 `LuaProfilerBackend.Register(实例)` |

## 适配器的设计：只依赖上游公开 API，扩展点用反射探测

`MikuLuaProfilerBackend` 编译期只用上游已有的成员：`LuaDeepProfilerSetting.Instance`（`isLocal / isRecord / isStartRecord / ip / port / m_isDeepLuaProfiler`）、`LuaProfiler.RegisterOnReceiveSample / UnRegistReceive / mainL`、`Sample.RegAction / UnRegAction` 与 `Sample` 字段、`HeartBeatMsg` 类型、`NetWorkMgrClient.Connect / Disconnect / GetIsConnect`（Editor 程序集，反射调用）。

AI Profiler 真机链路需要的几处能力上游没有，**无法用 override / 扩展方法从外部补上**（都是类内部行为：Hook 安装时机、采样休眠、心跳载荷、发送队列）。适配器对这些成员用反射探测，**打了补丁就启用，没打就降级**：

| 扩展点 | 用途 | 未打补丁时的行为 |
|---|---|---|
| `HookLuaSetup.IsInitialized` / `IsDeepProfilerReady` | Editor 本地 StartRecord 前校验 Hook 与 Lua VM 就绪 | 以 `LuaProfiler.mainL != IntPtr.Zero` 代替 |
| `LuaDeepProfilerSetting.ProfilerWinOpen`（实例属性，持久化） | 进 Play 时按"面板是否打开"决定装不装 Hook | 上游是 `static bool`，同样能设，但不持久化（域重载后需重开面板） |
| `LuaDeepProfilerSetting.isDeepLuaProfiler`（带 Save 的属性） | 深度采样开关持久化 | 直接写 `m_isDeepLuaProfiler` 字段 + `Save()` |
| `HeartBeatMsg.hookReady / captureActive` + `RegAction` | 真机 1Hz 状态心跳：Hook 是否就绪、是否在采样 | `RemoteStatusSupported=false`，面板按"TCP 已连接即就绪"兜底 |
| `LuaProfiler.SetRemoteCaptureActive(bool)` | 真机 Hook 平时休眠，只在 StartRecord~StopRecord 之间产 Sample；断线/停录恢复 Lua GC、清队列 | no-op：上游 Hook 一装上就持续采样并发送 |
| `HookLuaSetup.OpenRemoteProfiler()` | GM 菜单写"下次启动装 Hook"的一次性标记 | 直接写上游约定的标记文件 `persistentDataPath/LUAPROFILER_SERVER` |

## patches/ 里有什么

按文件给出的 `diff -u`（`a/` = 上游基线，`b/` = 源工程的修改版）。**注意**：源工程的 Miku 是在更早的上游版本上修改的 fork，所以 diff 里混有少量与 AI Profiler 无关的差异（Windows hook / 解析器等）。**不要整包 `git apply`**，按下面的改动点从各 diff 里择取 hunk 手工合入：

| 文件 | AI Profiler 相关改动点 |
|---|---|
| `LuaHookSetup.cs.diff` | ① `IsInitialized` / `IsDeepProfilerReady` 只读属性；② `OpenRemoteProfiler()`；③ 真机按一次性标记文件（`LuaProfiler.CheckServerIsOpen`）自动切远程+深度并装 Hook，`ConsumeServerOpenRequest` 消费标记；④ server 只 `BeginListen` 不阻塞等编辑器；⑤ Editor 未开面板（`ProfilerWinOpen=false`）时不装 Hook、`isInite` 如实为 false；⑥ `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` 重置静态态，兼容关闭 Domain Reload |
| `LuaProfiler.cs.diff` | `CheckServerIsOpen / OpenServer / CloseServer / ConsumeServerOpenRequest`；`SetRemoteCaptureActive` + `ShouldCaptureSample`（远程未激活时 Begin/EndSample 直接返回、恢复 Lua GC、清队列）；`SendProfilerStatus` 发 `HeartBeatMsg.Create(mainL就绪, 采样中)` 代替空 Sample 心跳；协程 LuaState 的采样丢弃 |
| `PKGHeartBeat.cs.diff` | `HeartBeatMsg` 增加 `hookReady / captureActive` 载荷、`Create / RegAction / UnRegAction`、Read/Write |
| `NetWorkMgr.cs.diff` / `NetWorkMgr.Server.cs.diff` | 发送队列硬限 `MAX_PENDING_COMMANDS=128`（未连接时到达即回收）、`ClearPendingCommands`、`_Close` 时清队列并 Dispose、发送线程逐条出队 |
| `NetWorkMgr.Client.cs.diff`（Editor） | 接收线程绑定到具体 TcpClient（重连不串线）、包头校验抛错、断线时正确复位 `_isConnected` |
| `LuaDeepProfilerSetting.cs.diff` | `ProfilerWinOpen` 改实例属性并持久化；`isDeepLuaProfiler` / `discardInvalid` 属性 |
| `Sample.cs.diff` | `CheckSampleValid`（可选的空样本丢弃），非必需 |

合入后重新编译，`MikuLuaProfilerBackend` 的反射探测会自动发现这些成员；面板真机页的 Lua 状态会从"按连接即就绪"变成真实的 Hook 心跳状态。

## 其他

- 真机 Lua 采样的 Development 包需要 Miku 自己的宏 `USE_LUA_PROFILER`（gate `HookLuaSetup`），与本工具的 `AI_PROFILER_DEVICE`（gate `DeviceFrameRecorder`）是两个宏，都要加。
- Miku 的二进制（`Editor/CECIL/Miku.Cecil.dll`、`Runtime/Plugins/Android/**` 的 ShadowHook）直接用上游的。
- Miku 端口默认 2333；面板 ADB 一键连接把它转发为 `tcp:2333`，与 `AIProfilerWindow.LUA_PROFILER_ADB_PORT` 一致。
