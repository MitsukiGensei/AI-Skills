# MikuLuaProfiler integration notes (third-party, not distributed with this repo)

**English** | [中文](README.zh-CN.md)

The Lua dimensions of AI Profiler (Lua CPU / Lua VM GC / on-device Lua sampling) use **MikuLuaProfiler** as the reference backend by default. It is a third-party open-source library and **its source is not bundled in this repo**:

- Upstream repository: <https://github.com/leinlin/Miku-LuaProfiler> (MIT)
- The upstream baseline that `patches/` in this directory is based on: [patches/UPSTREAM_BASE.txt](patches/UPSTREAM_BASE.txt)

## Do you need it

| Project situation | What to do |
|---|---|
| No Lua | Nothing. `LuaProfilerBackend.Current` is the Null implementation; LUA_HOTSPOTS exports as NO DATA |
| Lua, using Miku | Install upstream Miku → add the Scripting Define `AI_PROFILER_MIKU` in Player Settings → `Assets/AIProfiler/Runtime/Miku/MikuLuaProfilerBackend.cs` takes effect automatically (Editor-local sampling works as is). For unlimited on-device sampling, also apply the patches below |
| Lua, using another profiler | Implement `ILuaProfilerBackend` (`Assets/AIProfiler/Runtime/LuaProfilerBackend.cs`) and call `LuaProfilerBackend.Register(instance)` at startup |

## Adapter design: depends only on the upstream public API, probes extension points via reflection

At compile time `MikuLuaProfilerBackend` uses only members that already exist upstream: `LuaDeepProfilerSetting.Instance` (`isLocal / isRecord / isStartRecord / ip / port / m_isDeepLuaProfiler`), `LuaProfiler.RegisterOnReceiveSample / UnRegistReceive / mainL`, `Sample.RegAction / UnRegAction` and the `Sample` fields, the `HeartBeatMsg` type, `NetWorkMgrClient.Connect / Disconnect / GetIsConnect` (Editor assembly, called via reflection).

A few capabilities the AI Profiler on-device pipeline needs do not exist upstream and **cannot be added from the outside via override / extension methods** (they are all class-internal behavior: Hook install timing, sampling sleep, heartbeat payload, send queue). The adapter probes these members via reflection — **enabled when patched, degraded when not**:

| Extension point | Purpose | Behavior without the patch |
|---|---|---|
| `HookLuaSetup.IsInitialized` / `IsDeepProfilerReady` | Verifies Hook and Lua VM readiness before Editor-local StartRecord | Falls back to `LuaProfiler.mainL != IntPtr.Zero` |
| `LuaDeepProfilerSetting.ProfilerWinOpen` (instance property, persisted) | Decides on entering Play whether to install the Hook based on "is the window open" | Upstream has a `static bool`, which can be set the same way but is not persisted (the window must be reopened after a domain reload) |
| `LuaDeepProfilerSetting.isDeepLuaProfiler` (property with Save) | Persists the deep-sampling switch | Writes the `m_isDeepLuaProfiler` field directly + `Save()` |
| `HeartBeatMsg.hookReady / captureActive` + `RegAction` | On-device 1Hz status heartbeat: Hook ready, sampling active | `RemoteStatusSupported=false`; the window falls back to "TCP connected = ready" |
| `LuaProfiler.SetRemoteCaptureActive(bool)` | On-device Hook sleeps normally and produces Samples only between StartRecord and StopRecord; restores Lua GC and clears the queue on disconnect / stop | no-op: the upstream Hook samples and sends continuously once installed |
| `HookLuaSetup.OpenRemoteProfiler()` | The GM menu writes a one-shot "install the Hook on next launch" flag | Writes the upstream-convention flag file `persistentDataPath/LUAPROFILER_SERVER` directly |

## What is in patches/

Per-file `diff -u` (`a/` = upstream baseline, `b/` = the source project's modified version). **Note**: the source project's Miku is a fork modified on top of an older upstream version, so the diffs contain a few differences unrelated to AI Profiler (Windows hook / parser, etc.). **Do not `git apply` them wholesale**; pick the hunks for the change points below out of each diff and merge them by hand:

| File | AI-Profiler-related change points |
|---|---|
| `LuaHookSetup.cs.diff` | ① `IsInitialized` / `IsDeepProfilerReady` read-only properties; ② `OpenRemoteProfiler()`; ③ on device, switch to remote + deep and install the Hook automatically based on the one-shot flag file (`LuaProfiler.CheckServerIsOpen`), `ConsumeServerOpenRequest` consumes the flag; ④ the server only `BeginListen`s without blocking for the editor; ⑤ in the Editor, do not install the Hook when the window is not open (`ProfilerWinOpen=false`) and report `isInite` as false truthfully; ⑥ `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` resets static state, compatible with Domain Reload disabled |
| `LuaProfiler.cs.diff` | `CheckServerIsOpen / OpenServer / CloseServer / ConsumeServerOpenRequest`; `SetRemoteCaptureActive` + `ShouldCaptureSample` (Begin/EndSample return immediately when remote capture is inactive, restore Lua GC, clear the queue); `SendProfilerStatus` sends `HeartBeatMsg.Create(mainL ready, sampling)` instead of an empty-Sample heartbeat; samples from coroutine LuaStates are dropped |
| `PKGHeartBeat.cs.diff` | `HeartBeatMsg` gains the `hookReady / captureActive` payload, `Create / RegAction / UnRegAction`, Read/Write |
| `NetWorkMgr.cs.diff` / `NetWorkMgr.Server.cs.diff` | Hard send-queue limit `MAX_PENDING_COMMANDS=128` (recycled on arrival when not connected), `ClearPendingCommands`, clear the queue and Dispose on `_Close`, the send thread dequeues one at a time |
| `NetWorkMgr.Client.cs.diff` (Editor) | Receive thread bound to a specific TcpClient (no cross-talk on reconnect), packet header validation throws, `_isConnected` reset correctly on disconnect |
| `LuaDeepProfilerSetting.cs.diff` | `ProfilerWinOpen` becomes an instance property and is persisted; `isDeepLuaProfiler` / `discardInvalid` properties |
| `Sample.cs.diff` | `CheckSampleValid` (optional empty-sample discarding), not required |

After merging and recompiling, `MikuLuaProfilerBackend`'s reflection probing discovers these members automatically; the Lua status on the window's device page changes from "connected = ready" to the real Hook heartbeat status.

## Other notes

- A Development build for on-device Lua sampling needs Miku's own define `USE_LUA_PROFILER` (gates `HookLuaSetup`); together with this tool's `AI_PROFILER_DEVICE` (gates `DeviceFrameRecorder`) that is two defines — add both.
- Miku's binaries (`Editor/CECIL/Miku.Cecil.dll`, the ShadowHook under `Runtime/Plugins/Android/**`) are used straight from upstream.
- Miku's default port is 2333; the window's ADB one-click connect forwards it as `tcp:2333`, matching `AIProfilerWindow.LUA_PROFILER_ADB_PORT`.
