# profiler-analysis / unity — Unity-project-side sampling and export companion

**English** | [中文](README.zh-CN.md)

This directory is the "before export" half of the `profiler-analysis` skill: one-click recording in the Unity Editor of the native Unity Profiler (C# CPU / GPU / memory / GC / frame timeline) + an optional Lua backend (Lua CPU + Lua VM GC) + a runtime capture (view opening / click response / opening-screen frame rate / node usage / scene switching / script VM memory), merged and exported as an `AI-Profiler-v1` multi-section text file for the skill's script to analyze.

**Genericity**: pure C#, not bound to any game framework; **projects without Lua can use it directly** (Lua dimensions report NO DATA). Lua plugs in through a backend abstraction, and Lua-side instrumentation is bridged through a pure-Lua adapter. Files mirror an `Assets/...` layout; merge by copying the whole directory into the target project (no `.meta` files included — Unity generates them).

## Contents

| Path | Assembly | Purpose |
|---|---|---|
| `Assets/AIProfiler/Runtime/AIProfilerCapture.cs` | Runtime | **Generic capture**. The project instruments its UI / scene flows with `MarkClick / MarkViewLoadStart / MarkViewResourceLoaded / MarkViewShown / BeginViewFpsWindow / ScheduleViewNodeStats / BeginSceneSwitch / EndSceneSwitch`, or directly `RecordViewOpen / RecordViewNodes / RecordSceneSwitch / RecordScriptMemory / RecordLine`. The window calls `BeginCapture()` on StartRecord and `EndCapture()` on StopRecord to retrieve the text → `VIEW_STATS / SCENE_SWITCH / LUA_MEM_TREND`. During Play a built-in hidden driver ticks every frame (FPS window) and samples memory periodically via `ScriptMemoryMBProvider`. Thresholds live in `AIProfilerCapture.Thresholds` |
| `Assets/AIProfiler/Runtime/LuaProfilerBackend.cs` | Runtime | **Lua backend abstraction** `ILuaProfilerBackend` + Null implementation + `LuaProfilerBackend.Current/Register`; `AIProfilerDeviceControl.OpenLuaProfiler()` device-side facade (wire it to the GM menu) |
| `Assets/AIProfiler/Runtime/Miku/MikuLuaProfilerBackend.cs` | Runtime (`#if AI_PROFILER_MIKU`) | MikuLuaProfiler adapter: built on the upstream public API, extension points probed via reflection. See [Miku-LuaProfiler/README.md](Miku-LuaProfiler/README.md) |
| `Assets/AIProfiler/Runtime/DeviceFrameRecorder.cs` | Runtime (`#if UNITY_EDITOR \|\| AI_PROFILER_DEVICE`) | **On-device unlimited frame recorder**: the device writes Profiler binary logs in segments gated by 600 frames / 32MB to `persistentDataPath/ai_profiler_frames`, publishing `.ready` when a segment closes; the Editor sends start/stop through `persistentDataPath/ai_profiler_control/command.txt` and reads back `state.txt`. The Lua sampling switch is delegated to the backend (may be absent) |
| `Assets/AIProfiler/Editor/AIProfilerWindow.cs` | Editor | **AI Profiler window** `Window/Analysis/AI Profiler`: Editor local / device connection modes; opening it automatically enables Unity Deep Profile (+ Lua deep sampling, if a backend exists) and calls `AIProfilerCapture.DisableCompetingLuaProfiler` to disable the project's own conflicting instrumentation; unlimited recording (segmented binary logs on disk at `<project>/ProfilerLogs/raw/<timestamp>/seg_*.raw`, triple gate of ~256MB / 16 frames in Deep / 600 frames non-Deep); sample-stream contamination monitor during recording; one-click ADB connection (port forwarding for Unity 34999 + Lua 2333) and background pull of device segments. Also provides the menu `Window/Analysis/AI Profiler Dump Suspect Frames` (locates early-return `BeginSample` leaks) |
| `Assets/AIProfiler/Editor/AIProfilerExporter.cs` | Editor | **Unified exporter**: accumulates segments via `ProfilerDriver.LoadProfile`, aggregates C# hotspots by linearly scanning the main-thread raw sample stream with `RawFrameDataView` (no per-frame tree building); merges Lua aggregates, `ProfilerRecorder` memory/render counters (with head/tail window trends), `FrameTimingManager` GPU frame time, and runtime capture text; writes `Assets/ProfilerLogs/yyyy_MM_dd_HH_mm_ss.txt`. Noise signatures of the project's own instrumentation can be appended to `ExtraInstrumentCsMarkers / ExtraInstrumentLuaLocations` |
| `Assets/AIProfiler/Editor/AIProfilerAutomation.cs` | Editor | **Unattended driver**: exposes the window's private StartRecord / StopRecord / ExportForAI / CleanRecord and the Deep / Lua switches as parameterless `Tools/AI Profiler Auto/*` MenuItems, for remote menu-execution tools doing batch capture |
| `Assets/AIProfiler/Editor/Integrations/AIProfilerSkills.cs` | Editor (optional) | unity-skills (MCP) extension: `aiprofiler_connect_adb / start / stop / export / status`, letting an Agent run A/B sampling fully automatically. Depends on unity-skills' `[UnitySkill]` attribute; leave it out if you don't use unity-skills |
| `Lua/AIProfilerCapture.lua` | Lua (optional) | **Pure-Lua adapter**, no framework dependencies (no class / custom events / timers): bridges Lua-side instrumentation to the C# capture and periodically reports `collectgarbage("count")`; includes the pcall guard `installErrWatch` (reports swallowed lua errors — an error unwind breaks the native Profiler's Begin/End pairing and is a sample-stream contamination suspect) |
| `Miku-LuaProfiler/` | — | Upstream link, adapter design, reference patches for the on-device pipeline extension points (Miku source not included) |

## Defines

| Define | Where | Purpose |
|---|---|---|
| `AI_PROFILER_DEVICE` | On-device Development build | Compiles in `DeviceFrameRecorder` (unlimited on-device Unity frame capture). Not for release builds |
| `AI_PROFILER_MIKU` | Projects using Miku (Editor + device build) | Enables `MikuLuaProfilerBackend` |
| `USE_LUA_PROFILER` | Device builds using Miku | Miku's own define, gates its `HookLuaSetup` |

## Integration steps

1. Copy `Assets/AIProfiler/` into the project. Projects without Lua are done here — open `Window/Analysis/AI Profiler` to record and export (Lua sections report NO DATA).
2. **Instrumentation** (to get data in VIEW_STATS / SCENE_SWITCH): call `AIProfilerCapture.Mark*/Begin*/End*` in the UI framework's "click → start loading → resources loaded → shown" flow and in the scene-switch flow; the calls are no-ops outside capture periods, so they can stay in permanently. C# projects call directly; Lua projects use `Lua/AIProfilerCapture.lua`:
   ```lua
   local Capture = require("AIProfilerCapture")
   Capture.init({ cs = CS.AIProfiler.AIProfilerCapture })   -- ToLua-generated binding class
   -- every frame Capture.update(dt); in UI/scene flows Capture.markClick / markViewLoadStart / ... / endSceneSwitch
   ```
3. **Script VM memory trend** (LUA_MEM_TREND): on the C# side set `AIProfilerCapture.ScriptMemoryMBProvider = () => ...`, or let the Lua adapter's per-frame `update` report periodically on its own.
4. **The project's own conflicting instrumentation** (e.g. an engine-native Deep Lua): set `AIProfilerCapture.DisableCompetingLuaProfiler = () => {...}` and `IsCompetingLuaProfilerActive = () => ...` (written to META as `deepLuaNative`). Leave unset if there is none.
5. **Lua backend**: see [Miku-LuaProfiler/README.md](Miku-LuaProfiler/README.md).
6. **Device**: build with `AI_PROFILER_DEVICE` (add `AI_PROFILER_MIKU` + `USE_LUA_PROFILER` for Lua); wire `AIProfilerDeviceControl.OpenLuaProfiler()` to the GM menu (returns false = the current build has no Lua backend); after the device authorizes adb over USB, switch the window to "Device connection" → `ADB one-click connect`.
7. Configure the noise signatures of the project's own instrumentation in both the exporter (`AIProfilerExporter.ExtraInstrumentCsMarkers` etc., appended at startup) and the analysis script (`scripts/profiler_config.json`) so both sides agree.

## Quick workflow reference

- **Editor local**: open the window (keep it open) → enter Play (with a Lua backend, confirm its Hook is installed) → `StartRecord` → interact → `StopRecord` → `ExportForAI` → the skill script analyzes `Assets/ProfilerLogs/<latest>.txt`. `CleanRecord` clears `ProfilerLogs/raw`.
- **Device**: Development build → (when Lua is needed) trigger `OpenLuaProfiler()` on the device → full restart → connect via USB → window "Device connection" → `ADB one-click connect` → same as above. For crash-prone scenes, uncheck "also capture Lua" first to use native safe mode.
- **Batch / unattended**: the `Tools/AI Profiler Auto/*` menus or unity-skills' `aiprofiler_*`; the Deep / Lua switches must be changed outside Play (they trigger recompilation / take effect on the next Play).

## Verified

Four configurations compile with Roslyn against the Unity 2022.3.62f2 reference assemblies: Editor without Lua, Editor + upstream Miku source (`AI_PROFILER_MIKU`), Player `AI_PROFILER_DEVICE`, Player + Miku. Not run-verified inside Unity — the window and exporter logic were migrated verbatim from the source project (only the Lua bridge and backend coupling were replaced), so runtime behavior follows the source project.

## Known constraints

- Export runs synchronously on the main thread; the Editor being unresponsive meanwhile is normal; it can be cancelled at any time and the frames parsed so far are still exported
- The ~2000-frame limit per `ProfilerDriver.LoadProfile` call is a hard Unity limit; unlimited recording works around it by segmenting; any segment load failure / walked 0 means that time range is unrecoverable, and the script marks it critical
- If the Console spams `Missing Profiler.EndSample` during recording, the current segment is already contaminated (deserialization will fail); eliminate the contamination source before resampling; the `Dump Suspect Frames` menu names the leaking `BeginSample`
- `AppDomain.FirstChanceException` is not dispatched on Unity 2022.3 Mono; swallowed script-layer exceptions can only be caught by script-side guards (the Lua adapter's `installErrWatch`)
- The line-format contract (`[ProfilerUtils][<Type>] <label> [<subject>] - ...`, META keys such as `mikuDeep=` / `hookReady=`) follows v1; the analysis script parses it accordingly, so changes must be synced on both sides
