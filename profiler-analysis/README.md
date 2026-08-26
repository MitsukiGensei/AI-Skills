# profiler-analysis — Unity performance-sample analysis

**English** | [中文](README.zh-CN.md)

Analyzes Unity Profiler export data, locates hotspots across multiple dimensions, has the main Agent triage and parallel subagents dig out root causes, and produces a report with `file:line` fixes.

Besides the skill itself, this directory ships the **Unity-project-side sampling/export companion** (AI Profiler window, exporter, generic runtime capture, unattended menus, Lua backend abstraction with a Miku adapter, pure-Lua instrumentation adapter) — see [unity/README.md](unity/README.md). The skill handles analysis "after export"; `unity/` handles sampling "before export" — together they form the complete pipeline.

## What it does

Preprocesses the performance-sample text exported from Unity (`Assets/ProfilerLogs/*.txt`) into multi-dimensional hotspot rankings (CPU C#/Lua, GC Mono/Lua VM, high-frequency calls, GPU counters, memory trends, frame spikes, view opening, scene switching), then has the Agent read the corresponding project source and deliver root-cause analysis and optimization advice pinned to `file:line`. **The analysis phase is strictly read-only** — turning the report into code requires explicitly entering the implementation loop (see "Architecture and mechanics").

The script auto-detects two export formats: `AI-Profiler-v1` (multi-source merged) and the legacy pure-Lua sampling from the old Lua Profiler. By default it filters noise from the Lua backend / EditorLoop / editor-only instrumentation (your project's own instrumentation signatures go in `scripts/profiler_config.json`), recognizes the `[Target]` sampling topology (Editor local vs. device connection) and interprets by the matching calibration, and tags C# hotspots with `pattern=` labels that point to the corresponding chapter of the analysis playbook.

## When to use

- You want to know which C# / Lua function is expensive or called at high frequency (including the "cheap per call but hammered every frame" hidden hotspots)
- Tracking down GC allocation sources (Mono GC + the Lua VM GC that Unity cannot see), or judging whether memory is leaking / accumulating
- Locating frame-rate spike frames and their associated events (loading, instantiation, scene switches)
- Finding which view opens slowly / which click gets no response / opening-screen stutter / wasted nodes, and which scene switch is slow
- Verifying that an optimization actually landed (`--diff` against the pre-optimization sample)
- You just finished a sampling run and want an optimization report sorted into P0/P1/P2

## When not to use

- Per-pass / per-shader GPU bottlenecks → Unity FrameDebugger / on-device GPU profiler (this data has counter-level GPU only)
- Object-level memory-leak attribution for textures / meshes → Memory Profiler snapshots (this data only judges trends and allocation sources)
- Render Thread / Job thread hotspots → this skill walks only the Main Thread sample stream; "clean C# ranking" ≠ "CPU is fine"
- Build size, on-device crashes/hangs → their own dedicated skills

## Usage

### Install

Copy the skill body (`SKILL.md` + `scripts/` + `references/`) into the Claude Code skills directory. **Project-level install is recommended** — the script derives the project root from its own path (`<project root>/.claude/skills/profiler-analysis/scripts/`, 4 levels up), and the default `Assets/ProfilerLogs` resolves relative to that root:

```powershell
# Project level (recommended; default paths just work)
Copy-Item -Recurse profiler-analysis "<project root>\.claude\skills\profiler-analysis"

# User level also works, but every call must pass --dir / --src-root explicitly (or use absolute paths in profiler_config.json)
Copy-Item -Recurse profiler-analysis "$env:USERPROFILE\.claude\skills\profiler-analysis"
```

Then copy `scripts/profiler_config.example.json` to `scripts/profiler_config.json` and fill in, per project, the Lua source root, the noise signatures of your project's own instrumentation, and the framework dispatch entry points (all optional; built-in generic defaults apply when left empty).

The `unity/` subdirectory is the Unity-project-side companion and does not need to go into the skills directory; merge it into your project following [unity/README.md](unity/README.md) (when copying the skill you can bring it along or drop it — the skill runs either way).

### Triggering

Saying "analyze the latest profiler sample", "where is this sample slow", "where is GC high", "what's wrong with the latest sample" in conversation triggers the full pipeline; explicit `/profiler-analysis` works too.

### Script usage

```bash
# Default: analyze the latest export under Assets/ProfilerLogs, auto-detect format
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --top 25

# Other common options
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --file "Assets/ProfilerLogs/2026_05_25_15_00_00.txt"
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --list       # list analyzable files
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --json       # machine-readable output (includes health block, per-row noise tags)
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --raw        # disable instrumentation filtering, see the unfiltered picture
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --dir <ProfilerLogs> --src-root <Lua source root> --config <profiler_config.json>

# Baseline diff: verify an optimization / catch regressions between versions; hotspots normalized per frame, output regression and improvement rankings
python .claude/skills/profiler-analysis/scripts/analyze_profiler.py --file "<post-optimization export>" --diff "<pre-optimization export>"
```

The script depends only on the Python 3 standard library; on Windows both `py -3 <script>` and `python <script>` work.

## File layout

| File | Description |
|---|---|
| `SKILL.md` | Skill body: data sources and calibration, workflow (Memory pre-check → completeness gate → attribution guard → preprocessing → triage → fan-out deep dive → adversarial verification → summary), 12 classes of fix pitfalls, report quality red lines, code-implementation review gate |
| `scripts/analyze_profiler.py` | Deterministic preprocessing script (pure stdlib): parses both export formats, filters instrumentation noise, signal-to-noise health check, completeness diagnostics, role/pattern tags, bottleneck and power-proxy profiles, `--diff` baseline comparison, resolves real Lua source file paths |
| `scripts/profiler_config.example.json` | Project config sample: `log_dir` / `src_root` / noise signatures of the project's own instrumentation / Lua framework dispatch entry points (`role=framework-*`). Copy to `profiler_config.json` to activate |
| `references/perf-analysis-playbook.md` | Analysis playbook: five-module triage framework + "signal → primary hypothesis → qualification method" for memory / startup / scene switch / render CPU / UGUI / animation / physics / GPU / power proxy; the script's `pattern=` tags point directly to the corresponding chapter |
| `unity/` | Unity-project-side companion (sampling + export), see [unity/README.md](unity/README.md) |

## Architecture and mechanics

The skill is a multi-Agent pipeline of **preprocessing → triage → fan-out deep dive → adversarial verification → summary**, extended by an **implementation loop** only when the user explicitly asks for it. The script does only deterministic preprocessing ("lay the data out"); qualification and fixes are left to Agents. Each independent issue gets a **read-only `Plan`-type subagent** digging concurrently, and the main Agent integrates and ranks at the end. The two fan-outs are the core of this skill — instead of copying the rankings flat, issues are split by "one issue = one root cause" for parallel root-cause analysis, then an **independent perspective** tries to refute each one.

```
   Step 0  Memory pre-check
      │   search profiler/performance/sampling/module names/hotspot entry points, reuse prior experience; record even on no hit
      ▼
   Step 0.5 Data completeness gate
      │   detect walked 0, native segment failures, Lua NO DATA; critical missing dimensions get a grey light / resample
      ▼
   Step 0.6 Attribution guard
      │   role=framework-* entry points (event dispatcher / Scheduler / Timer / Update entry) serve only as call-chain evidence
      ▼
   Step 1  Script preprocessing (deterministic, pure stdlib)
      │   parse rankings · filter instrumentation noise · signal-to-noise health check · completeness diagnostics · role/pattern tags
      │   · independent bottleneck and power-proxy profiles · resolve real Lua source file paths
      ▼
   Step 2  Main Agent triage (lightweight locating only, no deep reading)
      │   rankings → N mutually independent issues (same-root-cause markers merged)
      │   per issue record: symptom/metric · entry file:line · call-stack clues · sample evidence snippet · initial severity estimate
      ▼
   Step 3  Fan-out: one read-only subagent per issue (dispatched concurrently in one message)
      ├─ Lua CPU root cause     ├─ Lua VM GC allocation source   ├─ business Mono GC allocation source
      ├─ frame-spike events     ├─ view opening over budget      └─ scene switch over budget
      │   (Plan type = no Edit/Write, read-only by construction)
      │   (GPU counters and memory trends get no subagent; the main Agent gives the trend verdict directly)
      ▼
   Step 3.5 Adversarial verification: each P0/P1 candidate that changes behavior or rests on a benefit assumption gets an independent skeptic (refute-first)
      │   attacks on-device benefit / feasibility & compliance / correctness
      │   → VERDICT_HOLDS · downgrade to not-worth · downgrade to owner-decision
      │   (proposer ≠ skeptic: an agent that both proposes and self-checks defends itself, so an independent view does the refuting)
      ▼
   Step 4  Main Agent summary
      │   dedupe · benefit-reality gate · rank P0/P1/P2 by "benefit × certainty" · output markdown report
      │
      └─ Step 3.6 (only when the user explicitly asks for implementation) ──────────────────┐
             cross-report dedupe → re-read HEAD, decide MODIFY/REJECT/RESAMPLE/DEFER           │
             → modifier Agent → independent Reviewer (refute-first) ↺ fix                      │
             → re-review until PASS → independent Checkout Agent creates a per-item pending CL │
             (REJECT/rollback also needs a clean re-review confirming 0 residue) ──────────────┘
```

### Capabilities it orchestrates

| Touchpoint | Initiated by | Description |
|---|---|---|
| `scripts/analyze_profiler.py` | the skill (Step 1) | Deterministic preprocessing; outputs META per-source status + signal-to-noise health check + per-dimension rankings + bottleneck profile + Lua source files to read |
| `Plan`-type subagent ×N | main Agent (Step 3) | One per independent issue; reads source in parallel to determine root cause and give a `file:line` fix |
| `Plan`-type skeptic ×M | main Agent (Step 3.5) | For P0/P1 candidates that change behavior or rest on a benefit assumption: independent refute-first adversarial verification (≠ proposer); level adjusted by verdict |
| Modifier / Reviewer / Checkout Agents | main Agent (Step 3.6) | Three separated roles in the implementation loop; the modifier never self-reviews; Checkout creates the CL independently and never auto-submits |
| [references/perf-analysis-playbook.md](references/perf-analysis-playbook.md) | consulted by triage and subagents | Analysis playbook; the script's `pattern=` tags point directly to the corresponding chapter |

### Decision contract (this pipeline's failure / stop rules)

- **Decide, don't hedge**: anything that can be determined from code gets a verdict + concrete fix; "consider caching this" / "needs careful evaluation" that throws the problem back at the user is forbidden.
- **Only three kinds of stop, each handled differently** (see SKILL.md §three kinds of stop): ① external contracts (network protocol / settlement / serialization / cross-platform formats / server) → give the fix + mark "needs confirmation from <who>"; ② internal hard constraints (patterns the project's CLAUDE.md explicitly forbids) → do not propose the violating fix; find a compliant alternative or rule it not implementable; ③ hard feasibility blockers (data/timing unavailable at the execution site, dependency on unreadable native semantics, requires a new C# binding) → rule it not implementable purely statically / purely in Lua, or needing a runtime probe.
- **Pass the benefit-reality gate before ranking P levels** (on-device benefit / cost window / recurrence / feasibility & compliance, see SKILL.md Step 4): locating a hotspot correctly does not mean it is worth changing — editor artifacts, frame-slicing during a non-interactive loading period, a one-time init ranked P0 as if it ran every frame — all must be downgraded.
- **Insufficient data must be stated**: a source reporting `NO DATA`, a sample that is too short, hotspots all sitting in framework internals → say so plainly and recommend a resample; never leave "could have checked but didn't" items.
- **Framework entry points take no blame**: event dispatchers / wrapper calls / scheduling components / Timer / UI base-class Update / PlayerLoop are only call-chain evidence; keep tracing to the concrete handler/component/business function. The project's Lua-side entry points go in `lua_framework_dispatchers` in `profiler_config.json`.
- **Advice must be actionable**: every P0/P1/P2 item must carry a sample evidence snippet, the call chain, the current degradation level, the expected post-optimization benefit and its bounds, risks, and a verification method.
- **Stops in the implementation loop**: `REJECT` / `RESAMPLE` / `DEFER` are legitimate terminal states; fixes that alter behavioral trajectories, introduce floating-point drift by swapping formulas, require prefab surgery, or were torn apart by adversarial verification are never mixed into a batch implementation (see SKILL.md "do-not-do gate").

## Where the data comes from (two export formats · two sampling modes)

The `AI Profiler` window (`Window/Analysis/AI Profiler`, source in [unity/](unity/README.md)) has a **sampling mode** switch at the top:

- **Editor local** (default): keep the window open first, then enter Play; StartRecord checks Play state and Lua backend Hook readiness and blocks recording if unmet. By default it enables Unity Deep + Lua deep sampling (if a backend exists), disables the project's own conflicting instrumentation (via the `AIProfilerCapture.DisableCompetingLuaProfiler` hook point), and checks **unlimited recording** — Unity frame segments are streamed to disk as `<project>/ProfilerLogs/raw/<timestamp>/seg_*.raw`, rolled by a triple gate of ~256MB / 16 frames in Deep / 600 frames non-Deep with explicit flush, and accumulated segment by segment on export, **breaking through Unity's native ~2000-frame limit** (`CleanRecord` removes these `.raw` segments). Any failure or empty segment is treated as critical / grey light.
- **Device connection (phone)**: the device runs a Development build with the `AI_PROFILER_DEVICE` define (plus the backend define for Lua), plugged into the PC via USB. When Lua is needed, trigger `AIProfilerDeviceControl.OpenLuaProfiler()` on the device (wired to the GM menu), fully quit and restart; the flag is consumed once. The Hook sleeps after startup; sampling opens only on `StartRecord`, and `StopRecord` / TCP disconnect / closing the window turns sampling off and clears the bounded send queue; Hook readiness is reported by a separate 1Hz status packet (requires the backend patches; otherwise "connected = ready"). For crash-prone scenes, uncheck "also capture Lua" before connecting to enter native safe mode, capturing only C#/GPU/memory/GC; META then reads `mikuDeep=False` and Lua NO DATA does not trigger a resample verdict. Device frames roll and pull in real time at 32MB / 600 frames per segment.

The export is produced by `AIProfilerExporter.cs` in multiple sections: `META` (including `[Target]` sampling topology + `[Health]` instrumentation self-overhead + per-source capture status), `FRAME_TIMELINE` (`TIMELINE` sequential samples + `TOP_CPU_FRAMES` whole-run spike ranking; the script dedupes and merges them), `CS_HOTSPOTS`, `LUA_HOTSPOTS` (including Lua VM GC), `GPU`, `MEMORY` (with headAvg/tailAvg/trend columns), `GC`, plus three **Editor-local-only** sections: `VIEW_STATS` (view opening time / opening-screen FPS and stutter / node usage), `SCENE_SWITCH` (total time from switch initiated → user-perceived "switch done", >3000ms marked over budget), `LUA_MEM_TREND` (periodic sampling of script VM total memory, for Lua-side leak detection) — these three are produced by the runtime capture `AIProfilerCapture`, and the project must instrument its UI / scene flows (projects with Lua bridge via `unity/Lua/AIProfilerCapture.lua`). Both export formats land in `Assets/ProfilerLogs/YYYY_MM_DD_HH_MM_SS.txt`.

The old "Lua Profiler" window's `Export For AI` (pure Lua aggregate tree, no Format header): the script keeps backward-compatible parsing for historical files.

## Prerequisites

| Dependency | Description |
|---|---|
| Unity "AI Profiler" window | `Window/Analysis/AI Profiler`, multi-source merged export; supports Editor local / device connection sampling modes. Source and merge instructions in [unity/README.md](unity/README.md) |
| Lua backend (optional) | Projects with Lua implement `ILuaProfilerBackend`; the reference implementation is the MikuLuaProfiler adapter (upstream <https://github.com/leinlin/Miku-LuaProfiler>; its source is not bundled in this repo, see [unity/Miku-LuaProfiler/README.md](unity/Miku-LuaProfiler/README.md)). Projects without Lua get NO DATA in the Lua dimensions |
| Device sampling (optional) | Device needs a Development build (with `AI_PROFILER_DEVICE`) + USB/ADB |
| `AI Profiler Dump Suspect Frames` menu (optional) | For diagnosing `BeginSample` leak contamination; the contaminated scene must first be reproduced with the Unity Profiler window's Record (Deep) |
| Python 3 | Runs the preprocessing script; pure standard library |

## What to configure per project

SKILL.md and the playbook grew out of one specific Unity + Lua mobile-game project; the concrete function names and counter-examples in the text are evidence from that project (already anonymized), while the methodology itself is generic. When switching projects, configure / be aware of:

- **`scripts/profiler_config.json`**: Lua source root (`src_root`), noise signatures of the project's own instrumentation (`noise_cs_substr` / `noise_lua_loc_substr`, etc.), Lua-side framework dispatch entry points (`lua_framework_dispatchers` — the script tags these `role=framework-*`, and SKILL.md's "attribution guard" uses them to avoid blaming framework files as root causes). Sample in `profiler_config.example.json`.
- **Project rule documents**: SKILL.md says in several places "if the project has scene-loading performance rules / Lua coding conventions / timer ordering rules / knowledge routing, follow them". If you have them, put them under the project's `.claude/rules/`; otherwise SKILL.md's own calibration applies.
- **Project hard constraints**: SKILL.md's "internal hard constraint" stop condition refers to patterns explicitly forbidden by the project's CLAUDE.md (e.g. `loadstring` banned, generated binding directories must not be edited); fill in per project.
- **Unity side**: defines, instrumentation points, Lua backend, and the hook point for disabling conflicting instrumentation are in [unity/README.md](unity/README.md).

## Maintenance surface: what to sync when something changes

- **Export format changes (`AIProfilerExporter.cs` changes sections / columns / META keys)** → sync the parsing logic in `scripts/analyze_profiler.py` (section names, column order, `[Target]`/`[Health]`/`mikuDeep=`/`(Lua VM` detection) + SKILL.md "Where the data comes from" + the same-named section in this README.
- **Capture line format changes (the `[ProfilerUtils][<Type>] ...` contract in `AIProfilerCapture.cs`)** → sync the script's VIEW_STATS / SCENE_SWITCH / LUA_MEM_TREND regexes and the view/scene comparison in `--diff`.
- **New script CLI flags / config keys** → sync the "Script usage" code block, `profiler_config.example.json`, and SKILL.md Step 1.
- **New script `pattern=` tags** → must have a corresponding "primary hypothesis + qualification method" chapter in the [playbook](references/perf-analysis-playbook.md), otherwise triage receives a tag with nowhere to look it up; also extend the pattern list in SKILL.md Step 2.
- **Where rules go** (high-churn; add new experience here, don't scatter it into this README): fix pitfalls go in SKILL.md §12 classes of fix-accuracy pitfalls; batch-implementation exclusion conditions go in the "do-not-do gate"; semantic / state / P4 hygiene requirements for implementation go in §code-implementation review gate for performance advice. Every addition should carry an evidence date and a counter-example (anonymized before entering the repo).
- **Unity-side script changes** → sync the file list and wiring points in [unity/README.md](unity/README.md); when a private method on the window is renamed, the reflection calls in `AIProfilerAutomation.cs` / `AIProfilerSkills.cs` fail explicitly — fix them together.
- **README changes** → keep `README.md` (English, default) and `README.zh-CN.md` (Chinese) in sync; this applies to every README in this directory tree.

## Known limitations

- Lua backend instrumentation inflates absolute timings; reports always use relative share / order-of-magnitude comparisons, never pseudo-precise ms; `calls` counts and GC bytes are relatively trustworthy
- The backend's own overhead on the C# ranking (`MikuLuaProfiler::*`, `EditorLoop`, etc.) is known measurement noise and is not filed as an issue; for a clean C# CPU picture, or to handle crash-prone scenes first, uncheck "also capture Lua" before device connection to use native safe mode
- In-Editor GPU is counter-level and per-marker data is unreliable; in device mode the GPU / GC calibration is relatively trustworthy
- `VIEW_STATS` / `SCENE_SWITCH` / `LUA_MEM_TREND` are produced only in Editor-local mode; device exports report NO DATA; they also depend on the project's instrumentation — no instrumentation means NO DATA
- `VIEW_STATS` click response only pairs "click → open view"; unresponsive pure-logic buttons cannot be measured — no slow record ≠ every click is smooth
- META `deepLuaNative=True` means the project's own conflicting Lua instrumentation was not disabled; disable it and resample
- When META reports "sample stream contamination", the failed segments are caused by broken `Begin/End` pairing during recording; eliminate the contamination before resampling (shrinking segment size / adding memory does not help)
- The Unity-side code was compile-verified with Roslyn against Unity 2022.3 reference assemblies (four configurations: with/without the Lua backend, Editor/Player) but not run-verified inside Unity; the window and exporter logic were migrated verbatim from the source project, with only the Lua bridge and backend coupling replaced
