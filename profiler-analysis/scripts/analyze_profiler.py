#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
analyze_profiler.py — AI Profiler 导出文本（AI-Profiler-v1 / 旧 Lua-only）前处理器

职责（确定性、纯 stdlib）：
  1. 定位待分析文件：未指定 --file 时取 ProfilerLogs 中时间最新的 .txt
  2. 解析导出文件的 SECTION 1（热点函数表）
  3. 按 自身耗时 / 总耗时 / Lua GC / Mono GC / 调用次数 多维度排出 Top-N
  4. 收集热点涉及的 Lua 源文件，best-effort 解析到 Lua 源码根（--src-root / 配置 src_root）下的真实路径
       —— 供上层 Agent 直接打开源码做定性分析

它只做"把数据摆好"，不做优化判断。耗时归因 / GC 归因 / 改法建议由 SKILL.md
驱动的 Agent 阅读源码后给出。

用法：
  python analyze_profiler.py                      # 分析最新文件
  python analyze_profiler.py --file <path>        # 分析指定文件
  python analyze_profiler.py --top 30             # 每个维度 Top-30
  python analyze_profiler.py --dir <ProfilerLogs> # 自定义目录
  python analyze_profiler.py --list               # 仅列出可分析的文件
  python analyze_profiler.py --json               # 机器可读 JSON 输出
  python analyze_profiler.py --config <json>      # 项目配置（噪声特征 / 框架分发入口 / 默认目录），默认读脚本旁的 profiler_config.json
"""

import argparse
import glob
import json
import os
import re
import sys

# Windows 控制台默认 codepage 可能不是 UTF-8，强制 stdout/stderr 用 UTF-8，避免中文 mojibake
for _stream in ("stdout", "stderr"):
    try:
        getattr(sys, _stream).reconfigure(encoding="utf-8")
    except Exception:
        pass

# ---- 项目根定位：脚本按项目级安装在 <root>/.claude/skills/profiler-analysis/scripts/ 下 ----
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "..", "..", ".."))
DEFAULT_LOG_DIR = os.path.join(PROJECT_ROOT, "Assets", "ProfilerLogs")
DEFAULT_SRC_ROOT = os.path.join(PROJECT_ROOT, "Assets", "Lua")   # Lua 源码根：按项目用 --src-root 或配置 src_root 覆盖
DEFAULT_CONFIG = os.path.join(SCRIPT_DIR, "profiler_config.json")


def load_config(path):
    """读取项目配置（可选）。所有键都是可选的，缺省即用脚本内置的通用默认值。
    键：log_dir / src_root / noise_cs_substr / noise_cs_exact / noise_lua_loc_substr / noise_lua_name_substr /
        editor_only_substr / noise_gc_substr / lua_framework_dispatchers([[loc_substr, name_substr, role], ...]) /
        cs_framework_dispatcher_substr。样例见 profiler_config.example.json。"""
    if not path or not os.path.isfile(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        return cfg if isinstance(cfg, dict) else {}
    except Exception as e:  # 配置坏了不该让分析跑不起来
        print("[警告] 项目配置读取失败，使用内置默认: %s (%s)" % (path, e), file=sys.stderr)
        return {}


def apply_config(cfg):
    """把配置里的项目特征合并进内置默认表（追加，不替换通用项）。"""
    global _NOISE_CS_SUBSTR, _NOISE_CS_EXACT, _NOISE_LUA_LOC_SUBSTR, _NOISE_LUA_NAME_SUBSTR
    global _EDITOR_ONLY_SUBSTR, _NOISE_GC_SUBSTR, _LUA_FRAMEWORK_DISPATCHERS, _CS_FRAMEWORK_DISPATCHER_SUBSTR

    def merge(base, key):
        extra = cfg.get(key) or ()
        return tuple(base) + tuple(str(x).lower() for x in extra if isinstance(x, str))

    _NOISE_CS_SUBSTR = merge(_NOISE_CS_SUBSTR, "noise_cs_substr")
    _NOISE_CS_EXACT = merge(_NOISE_CS_EXACT, "noise_cs_exact")
    _NOISE_LUA_LOC_SUBSTR = merge(_NOISE_LUA_LOC_SUBSTR, "noise_lua_loc_substr")
    _NOISE_LUA_NAME_SUBSTR = merge(_NOISE_LUA_NAME_SUBSTR, "noise_lua_name_substr")
    _EDITOR_ONLY_SUBSTR = merge(_EDITOR_ONLY_SUBSTR, "editor_only_substr")
    _NOISE_GC_SUBSTR = merge(_NOISE_GC_SUBSTR, "noise_gc_substr")
    _CS_FRAMEWORK_DISPATCHER_SUBSTR = merge(_CS_FRAMEWORK_DISPATCHER_SUBSTR, "cs_framework_dispatcher_substr")
    disp = []
    for item in cfg.get("lua_framework_dispatchers") or ():
        if isinstance(item, (list, tuple)) and len(item) == 3:
            disp.append((str(item[0]).lower(), str(item[1]).lower(), str(item[2])))
    _LUA_FRAMEWORK_DISPATCHERS = tuple(_LUA_FRAMEWORK_DISPATCHERS) + tuple(disp)


class Record:
    __slots__ = ("rank", "self_ms", "total_ms", "calls",
                 "self_lua_gc", "self_mono_gc", "is_lua", "location", "name")

    def __init__(self, rank, self_ms, total_ms, calls,
                 self_lua_gc, self_mono_gc, is_lua, location, name):
        self.rank = rank
        self.self_ms = self_ms
        self.total_ms = total_ms
        self.calls = calls
        self.self_lua_gc = self_lua_gc
        self.self_mono_gc = self_mono_gc
        self.is_lua = is_lua
        self.location = location
        self.name = name


def find_latest_file(log_dir):
    files = glob.glob(os.path.join(log_dir, "*.txt"))
    if not files:
        return None
    # 文件名是 YYYY_MM_DD_HH_MM_SS.txt，字典序==时间序；再用 mtime 兜底
    files.sort(key=lambda p: (os.path.basename(p), os.path.getmtime(p)))
    return files[-1]


def list_files(log_dir):
    files = glob.glob(os.path.join(log_dir, "*.txt"))
    files.sort(key=lambda p: os.path.getmtime(p))
    return files


def _to_float(s, default=0.0):
    try:
        return float(s)
    except (ValueError, TypeError):
        return default


def _to_int(s, default=0):
    try:
        return int(s)
    except (ValueError, TypeError):
        return default


def parse_export(path):
    """解析导出文件，返回 (meta:dict, records:list[Record])。"""
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()

    meta = {
        "file": path,
        "export_time": "",
        "root_count": 0,
        "node_count": 0,
        "unique_func": 0,
        "omitted": 0,
    }
    records = []
    in_section1 = False

    for line in lines:
        stripped = line.strip()

        # ---- header 元信息 ----
        if stripped.startswith("Export Time"):
            meta["export_time"] = stripped.split(":", 1)[1].strip() if ":" in stripped else ""
            continue
        if stripped.startswith("Root Count"):
            meta["root_count"] = _to_int(stripped.split(":", 1)[1].strip())
            continue
        if stripped.startswith("Node Count"):
            meta["node_count"] = _to_int(stripped.split(":", 1)[1].strip())
            continue
        if stripped.startswith("Unique Func"):
            meta["unique_func"] = _to_int(stripped.split(":", 1)[1].strip())
            continue

        # ---- SECTION 1 边界 ----
        if "SECTION 1" in stripped:
            in_section1 = True
            continue
        if "SECTION 2" in stripped:
            in_section1 = False
            continue
        if not in_section1:
            continue

        if stripped.startswith("#") or stripped.startswith("=") or not stripped:
            continue
        if stripped.startswith("..."):
            # "... (N more functions omitted)"
            digits = "".join(ch for ch in stripped if ch.isdigit())
            meta["omitted"] = _to_int(digits)
            continue

        # ---- 数据行：rank | self | total | calls | luaGC | monoGC | isLua | location | name ----
        parts = stripped.split(" | ", 8)
        if len(parts) < 9:
            continue
        records.append(Record(
            rank=_to_int(parts[0]),
            self_ms=_to_float(parts[1]),
            total_ms=_to_float(parts[2]),
            calls=_to_int(parts[3]),
            self_lua_gc=_to_int(parts[4]),
            self_mono_gc=_to_int(parts[5]),
            is_lua=(parts[6] == "1"),
            location=parts[7].strip(),
            name=parts[8].strip(),
        ))

    return meta, records


def human_bytes(n):
    n = float(n)
    for unit in ("B", "KB", "MB", "GB"):
        if abs(n) < 1024.0 or unit == "GB":
            return ("%.0f%s" % (n, unit)) if unit == "B" else ("%.2f%s" % (n, unit))
        n /= 1024.0
    return "%.2fGB" % n


def parse_human_bytes(s):
    """'221.50MB' -> 字节数(float)；解析失败返回 0。供信噪比体检从 GC 段人类可读串反推。"""
    m = re.match(r"^\s*(-?[\d.]+)\s*([KMGT]?B)\s*$", s or "", re.IGNORECASE)
    if not m:
        return 0.0
    val = float(m.group(1))
    mult = {"B": 1, "KB": 1024, "MB": 1024 ** 2,
            "GB": 1024 ** 3, "TB": 1024 ** 4}.get(m.group(2).upper(), 1)
    return val * mult


# ----------------------------------------------------------------------
# 插桩 / 编辑器噪声分类
# ----------------------------------------------------------------------
# AI Profiler 同开 Unity Deep + 项目 deep-lua + Miku，三套插桩会互相计量，
# "测量工具自身"与"仅编辑器存在"的条目常占据 Top 榜首、淹没真实业务热点。
# 这里做确定性分类，用途有三：
#   ① 默认给出【过滤后】视图（真实 C#/引擎/Lua 热点），原始全貌用 --raw 看
#   ② 信噪比体检：估算插桩/编辑器开销占比，过高就提示重采更干净数据
#   ③ "待阅读源文件"不再把工具自身（Profiler.lua 等）列进去
# 分类只降权/分组，不删除任何原始数据。新增噪声特征往这几个元组里加即可。

# C# / 引擎 marker
# 内置只放通用项（Lua 后端自身 / 编辑器）；工程自带插桩的特征放 profiler_config.json（见 profiler_config.example.json）。
_NOISE_CS_SUBSTR = (
    "mikuluaprofiler",                # Miku 运行时（大小写两种写法都覆盖）
    "aiprofilercapture",              # 本工具的运行时采集器自身
)
_NOISE_CS_EXACT = ("editorloop",)     # 编辑器主循环，非业务/引擎运行时

# Lua（后端 location / name）
_NOISE_LUA_LOC_SUBSTR = (
    "tolua/misc/misc",                # Miku reimport / 重新词法分析注桩位置
)
_NOISE_LUA_NAME_SUBSTR = ("reimport",)

# Mono GC.Alloc 路径里"仅编辑器存在、真机没有"的分配源——勿照此优化
_EDITOR_ONLY_SUBSTR = (
    "unityeditor", "editorbuildrules", "scriptcompilation",
    "assetpath.", "fileutil.getphysicalpath", "combinedefinearrays",
)
# Mono GC.Alloc 路径里明确属于 Lua 插桩自身的分配源
_NOISE_GC_SUBSTR = (
    "mikuluaprofiler", "::llex", ".llex", "lual_loadbufferx",
)

# 框架分发/包装入口：这些条目可以作为“调用链经过这里”的证据，
# 但不能把性能问题直接归责到这些框架文件。真正根因要继续追 handler / 组件 / 定时任务。
# Lua 侧入口与项目框架强相关，内置为空，由 profiler_config.json 的 lua_framework_dispatchers 提供
# （每项 [location 子串, 函数名子串, role 标签]，role 以 framework-dispatcher: / framework-wrapper: 开头）。
_LUA_FRAMEWORK_DISPATCHERS = ()

_CS_FRAMEWORK_DISPATCHER_SUBSTR = (
    "playerloop",
    "scriptrunbehaviourupdate",
    "behaviourupdate",
    "updatebeat",
    "lateupdatebeat",
    "fixedupdatebeat",
)

# 特征 marker → 已知性能模式提示（性能分析方法论沉淀，
# 详见 references/perf-analysis-playbook.md，§号指向该文档章节）。
# 只做提示、不降权不过滤；triage 时把命中模式当作「优先假设」而非结论。
_CS_PATTERN_HINTS = (
    ("shader.creategpuprogram", "shader-compile",
     "shader 变体现场编译卡主线程——变体未被预热/收集，补收集 SVC 而非改 pragma（§二 切换三件套1）"),
    ("semaphore.waitforsignal", "passive-wait",
     "被动等待（等加载线程/GPU present），随主因缓解而降，不单独立项（§二）"),
    ("gfx.waitforpresent", "gpu-bound-wait",
     "主线程等 GPU 出帧——渲染/GPU 侧是瓶颈（§三/§四）"),
    ("waitfortargetfps", "idle-wait",
     "空转等帧；若落在 loading 窗口 = 异步加载权限过低信号（backgroundLoadingPriority，§二 loading）"),
    ("canvas.sendwillrendercanvases", "ugui-vertex",
     "UGUI Vertex 更新大户（换图/Text 内容/透明度）；找每帧变内容的大 Vertex 元素（§三 UGUI 三类变化表）"),
    ("synctransform", "ugui-transform",
     "有 UI 每帧动 Transform/显隐；自身便宜但 calls 高会顶起父节点，查世界空间 UI 的每帧缩放/位移（§三）"),
    ("canvas.buildbatch", "ugui-rebatch",
     "UI 动静分离差导致重合批；通常伴随 Vertex/Transform 更新（§三 UGUI 三类变化表）"),
    ("meshskinning", "skinning-pressure",
     "蒙皮压渲染线程——渲染线程高压 ≠ DC 多；清屏对照 + 开关 CPU/GPU Skinning 验证（§三 渲染线程）"),
    ("physics.", "physics-idle-sim",
     "有 Static Collider 即常驻 3D 物理模拟；若无刚体/回调需求可改 SimulationMode=Script（§三 物理）"),
    ("addressablesbehaviour.ondestroy", "teardown-storm",
     "拆旧销毁风暴（逐对象 Destroy + Addressables Release），看 calls 规模（§二 切换三件套3）"),
    ("coroutinesdelayedcalls", "teardown-storm",
     "拆旧销毁风暴（切场景集中 Destroy），看 calls 规模（§二 切换三件套3）"),
    ("loadassetasync", "load-burst",
     "资源加载集中单帧；目标是按帧预算摊平尖峰而非消除总量（§二 切换三件套2）"),
    ("refchunk", "load-burst",
     "地形/FOD chunk 引用加载集中单帧；按帧预算分摊（§二 切换三件套2）"),
    ("chunkterrain", "load-burst",
     "地形 chunk 流式加载/卸载；关注是否集中单帧（§二 切换三件套2）"),
    ("animators.", "animator-cull",
     "动画更新吃主线程+Job 线程；查 Culling Mode（Always Animate→CullUpdateTransforms）与激活 Animator 数量（§三 动画）"),
    ("animator.", "animator-cull",
     "动画更新吃主线程+Job 线程；查 Culling Mode（Always Animate→CullUpdateTransforms）与激活 Animator 数量（§三 动画）"),
)


def classify_cs_pattern(name):
    """特征 marker 命中已知模式时返回 (pattern_id, hint)，未命中返回 (None, "")。"""
    low = (name or "").lower().replace(" ", "")
    for s, pid, hint in _CS_PATTERN_HINTS:
        if s in low:
            return pid, hint
    return None, ""


# 稳态常驻浪费模式（每帧白烧 = 功耗代理风险）与切换/加载类模式，供瓶颈画像分组
_RESIDENT_WASTE_PATTERNS = frozenset((
    "physics-idle-sim", "animator-cull", "skinning-pressure",
    "ugui-vertex", "ugui-transform", "ugui-rebatch",
))
_TRANSITION_PATTERNS = frozenset(("shader-compile", "load-burst", "teardown-storm"))

# 低端机同屏三角形预算（个），来源 playbook §四
_TRI_BUDGET_LOW_END = 150000


def _gpu_counter_vals(data, prefix):
    """从 GPU 原样行解析数值列（min | avg | max | last，能解析几个算几个）。"""
    plow = prefix.lower()
    for s in data.get("gpu", ()):
        if s.strip().lower().startswith(plow):
            vals = []
            for p in s.split("|")[1:]:
                try:
                    vals.append(float(p.strip()))
                except ValueError:
                    pass
            return vals
    return []


def compute_bottleneck_profile(data):
    """独立瓶颈 / 功耗代理画像：只用本采样数据做启发式判断，不依赖任何外部工具。
    规则口径见 references/perf-analysis-playbook.md §〇/§五。返回 {signals, judgments}。"""
    cs = [r for r in (data.get("cs_hotspots") or ()) if classify_cs(r.get("marker")) == "signal"]

    def self_ms(sub):
        return sum(r.get("selfMs", 0.0) for r in cs
                   if sub in (r.get("marker") or "").lower().replace(" ", ""))

    wait_target = self_ms("waitfortargetfps")
    wait_present = self_ms("gfx.waitforpresent")
    wait_sema = self_ms("semaphore.waitforsignal")

    gpu_ft = _gpu_counter_vals(data, "gpuframetime")     # [min, avg, max]
    cpu_ft = _gpu_counter_vals(data, "cpuframetime")     # [avg]
    tri = _gpu_counter_vals(data, "triangles count")     # [min, avg, max, last]
    dc = _gpu_counter_vals(data, "draw calls count")

    patterns = set()
    for r in cs:
        pid, _ = classify_cs_pattern(r.get("marker"))
        if pid:
            patterns.add(pid)

    signals = {
        "waitTargetFpsSelfMs": round(wait_target, 1),
        "gfxWaitForPresentSelfMs": round(wait_present, 1),
        "semaphoreWaitSelfMs": round(wait_sema, 1),
        "gpuFrameMsAvg": gpu_ft[1] if len(gpu_ft) > 1 else None,
        "gpuFrameMsMax": gpu_ft[2] if len(gpu_ft) > 2 else None,
        "cpuFrameMsAvg": cpu_ft[0] if cpu_ft else None,
        "triAvg": tri[1] if len(tri) > 1 else None,
        "triMax": tri[2] if len(tri) > 2 else None,
        "dcAvg": dc[1] if len(dc) > 1 else None,
        "dcMax": dc[2] if len(dc) > 2 else None,
        "residentWastePatterns": sorted(patterns & _RESIDENT_WASTE_PATTERNS),
        "transitionPatterns": sorted(patterns & _TRANSITION_PATTERNS),
    }

    is_device = data.get("is_device", False)
    gpu_caveat = "" if is_device else "（Editor 口径 GPU 仅供参考，device 采样更可信）"
    judgments = []

    # bound 类型（GpuFrameTime vs CpuFrameTime + 等待 marker）
    gpu_avg, cpu_avg = signals["gpuFrameMsAvg"], signals["cpuFrameMsAvg"]
    if gpu_avg and cpu_avg:
        if gpu_avg >= 0.8 * cpu_avg:
            judgments.append("GPU 帧耗时逼近/超过 CPU（%.1f vs %.1f ms）——偏 GPU bound 画像%s"
                             % (gpu_avg, cpu_avg, gpu_caveat))
        else:
            judgments.append("GPU 帧耗时远低于 CPU（%.1f vs %.1f ms）——偏 CPU bound 画像%s"
                             % (gpu_avg, cpu_avg, gpu_caveat))
    if wait_present > 0 and wait_present >= wait_target:
        judgments.append("主线程在等 GPU 出帧（Gfx.WaitForPresent %.0fms）——渲染/GPU 侧压力信号" % wait_present)

    # 喘息占比（功耗代理 1）
    if wait_target > 0:
        judgments.append("存在 WaitForTargetFPS 喘息（%.0fms）——CPU 有余量；"
                         "若采样覆盖 loading 窗口，这是异步加载权限过低的信号（§二）" % wait_target)
    elif cpu_avg and cpu_avg >= 30:
        judgments.append("帧均 CPU %.1fms 且几乎无 WaitForTargetFPS 喘息——芯片持续满载画像，"
                         "发热/降频风险（§五）" % cpu_avg)

    # 被动等待
    if wait_sema > 0 and signals["transitionPatterns"]:
        judgments.append("Semaphore.WaitForSignal（%.0fms）为被动等待，随 %s 主因缓解，不单独立项（§二）"
                         % (wait_sema, "、".join(signals["transitionPatterns"])))

    # 三角形预算（功耗代理 3）
    tri_avg = signals["triAvg"]
    if tri_avg and tri_avg >= _TRI_BUDGET_LOW_END:
        judgments.append("同屏三角形 avg %.1f 万，达到/超过低端机预算上限（10-15 万）——"
                         "GPU/带宽功耗压力（§四/§五）" % (tri_avg / 10000.0))

    # 常驻浪费模式（功耗代理 2）
    if signals["residentWastePatterns"]:
        judgments.append("命中常驻浪费模式 [%s]——稳态白烧点 = 功耗代理风险，优先清理（§五）"
                         % "、".join(signals["residentWastePatterns"]))
    if signals["transitionPatterns"]:
        judgments.append("命中切换/加载模式 [%s]——按「切换三件套」归并立项（§二）"
                         % "、".join(signals["transitionPatterns"]))

    return {"signals": signals, "judgments": judgments}


def emit_bottleneck_profile(data):
    prof = compute_bottleneck_profile(data)
    s = prof["signals"]
    print("\n" + "=" * 70)
    print("独立瓶颈 / 功耗代理画像（仅基于本采样数据的启发式，playbook §〇/§五）")
    print("=" * 70)

    def fmt(v, unit="", scale=1.0, nd=1):
        return ("%.*f%s" % (nd, v / scale, unit)) if v is not None else "无数据"

    print("  帧耗时  ：CPU avg=%s | GPU avg=%s max=%s" % (
        fmt(s["cpuFrameMsAvg"], "ms"), fmt(s["gpuFrameMsAvg"], "ms"), fmt(s["gpuFrameMsMax"], "ms")))
    print("  渲染量  ：DC avg=%s max=%s | Tri avg=%s max=%s" % (
        fmt(s["dcAvg"], "", 1, 0), fmt(s["dcMax"], "", 1, 0),
        fmt(s["triAvg"], "万", 10000.0), fmt(s["triMax"], "万", 10000.0)))
    print("  等待类  ：WaitForTargetFPS=%.0fms | Gfx.WaitForPresent=%.0fms | Semaphore.WaitForSignal=%.0fms" % (
        s["waitTargetFpsSelfMs"], s["gfxWaitForPresentSelfMs"], s["semaphoreWaitSelfMs"]))
    if prof["judgments"]:
        print("  判断：")
        for j in prof["judgments"]:
            print("   - %s" % j)
    else:
        print("  判断：无明确画像信号（等待类 marker 与计数器均无异常特征）。")
    print("  （启发式仅供 triage 定向；结论仍须落到热点/源码证据。）")
    return prof


def classify_cs(name):
    low = (name or "").lower()
    if low in _NOISE_CS_EXACT:
        return "instrument"
    for s in _NOISE_CS_SUBSTR:
        if s in low:
            return "instrument"
    return "signal"


def classify_lua(location, name):
    loc = (location or "").lower()
    nm = (name or "").lower()
    for s in _NOISE_LUA_LOC_SUBSTR:
        if s in loc:
            return "instrument"
    for s in _NOISE_LUA_NAME_SUBSTR:
        if s in nm:
            return "instrument"
    return "signal"


def classify_lua_role(location, name):
    """返回 signal / instrument / framework-*。framework-* 表示只能当分发链路证据，不可直接归责。"""
    base = classify_lua(location, name)
    if base != "signal":
        return base
    loc = (location or "").lower()
    nm = (name or "").lower()
    for loc_part, name_part, role in _LUA_FRAMEWORK_DISPATCHERS:
        if loc_part in loc and name_part in nm:
            return role
    return "signal"


def classify_cs_role(name):
    base = classify_cs(name)
    if base != "signal":
        return base
    low = (name or "").lower().replace(" ", "")
    for s in _CS_FRAMEWORK_DISPATCHER_SUBSTR:
        if s in low:
            return "framework-dispatcher:update-loop"
    return "signal"


def role_note(role):
    if role.startswith("framework-dispatcher"):
        return "框架分发入口，不可直接归责，需追具体 handler/组件"
    if role.startswith("framework-wrapper"):
        return "框架包装入口，不可直接归责，需追被包装调用"
    return ""


def classify_gc_path(path):
    low = (path or "").lower()
    for s in _NOISE_CS_SUBSTR + _NOISE_GC_SUBSTR:
        if s in low:
            return "instrument"
    for s in _EDITOR_ONLY_SUBSTR:
        if s in low:
            return "editor-only"
    return "signal"


def _split_location(location):
    """'Foo/Bar.lua:120' -> ('Foo/Bar.lua', 120)；无行号返回 (location, None)。"""
    if not location or location == "-":
        return None, None
    idx = location.rfind(":")
    if idx <= 0:
        return location, None
    line_part = location[idx + 1:]
    if line_part.isdigit():
        return location[:idx], _to_int(line_part)
    return location, None


_resolve_cache = {}


def resolve_source(raw_path, src_root):
    """best-effort 把 profiler 里的 lua 路径解析到真实文件。返回真实路径或 None。"""
    if not raw_path:
        return None
    if raw_path in _resolve_cache:
        return _resolve_cache[raw_path]

    candidates = []
    # 1) 直接当作相对/绝对路径
    for p in (raw_path,
              os.path.join(PROJECT_ROOT, raw_path),
              os.path.join(src_root, raw_path)):
        if os.path.isfile(p):
            candidates.append(os.path.abspath(p))
            break

    # 2) 按 basename 在 src_root 下递归找
    if not candidates:
        base = os.path.basename(raw_path)
        if base and not base.endswith(".lua"):
            base += ".lua"
        if base.endswith(".lua"):
            hits = glob.glob(os.path.join(src_root, "**", base), recursive=True)
            candidates = [os.path.abspath(h) for h in hits]

    result = None
    if len(candidates) == 1:
        result = candidates[0]
    elif len(candidates) > 1:
        # 多个同名：尽量挑路径里包含原始片段的
        norm = raw_path.replace("\\", "/").rstrip(".lua")
        seg = norm.split("/")[-2] if "/" in norm else ""
        best = [c for c in candidates if seg and seg in c.replace("\\", "/")]
        result = (best[0] if best else candidates[0]) + (
            "  (%d 个同名候选，已挑一个；如不对用 Grep 确认)" % len(candidates))
    _resolve_cache[raw_path] = result
    return result


def rel(path):
    try:
        return os.path.relpath(path, PROJECT_ROOT).replace("\\", "/")
    except ValueError:
        return path


def record_is_noise(r):
    """旧 Lua-only 格式的 Record 噪声判定（插桩自身）。"""
    if r.is_lua:
        return classify_lua(r.location, r.name) != "signal"
    return classify_cs(r.name) != "signal"


def top_by(records, key, n, predicate=None):
    pool = [r for r in records if (predicate is None or predicate(r))]
    pool.sort(key=key, reverse=True)
    return pool[:n]


def print_table(title, rows, value_fmt):
    print("\n" + "=" * 70)
    print(title)
    print("=" * 70)
    if not rows:
        print("  (无数据)")
        return
    for i, r in enumerate(rows, 1):
        print("  %2d. %s" % (i, value_fmt(r)))
        loc = r.location if r.location and r.location != "-" else "(C#/无源)"
        role = classify_lua_role(r.location, r.name) if r.is_lua else classify_cs_role(r.name)
        note = role_note(role)
        role_suffix = ("  | role=%s" % role) if note else ""
        print("       %s  | calls=%d  | %s%s" % (loc, r.calls, r.name[:90], role_suffix))
        if note:
            print("       注意：%s" % note)


def emit_human(meta, records, src_root, top, raw=False):
    print("#" * 70)
    print("# Lua Profiler 数据预处理")
    print("#" * 70)
    print("文件        : %s" % rel(meta["file"]))
    print("导出时间    : %s" % meta["export_time"])
    print("根节点 / 节点 / 唯一函数 : %d / %d / %d"
          % (meta["root_count"], meta["node_count"], meta["unique_func"]))
    if meta["omitted"]:
        print("注意        : 热点表尾部省略了 %d 个低占比函数（导出端 cap）" % meta["omitted"])
    print("解析到热点函数 : %d 条" % len(records))

    # 信噪比体检（旧格式仅 Lua self 维度）
    tot = sum(r.self_ms for r in records)
    noi = sum(r.self_ms for r in records if record_is_noise(r))
    noise_share = (noi / tot) if tot > 0 else 0.0
    print("信噪比体检    : self 耗时中插桩（Profiler.* / reimport）占 %.0f%%%s"
          % (noise_share * 100, "" if raw else "，下方榜已过滤"))
    if noise_share >= 0.5 and not raw:
        print("              ⚠ 插桩占据榜首，建议关掉其中一套 lua hook 重采更干净数据")

    # 默认过滤插桩；--raw 看全貌
    if not raw:
        kept = [r for r in records if not record_is_noise(r)]
        hidden = len(records) - len(kept)
        if hidden:
            print("              （已隐去 %d 条插桩函数；--raw 查看全部）" % hidden)
        records = kept

    print_table(
        "耗时热点 Top-%d（按自身耗时 self，单位 ms）" % top,
        top_by(records, lambda r: r.self_ms, top),
        lambda r: "self=%.3fms  total=%.3fms" % (r.self_ms, r.total_ms))

    print_table(
        "总耗时 Top-%d（按 total，含子级，单位 ms）" % top,
        top_by(records, lambda r: r.total_ms, top),
        lambda r: "total=%.3fms  self=%.3fms" % (r.total_ms, r.self_ms))

    print_table(
        "Lua GC 热点 Top-%d（按自身 Lua GC 分配）" % top,
        top_by(records, lambda r: r.self_lua_gc, top, lambda r: r.self_lua_gc > 0),
        lambda r: "luaGC=%s" % human_bytes(r.self_lua_gc))

    print_table(
        "Mono GC 热点 Top-%d（按自身 Mono GC 分配）" % top,
        top_by(records, lambda r: r.self_mono_gc, top, lambda r: r.self_mono_gc > 0),
        lambda r: "monoGC=%s" % human_bytes(r.self_mono_gc))

    print_table(
        "高频调用 Top-%d（按 calls）" % top,
        top_by(records, lambda r: r.calls, top),
        lambda r: "calls=%d  self=%.3fms  luaGC=%s"
                  % (r.calls, r.self_ms, human_bytes(r.self_lua_gc)))

    # ---- 热点涉及的源文件解析 ----
    print("\n" + "=" * 70)
    print("待阅读源文件（综合 耗时/GC/调用 各维度 Top 热点，去重后解析；默认剔除插桩与框架分发入口）")
    print("=" * 70)
    seen = {}
    pools = (top_by(records, lambda r: r.self_ms, top),
             top_by(records, lambda r: r.self_lua_gc, top, lambda r: r.self_lua_gc > 0),
             top_by(records, lambda r: r.self_mono_gc, top, lambda r: r.self_mono_gc > 0),
             top_by(records, lambda r: r.calls, top))
    for pool in pools:
        for r in pool:
            if not r.is_lua:
                continue
            if not raw and classify_lua_role(r.location, r.name) != "signal":
                continue
            raw_path, line = _split_location(r.location)
            if not raw_path:
                continue
            entry = seen.setdefault(raw_path, {"lines": set(), "resolved": None})
            if line:
                entry["lines"].add(line)
    if not seen:
        print("  (热点均为 C# 调用或无源信息，无可直接打开的 Lua 源文件)")
    else:
        for raw_path in sorted(seen.keys()):
            resolved = resolve_source(raw_path, src_root)
            lines = sorted(seen[raw_path]["lines"])
            line_str = ("  关注行: %s" % ",".join(map(str, lines))) if lines else ""
            if resolved:
                print("  - %s%s" % (rel(resolved.split("  (")[0]) if os.path.isabs(resolved.split("  (")[0]) else resolved, line_str))
                if "(" in resolved:
                    print("      %s" % resolved[resolved.find("  (") + 2:])
            else:
                print("  - [未解析] %s%s  → 用 Glob/Grep 在 Lua 源码根下定位" % (raw_path, line_str))

    print("\n" + "-" * 70)
    print("下一步：Agent 按 SKILL.md 打开上述源文件，结合各维度 Top 热点，")
    print("       归因耗时 / GC，给出文件:行 级别的具体优化建议。")
    print("-" * 70)


def emit_json(meta, records, src_root, top):
    def rec_to_dict(r):
        rawp, line = _split_location(r.location)
        role = classify_lua_role(r.location, r.name) if r.is_lua else classify_cs_role(r.name)
        return {
            "rank": r.rank, "self_ms": r.self_ms, "total_ms": r.total_ms,
            "calls": r.calls, "self_lua_gc": r.self_lua_gc,
            "self_mono_gc": r.self_mono_gc, "is_lua": r.is_lua,
            "location": r.location, "name": r.name,
            "source_path": rawp, "source_line": line,
            "noise": "instrument" if record_is_noise(r) else "signal",
            "role": role,
            "role_note": role_note(role),
            "resolved": resolve_source(rawp, src_root) if (r.is_lua and rawp) else None,
        }

    out = {
        "meta": meta,
        "top_self_time": [rec_to_dict(r) for r in top_by(records, lambda r: r.self_ms, top)],
        "top_total_time": [rec_to_dict(r) for r in top_by(records, lambda r: r.total_ms, top)],
        "top_lua_gc": [rec_to_dict(r) for r in top_by(records, lambda r: r.self_lua_gc, top, lambda r: r.self_lua_gc > 0)],
        "top_mono_gc": [rec_to_dict(r) for r in top_by(records, lambda r: r.self_mono_gc, top, lambda r: r.self_mono_gc > 0)],
        "top_calls": [rec_to_dict(r) for r in top_by(records, lambda r: r.calls, top)],
    }
    print(json.dumps(out, ensure_ascii=False, indent=2))


# ======================================================================
# AI-Profiler-v1 多 section 格式（Unity 原生 + Miku 合并导出）
# ======================================================================

def detect_format(path):
    """读头部判断格式：'ai-v1'（新多 section）或 'lua-v0'（旧 Lua-only）。"""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for _ in range(40):
                line = f.readline()
                if not line:
                    break
                s = line.strip()
                if s.startswith("Format") and "AI-Profiler-v1" in s:
                    return "ai-v1"
                if "#### SECTION:" in s:
                    return "ai-v1"
    except Exception:
        pass
    return "lua-v0"


def parse_ai_profiler(path):
    """解析 AI-Profiler-v1 文本，返回 dict。"""
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()

    data = {
        "file": path,
        "meta_lines": [],
        "frame_timeline": [],   # {frame, cpuMs, gcB}（TIMELINE 子块：前 N 帧顺序采样）
        "top_cpu_frames": [],   # {frame, cpuMs, gcB}（TOP_CPU_FRAMES 子块：全程按 cpuMs 降序）
        "cs_hotspots": [],      # {rank, selfMs, totalMs, calls, gcIncl, marker}
        "lua_hotspots": [],     # {rank, selfMs, totalMs, calls, luaGc, monoGc, location, name}
        "gpu": [],              # 原样行
        "memory": [],           # 原样行
        "gc_frames": [],        # {frame, gcB}（全程 TOP_GC_FRAMES，用于兜住 TIMELINE 截断后的 GC 尖刺）
        "gc_paths": [],         # (bytesStr, path)
        "gc_lua_vm": [],        # (bytesStr, location, name)
        "view_stats": [],       # 原样行（ProfilerUtils 界面采集：time|frame|flag|message，flag !=超标；含 fps/time 续行）
        "scene_switch": [],     # 原样行（场景切换耗时：time|frame|flag|message）
        "lua_mem_trend": [],    # 原样行（Lua VM 内存采样：time|frame|luaVmMB）
    }

    section = "META"
    gc_sub = None
    tl_sub = "timeline"  # FRAME_TIMELINE 子块：旧格式无子标题，默认按 timeline 解析（向后兼容）

    for raw in lines:
        s = raw.strip()
        if s.startswith("#### SECTION:"):
            section = s.replace("#", "").replace("SECTION:", "").strip().upper()
            gc_sub = None
            tl_sub = "timeline"
            continue
        if section == "GC" and s.startswith("## "):
            up = s.upper()
            if "TOP_GC_FRAMES" in up:
                gc_sub = "frames"
            elif "TOP_GC_ALLOC_PATHS" in up:
                gc_sub = "paths"
            elif "TOP_LUA_VM_GC" in up:
                gc_sub = "lua_vm"
            else:
                gc_sub = None
            continue
        if section == "FRAME_TIMELINE" and s.startswith("## "):
            tl_sub = "top_cpu" if "TOP_CPU_FRAMES" in s.upper() else "timeline"
            continue
        if not s or s.startswith("#") or s.startswith("=") or s.startswith("...") or s.startswith("("):
            if section == "META" and s and not s.startswith("=") and not s.startswith("#"):
                data["meta_lines"].append(s)
            continue

        if section == "META":
            data["meta_lines"].append(s)
            continue

        if section == "FRAME_TIMELINE":
            p = [x.strip() for x in s.split(" | ")]
            if len(p) >= 3:
                row = {"frame": _to_int(p[0]), "cpuMs": _to_float(p[1]), "gcB": _to_float(p[2])}
                key = "top_cpu_frames" if tl_sub == "top_cpu" else "frame_timeline"
                data[key].append(row)
        elif section == "CS_HOTSPOTS":
            p = s.split(" | ", 5)
            if len(p) >= 6:
                data["cs_hotspots"].append({
                    "rank": _to_int(p[0]), "selfMs": _to_float(p[1]), "totalMs": _to_float(p[2]),
                    "calls": _to_int(p[3]), "gcIncl": _to_float(p[4]), "marker": p[5].strip(),
                })
        elif section == "LUA_HOTSPOTS":
            p = s.split(" | ", 7)
            if len(p) >= 8:
                data["lua_hotspots"].append({
                    "rank": _to_int(p[0]), "selfMs": _to_float(p[1]), "totalMs": _to_float(p[2]),
                    "calls": _to_int(p[3]), "luaGc": _to_int(p[4]), "monoGc": _to_int(p[5]),
                    "location": p[6].strip(), "name": p[7].strip(),
                })
        elif section == "GPU":
            data["gpu"].append(s)
        elif section == "MEMORY":
            data["memory"].append(s)
        elif section == "VIEW_STATS":
            data["view_stats"].append(s)
        elif section == "SCENE_SWITCH":
            data["scene_switch"].append(s)
        elif section == "LUA_MEM_TREND":
            data["lua_mem_trend"].append(s)
        elif section == "GC":
            p = [x.strip() for x in s.split(" | ")]
            if gc_sub == "frames":
                # 格式: "frame | gcB  (human)"，取 frame + gcB 的前导数字（全程 TOP_GC_FRAMES）
                if len(p) >= 2:
                    data["gc_frames"].append({"frame": _to_int(p[0]), "gcB": _to_float(p[1].split()[0])})
            elif gc_sub == "paths" and len(p) >= 2:
                data["gc_paths"].append((p[0], " | ".join(p[1:])))
            elif gc_sub == "lua_vm" and len(p) >= 3:
                data["gc_lua_vm"].append((p[0], p[1], " | ".join(p[2:])))

    # 采样拓扑：真机连接(device) vs Editor 本地(editor)。AIProfilerExporter 的 [Target] 块写了 "Capture Mode : device ..."
    meta_blob = " ".join(data["meta_lines"]).lower()
    data["is_device"] = ("capture mode" in meta_blob and "device" in meta_blob)
    return data


def _collect_lua_sources(data, src_root, top, raw=False):
    """从 LUA_HOTSPOTS（self/luaGc）+ gc_lua_vm 的 location 解析到真实源文件。返回 {raw: set(lines)}。
    默认跳过插桩自身（Profiler.lua / reimport 等），避免让 Agent 去读测量工具源码；--raw 不跳过。"""
    seen = {}

    def add_loc(loc, name=""):
        if not raw and classify_lua_role(loc, name) != "signal":
            return
        rawp, line = _split_location(loc)
        if not rawp:
            return
        e = seen.setdefault(rawp, set())
        if line:
            e.add(line)

    lua = data["lua_hotspots"]
    for r in sorted(lua, key=lambda x: x["selfMs"], reverse=True)[:top]:
        add_loc(r.get("location"), r.get("name"))
    for r in sorted(lua, key=lambda x: x["luaGc"], reverse=True)[:top]:
        add_loc(r.get("location"), r.get("name"))
    for _b, loc, name in data["gc_lua_vm"][:top]:
        add_loc(loc, name)
    return seen


def compute_health(data):
    """估算 AI-Profiler-v1 数据里"插桩/编辑器开销"占比。返回 {cs, lua, gc} 三个 0~1 占比。"""
    def share(rows, val, is_noise):
        tot = sum(val(r) for r in rows)
        noi = sum(val(r) for r in rows if is_noise(r))
        return (noi / tot) if tot > 0 else 0.0

    cs_share = share(data["cs_hotspots"], lambda r: r["selfMs"],
                     lambda r: classify_cs(r["marker"]) != "signal")
    lua_share = share(data["lua_hotspots"], lambda r: r["selfMs"],
                      lambda r: classify_lua(r["location"], r["name"]) != "signal")
    gp = [(parse_human_bytes(b), classify_gc_path(p)) for b, p in data["gc_paths"]]
    gtot = sum(v for v, _ in gp)
    gnoi = sum(v for v, c in gp if c != "signal")
    gc_share = (gnoi / gtot) if gtot > 0 else 0.0
    return {"cs": cs_share, "lua": lua_share, "gc": gc_share}


def compute_data_quality(data):
    """从 META 和已解析 section 推导样本完整性。用于决定哪些维度可分析、哪些必须标灰/重采。"""
    diag = {
        "walked_frames": None,
        "native_no_data": False,
        "lua_no_data": False,
        "miku_enabled": None,
        "deep_lua_native_on": False,
        "segment_total": 0,
        "segment_failed": 0,
        "segment_empty": 0,
        "segment_max_bytes": None,
        "segment_total_bytes": None,
        "pollution_record": 0,
        "pollution_replay": 0,
        "warnings": [],
    }
    for line in data.get("meta_lines", []):
        low = line.lower()
        m = re.search(r"walked\s+(\d+)\s+frames", line, re.IGNORECASE)
        if m:
            diag["walked_frames"] = _to_int(m.group(1))
        if "unity native profiler" in low and "no data" in low:
            diag["native_no_data"] = True
        if "(lua vm" in low and "no data" in low:
            diag["lua_no_data"] = True
        m = re.search(r"mikuDeep=(True|False)", line, re.IGNORECASE)
        if m:
            diag["miku_enabled"] = m.group(1).lower() == "true"
        if "deepluanative=true" in low.replace(" ", ""):
            diag["deep_lua_native_on"] = True
        m = re.search(r"分段加载:\s*(\d+)\s*/\s*(\d+)", line)
        if m:
            diag["segment_failed"] = max(diag["segment_failed"], _to_int(m.group(1)))
            diag["segment_total"] = max(diag["segment_total"], _to_int(m.group(2)))
        m = re.search(
            r"Native segments\s*:\s*total=(\d+),\s*failed=(\d+),\s*empty=(\d+),\s*max=([^,]+),\s*bytes=([^\s]+)",
            line, re.IGNORECASE)
        if m:
            diag["segment_total"] = _to_int(m.group(1))
            diag["segment_failed"] = _to_int(m.group(2))
            diag["segment_empty"] = _to_int(m.group(3))
            diag["segment_max_bytes"] = m.group(4)
            diag["segment_total_bytes"] = m.group(5)
        # 采样流污染（Profiler Begin/End 配对断裂）：录制期由面板监听写入，重放期由导出器捕获。
        # 它定性了分段失败的成因——污染期段落盘即损坏，与"段过大/内存不足"是不同的重采动作。
        m = re.search(r"采样流污染\(录制期\):\s*(\d+)\s*条", line)
        if m:
            diag["pollution_record"] = max(diag["pollution_record"], _to_int(m.group(1)))
        m = re.search(r"采样流污染\(重放期\):.*?(\d+)\s*条", line)
        if m:
            diag["pollution_replay"] = max(diag["pollution_replay"], _to_int(m.group(1)))

    if diag["segment_total"] > 0 and diag["segment_failed"] > 0:
        diag["warnings"].append({
            "code": "NATIVE_SEGMENT_LOAD_FAILED",
            "severity": "critical",
            "message": "原生 Profiler 分段加载失败 %d/%d，缺失段对应时序不可恢复；C#/GC/帧尖刺维度残缺，必须标灰并重采。"
                       % (diag["segment_failed"], diag["segment_total"]),
        })
    if diag["pollution_record"] > 0 or diag["pollution_replay"] > 0:
        diag["warnings"].append({
            "code": "SAMPLE_STREAM_POLLUTION",
            "severity": "warning",
            "message": "采样流污染（Profiler Begin/End 配对断裂，录制期 %d / 重放期 %d 条）：失败段成因是污染期段落盘损坏，"
                       "非段过大/内存不足。注意首条告警 Previous samples 尾部通常只是帧内最后执行的 Update（校验点），未必是泄漏源；"
                       "定位泄漏源用菜单 Window/Analysis/AI Profiler Dump Suspect Frames（见 SKILL.md 定案方法条目）。"
                       % (diag["pollution_record"], diag["pollution_replay"]),
        })
    if diag["segment_empty"] > 0:
        diag["warnings"].append({
            "code": "NATIVE_SEGMENT_EMPTY",
            "severity": "critical",
            "message": "有 %d 个原生分段加载后没有可遍历帧，缺失时序不可恢复；必须标灰并重采。"
                       % diag["segment_empty"],
        })
    if diag["native_no_data"] or diag["walked_frames"] == 0:
        diag["warnings"].append({
            "code": "NATIVE_DATA_MISSING",
            "severity": "critical",
            "message": "原生帧数据缺失（walked 0 或 Unity native profiler=NO DATA）；不能下 C#/Mono GC/帧尖刺结论。",
        })
    if diag["lua_no_data"] and diag["miku_enabled"] is not False:
        diag["warnings"].append({
            "code": "LUA_DATA_MISSING",
            "severity": "critical",
            "message": "Lua 后端未捕获 Lua 数据；不能下 Lua CPU/Lua VM GC 结论。Editor 检查 Play/Hook；真机检查首次开启后是否完整重启且 hookReady=True。",
        })
    if diag["deep_lua_native_on"]:
        diag["warnings"].append({
            "code": "NATIVE_DEEP_LUA_ON",
            "severity": "warning",
            "message": "deepLuaNative=True，说明工程自带的原生 Deep Lua 插桩漏关，与 Lua 后端双重插桩放大噪声。",
        })
    return diag


def emit_data_quality_block(data):
    diag = compute_data_quality(data)
    print("\n" + "=" * 70)
    print("数据完整性诊断")
    print("=" * 70)
    walked = "未知" if diag["walked_frames"] is None else str(diag["walked_frames"])
    seg = "无分段" if diag["segment_total"] <= 0 else (
        "%d 段，失败 %d，空段 %d" % (diag["segment_total"], diag["segment_failed"], diag["segment_empty"]))
    if diag["segment_max_bytes"]:
        seg += "，最大段 %s，总量 %s" % (diag["segment_max_bytes"], diag["segment_total_bytes"])
    print("  原生 walked frames : %s" % walked)
    print("  原生分段状态       : %s" % seg)
    if not diag["warnings"]:
        print("  结论               : 数据源完整性未见硬伤。")
        return diag
    for w in diag["warnings"]:
        print("  [%s] %s" % (w["code"], w["message"]))
    if any(w["severity"] == "critical" for w in diag["warnings"]):
        print("  处理               : 涉及缺失维度的报告标题/结论应标灰；只分析仍有数据的维度。")
    return diag


def emit_health_block(health, raw=False, is_device=False):
    gc_label = "插桩占" if is_device else "插桩 + 编辑器工件占"
    print("\n" + "=" * 70)
    print("信噪比体检（插桩%s开销占比，越低越干净）" % ("" if is_device else " / 编辑器"))
    print("=" * 70)
    print("  C# self 耗时   : 插桩占 %.0f%%" % (health["cs"] * 100))
    print("  Lua self 耗时  : 插桩占 %.0f%%" % (health["lua"] * 100))
    print("  Mono GC 分配    : %s %.0f%%" % (gc_label, health["gc"] * 100))
    worst = max(health["cs"], health["lua"], health["gc"])
    if worst >= 0.5:
        view_note = "下方为 --raw 未过滤全貌，噪声未剔除" if raw else "下文一律按【过滤后】视图分析"
        tool_note = "测量工具开销" if is_device else "测量工具/编辑器开销"
        print("  ⚠ %s占据榜首，业务热点被淹没——%s。" % (tool_note, view_note))
        print("    Lua 走后端单 hook（AI Profiler 面板已自动关闭工程自带的冲突插桩）；后端自身仍会放大绝对耗时：")
        print("      · Lua 耗时 / GC：用此采样即可（Lua VM GC 唯 Miku 可得），看相对占比 / 尖刺")
        print("      · 干净的 C# / 引擎 CPU：另采一次关掉 Miku（仅 Unity Deep），避免 Miku 运行时占 C# 榜")
        print("      · 若 Lua 榜出现工程自带插桩的条目（deepLuaNative=True）：冲突插桩漏关了，关掉后重采")
        print("      · 通用：缩短采样窗口、只覆盖目标操作；必要时 Profiler.SetWhiteList 限定模块")
    else:
        print("  信噪比尚可，可直接参考下方 Top 榜。")


def emit_attribution_guardrails(data, top):
    rows = []
    for r in data.get("lua_hotspots", []):
        role = classify_lua_role(r.get("location"), r.get("name"))
        if role.startswith("framework-"):
            rows.append({
                "kind": "Lua", "role": role, "selfMs": r.get("selfMs", 0.0),
                "totalMs": r.get("totalMs", 0.0), "calls": r.get("calls", 0),
                "where": r.get("location") or "-", "name": r.get("name") or "",
            })
    for r in data.get("cs_hotspots", []):
        role = classify_cs_role(r.get("marker"))
        if role.startswith("framework-"):
            rows.append({
                "kind": "C#", "role": role, "selfMs": r.get("selfMs", 0.0),
                "totalMs": r.get("totalMs", 0.0), "calls": r.get("calls", 0),
                "where": "(C#/marker)", "name": r.get("marker") or "",
            })
    if not rows:
        return
    rows.sort(key=lambda x: x["selfMs"], reverse=True)
    print("\n" + "=" * 70)
    print("归责保护：框架分发 / 包装入口（不要直接立项到这些文件）")
    print("=" * 70)
    for r in rows[:top]:
        print("  %s role=%s | self=%.3fms total=%.3fms calls=%d" %
              (r["kind"], r["role"], r["selfMs"], r["totalMs"], r["calls"]))
        print("       %s | %s" % (r["where"], r["name"][:100]))
        print("       处理：%s；报告中要写下游调用链/handler/组件，不能只写此入口。" % role_note(r["role"]))


def emit_ai_human(data, src_root, top, raw=False):
    print("#" * 70)
    print("# AI Profiler 数据预处理 (AI-Profiler-v1)")
    print("#" * 70)
    print("文件: %s" % rel(data["file"]))
    for ml in data["meta_lines"][:50]:
        print("  " + ml)

    is_device = data.get("is_device", False)
    if is_device:
        print("\n" + "=" * 70)
        print("★ 真机连接采样（device）—— 不是 Editor 数据，按真机口径解读：")
        print("  · C#/GPU/内存/GC 来自连接设备帧数据；GC 不含编辑器工件（FileUtil.GetPhysicalPath /")
        print("    UnityEditor.* 真机本无），出现的都是真机真实分配。")
        print("  · GPU/渲染计数器来自设备相对可信；GPU/CPU Frame Time(ms) 见 GPU section 计数器行。")
        print("  · Lua 来自 Miku 远程回传，Miku 插桩仍放大 Lua 绝对耗时——看相对占比/调用次数/GC 字节，勿当真机实值。")
        print("  · C# 为设备 marker 层级；完整 deep C# 取决于打包是否开 deep profiling。")
        print("=" * 70)

    emit_data_quality_block(data)
    emit_health_block(compute_health(data), raw, is_device)
    # 检测工程自带的原生 Deep Lua 是否漏关（正常应 False；面板会主动关掉它）
    meta_blob = " ".join(data["meta_lines"]).lower().replace(" ", "")
    if "deepluanative=true" in meta_blob:
        print("  ⚠ 本次 deepLuaNative=True：工程自带的原生 Deep Lua 插桩漏关了，与 Lua 后端重复插桩、放大噪声。")
        print("    关掉工程自带插桩（AIProfilerCapture.DisableCompetingLuaProfiler 接入点）后重采，可显著提升信噪比。")
    if raw:
        print("\n[--raw] 已关闭插桩过滤，下方为未过滤全貌。")

    emit_attribution_guardrails(data, top)

    tl = data["frame_timeline"]
    # cpuMs 尖刺榜：合并 TIMELINE（前 N 帧顺序）与 TOP_CPU_FRAMES（全程降序），按 frame 去重。
    # 长录制时 TIMELINE 被截断到前 N 帧，靠 TOP_CPU_FRAMES 兜住后段尖刺。
    cpu_pool = {}
    for r in tl + data.get("top_cpu_frames", []):
        cpu_pool[r["frame"]] = r
    if tl or cpu_pool:
        print("\n" + "=" * 70)
        print("帧时间线尖刺 Top-%d（按 cpuMs，含全程 TOP_CPU_FRAMES）" % top)
        print("=" * 70)
        for r in sorted(cpu_pool.values(), key=lambda x: x["cpuMs"], reverse=True)[:top]:
            print("  frame %s | cpu=%.3fms | gc=%.0fB" % (r["frame"], r["cpuMs"], r["gcB"]))
        # GC.Alloc 尖刺榜：合并 TIMELINE（有 cpuMs）与全程 TOP_GC_FRAMES（仅 frame+gcB），按 frame 去重、优先取 tl。
        # 长录制时 TIMELINE 截断到前 N 帧，靠 TOP_GC_FRAMES 兜住后段 GC 尖刺。
        gc_pool = {}
        for r in tl:
            if r["gcB"] > 0:
                gc_pool[r["frame"]] = {"frame": r["frame"], "gcB": r["gcB"], "cpuMs": r["cpuMs"]}
        for r in data.get("gc_frames", []):
            if r["gcB"] > 0 and r["frame"] not in gc_pool:
                gc_pool[r["frame"]] = {"frame": r["frame"], "gcB": r["gcB"], "cpuMs": None}
        spikes = sorted(gc_pool.values(), key=lambda x: x["gcB"], reverse=True)[:top]
        if spikes:
            print("\n帧时间线尖刺 Top-%d（按 GC.Alloc，含全程 TOP_GC_FRAMES）" % top)
            for r in spikes:
                cpu = ("%.3fms" % r["cpuMs"]) if r["cpuMs"] is not None else "?(仅在 TOP_GC_FRAMES)"
                print("  frame %s | gc=%s | cpu=%s" % (r["frame"], human_bytes(r["gcB"]), cpu))

    cs = data["cs_hotspots"]
    label = "C# / 引擎 CPU 热点 Top-%d（self ms%s）" % (top, "" if raw else "，已过滤插桩")
    print("\n" + "=" * 70)
    print(label)
    print("=" * 70)
    if cs:
        ordered = sorted(cs, key=lambda x: x["selfMs"], reverse=True)
        shown = [r for r in ordered if raw or classify_cs(r["marker"]) == "signal"]
        hidden = len(ordered) - len(shown)
        matched_patterns = set()
        for r in shown[:top]:
            role = classify_cs_role(r["marker"])
            suffix = "" if not role_note(role) else " | role=%s" % role
            pid, hint = classify_cs_pattern(r["marker"])
            if pid:
                suffix += " | pattern=%s" % pid
                matched_patterns.add(pid)
            print("  self=%.3fms total=%.3fms calls=%d gcIncl=%s | %s%s" % (
                r["selfMs"], r["totalMs"], r["calls"], human_bytes(r["gcIncl"]), r["marker"], suffix))
            if role_note(role):
                print("       注意：%s" % role_note(role))
            if pid:
                print("       模式：%s" % hint)
        if hidden:
            print("  （已隐去 %d 条插桩 / EditorLoop 行；--raw 查看全部）" % hidden)
        if matched_patterns:
            print("  （命中已知模式 %s——按提示作优先假设 triage，方法论详见"
                  " references/perf-analysis-playbook.md）" % "、".join(sorted(matched_patterns)))
    else:
        print("  (无 C# 热点数据)")

    lua = data["lua_hotspots"]
    label = "Lua CPU 热点 Top-%d（self ms，来自 Miku%s）" % (top, "" if raw else "，已过滤插桩")
    print("\n" + "=" * 70)
    print(label)
    print("=" * 70)
    if lua:
        ordered = sorted(lua, key=lambda x: x["selfMs"], reverse=True)
        shown = [r for r in ordered if raw or classify_lua(r["location"], r["name"]) == "signal"]
        hidden = len(ordered) - len(shown)
        for r in shown[:top]:
            print("  self=%.3fms total=%.3fms calls=%d luaGc=%s monoGc=%s" % (
                r["selfMs"], r["totalMs"], r["calls"], human_bytes(r["luaGc"]), human_bytes(r["monoGc"])))
            role = classify_lua_role(r["location"], r["name"])
            suffix = "" if not role_note(role) else " | role=%s" % role
            print("       %s | %s%s" % (r["location"] or "-", r["name"][:90], suffix))
            if role_note(role):
                print("       注意：%s" % role_note(role))
        if hidden:
            print("  （已隐去 %d 条插桩行：Profiler.* / reimport；--raw 查看全部）" % hidden)
        gcordered = [r for r in sorted(lua, key=lambda x: x["luaGc"], reverse=True) if r["luaGc"] > 0]
        gcshown = [r for r in gcordered if raw or classify_lua(r["location"], r["name"]) == "signal"]
        if gcshown:
            print("\nLua VM GC 热点 Top-%d（luaGc%s）" % (top, "" if raw else "，已过滤插桩"))
            for r in gcshown[:top]:
                role = classify_lua_role(r["location"], r["name"])
                suffix = "" if not role_note(role) else " | role=%s" % role
                print("  luaGc=%s | %s | %s%s" % (human_bytes(r["luaGc"]), r["location"] or "-", r["name"][:80], suffix))
            gchidden = len(gcordered) - len(gcshown)
            if gchidden:
                print("  （已隐去 %d 条插桩分配；--raw 查看全部）" % gchidden)
    else:
        print("  (无 Lua 数据 - Miku 未捕获；见 META 各源状态)")

    # 高频调用画像（calls/帧）：单次便宜但每帧狂刷的调用在耗时榜上隐形，却是 GC/功耗的稳定源。
    # 修法方向是「降频」（dirty 化 / 缓存 / 事件化），与耗时榜的「降单次成本」正交。
    walked = compute_data_quality(data).get("walked_frames") or 0
    if walked > 0 and (cs or lua):
        print("\n" + "=" * 70)
        print("高频调用画像 Top-%d（calls/帧 ≥ 1，按调用次数降序%s）" % (top, "" if raw else "，已过滤插桩"))
        print("=" * 70)
        if walked == 2000:
            print("  ⚠ walked=2000 恰为 live（关无上限）模式帧缓冲上限——原生帧可能只覆盖录制尾部，")
            print("    而 Lua calls 覆盖全程，calls/帧 会被同比例高估；建议开「无上限录制」重采后再下频率结论。")
        lua_rate = [r for r in lua
                    if r.get("calls", 0) >= walked and (raw or classify_lua(r["location"], r["name"]) == "signal")]
        lua_rate.sort(key=lambda x: x["calls"], reverse=True)
        if lua_rate:
            print("  [Lua]")
            for r in lua_rate[:top]:
                per_call_us = (r["selfMs"] / r["calls"] * 1000) if r["calls"] > 0 else 0
                role = classify_lua_role(r["location"], r["name"])
                suffix = "" if not role_note(role) else " | role=%s" % role
                print("  %.1f 次/帧 | calls=%d self=%.1fms (%.2fµs/次) luaGc=%s | %s | %s%s" % (
                    r["calls"] / walked, r["calls"], r["selfMs"], per_call_us,
                    human_bytes(r["luaGc"]), r["location"] or "-", r["name"][:70], suffix))
        cs_rate = [r for r in cs
                   if r.get("calls", 0) >= walked and (raw or classify_cs(r["marker"]) == "signal")]
        cs_rate.sort(key=lambda x: x["calls"], reverse=True)
        if cs_rate:
            print("  [C#/引擎]")
            for r in cs_rate[:top]:
                per_call_us = (r["selfMs"] / r["calls"] * 1000) if r["calls"] > 0 else 0
                print("  %.1f 次/帧 | calls=%d self=%.1fms (%.2fµs/次) gcIncl=%s | %s" % (
                    r["calls"] / walked, r["calls"], r["selfMs"], per_call_us,
                    human_bytes(r["gcIncl"]), r["marker"]))
        if not lua_rate and not cs_rate:
            print("  (无 ≥1 次/帧 的高频条目)")
        else:
            print("  修法方向：高频条目优先「降频」（dirty 时才算 / 缓存结果 / 事件化替代轮询），与耗时榜「降单次成本」正交；")
            print("  框架分发器（role=framework-*）天然高频不立项，看其下游 handler；calls 数不受插桩放大影响、相对可信。")

    if data["gpu"]:
        print("\n" + "=" * 70)
        print("GPU / 渲染计数器")
        print("=" * 70)
        for s in data["gpu"]:
            print("  " + s)
        print("  口径参考：低端机同屏三角形预算 10-15w；逐 Pass 合批/SRP Batcher 覆盖率需")
        print("  FrameDebugger 定性（见 references/perf-analysis-playbook.md §三/§四）。")

    if data["memory"]:
        print("\n" + "=" * 70)
        print("内存计数器（headAvg/tailAvg/trend 列 = 录制前/后窗口均值对比，判上升趋势）")
        print("=" * 70)
        for s in data["memory"]:
            print("  " + s)
        rising = []
        trend_re = re.compile(r"^([^|]+)\|.*\|\s*([+-][\d.]+)%\s*$")
        for s in data["memory"]:
            m = trend_re.match(s)
            if m and _to_float(m.group(2)) >= 10.0:
                rising.append((m.group(1).strip(), _to_float(m.group(2))))
        if rising:
            print("  ⚠ 上升趋势计数器（+10% 以上）——持续上升是泄漏/累积信号，结合录制期操作与 Memory Profiler 复核：")
            for label, pct in rising:
                print("    %s: %+.1f%%" % (label, pct))

    if data.get("view_stats"):
        # 头行格式: "HH:MM:SS|frame|flag|[ProfilerUtils][Type] 界面[子项] [ViewName]..."（frame 列旧格式可能缺失）；
        # fps/time 续行无此前缀
        head_re = re.compile(r"^(\d{2}:\d{2}:\d{2})\|(?:(\d+)\|)?([-!])\|\[ProfilerUtils\]\[(\w+)\]\s*(\S*)\s*\[([^\]]+)\]")
        views = set()
        flagged = []
        heads = 0
        for line in data["view_stats"]:
            m = head_re.match(line)
            if not m:
                continue
            heads += 1
            views.add(m.group(6))
            if m.group(3) == "!":
                flagged.append(line)
        print("\n" + "=" * 70)
        print("界面打开统计（VIEW_STATS · 运行时 AIProfilerCapture 采集）")
        print("=" * 70)
        print("  共 %d 条记录，覆盖 %d 个界面；超标 %d 条（flag=!）。" % (heads, len(views), len(flagged)))
        if flagged:
            print("  超标条目（资源/总耗时/点击响应超阈值、FPS 卡顿超标、加载超统计窗口——正文自带阈值提示）：")
            for line in flagged[:top]:
                print("    " + line)
            if len(flagged) > top:
                print("    ...（其余 %d 条超标见导出文件 VIEW_STATS section）" % (len(flagged) - top))
            print("  归因提示：打开耗时高看 ViewOpen 拆分（资源加载 vs 显示完成）+ 该界面 Lua OnOpen 热点；")
            print("  点击响应 slow 是「点了没画面反应」的静默期；ViewFPS 卡顿结合尖刺帧与逐帧 time 序列（见原文续行）。")
        else:
            print("  无超标条目。逐条明细与逐帧 fps/time 序列见导出文件 VIEW_STATS section 原文。")

    if data.get("scene_switch"):
        sw_re = re.compile(
            r"^(\d{2}:\d{2}:\d{2})\|(?:(\d+)\|)?([-!])\|\[ProfilerUtils\]\[SceneSwitch\]\s*场景\s*"
            r"\[([^\]]+)\].*?切换耗时:\s*([\d.]+)ms")
        rows = []
        for line in data["scene_switch"]:
            m = sw_re.match(line)
            if m:
                rows.append({"time": m.group(1), "frame": m.group(2) or "?", "flag": m.group(3),
                             "route": m.group(4), "ms": _to_float(m.group(5))})
        if rows:
            slow = [r for r in rows if r["flag"] == "!"]
            print("\n" + "=" * 70)
            print("场景切换耗时（SCENE_SWITCH · SwitchScene → SwitchToSceneOver，loading 关闭后的用户可感耗时）")
            print("=" * 70)
            print("  共 %d 次切换；超标 %d 次（>3000ms）。" % (len(rows), len(slow)))
            for r in sorted(rows, key=lambda x: x["ms"], reverse=True)[:top]:
                mark = " [!]" if r["flag"] == "!" else ""
                print("  %s | frame %s | %-40s | %.0fms%s" % (r["time"], r["frame"], r["route"], r["ms"], mark))
            if slow:
                print("  超标切换按六段分解定位（前摇/Unity场景加载/MinLoadTime白等/业务资源/业务初始化/揭幕），")
                print("  先算结构性等待占比（最小 loading 时长白等 / 固定 Delay / 被串行推迟的加载），占比高先修结构再优化真实加载。")

    if data.get("lua_mem_trend"):
        mem_re = re.compile(r"^(\d{2}:\d{2}:\d{2})\|(\d+)\|([\d.]+)$")
        samples = []
        for line in data["lua_mem_trend"]:
            m = mem_re.match(line)
            if m:
                samples.append((m.group(1), _to_float(m.group(3))))
        if len(samples) >= 2:
            first_t, first_v = samples[0]
            last_t, last_v = samples[-1]
            peak_v = max(v for _, v in samples)
            delta = last_v - first_v
            pct = (delta / first_v * 100) if first_v > 0 else 0
            print("\n" + "=" * 70)
            print("Lua VM 内存趋势（LUA_MEM_TREND · collectgarbage count 周期采样）")
            print("=" * 70)
            print("  样本 %d 个：首 %.1fMB (%s) → 末 %.1fMB (%s)，峰值 %.1fMB，Δ %+.1fMB (%+.1f%%)"
                  % (len(samples), first_v, first_t, last_v, last_t, peak_v, delta, pct))
            if delta >= 20 and pct >= 15:
                print("  ⚠ Lua VM 内存持续上升（Δ≥20MB 且 ≥15%）——泄漏/累积信号，结合 TOP_LUA_VM_GC 分配源与")
                print("    录制期操作（反复开某界面/挂机）定位；确认前先排除录制期业务本身就该涨（进新场景/加载新模块）。")
        elif data["lua_mem_trend"]:
            print("\n  (LUA_MEM_TREND 样本不足 2 个，无趋势可判)")

    emit_bottleneck_profile(data)

    if data["gc_paths"] or data["gc_lua_vm"]:
        print("\n" + "=" * 70)
        print("GC 归因")
        print("=" * 70)
        if data["gc_paths"]:
            if raw:
                print("Mono GC.Alloc 调用路径 Top:")
                for b, p in data["gc_paths"][:top]:
                    print("  %s | %s" % (b, p))
            else:
                classified = [(b, p, classify_gc_path(p)) for b, p in data["gc_paths"]]
                sig = [(b, p) for b, p, c in classified if c == "signal"]
                editor = [(b, p) for b, p, c in classified if c == "editor-only"]
                instr = [(b, p) for b, p, c in classified if c == "instrument"]
                print("Mono GC.Alloc 调用路径 Top（已分组）:")
                print("  [真实业务 / 引擎分配]")
                if sig:
                    for b, p in sig[:top]:
                        print("    %s | %s" % (b, p))
                else:
                    print("    (本组为空——Mono GC 几乎全是插桩/编辑器工件)")
                if editor:
                    print("  [仅编辑器工件 · 真机无，勿据此优化]")
                    for b, p in editor[:8]:
                        print("    %s | %s" % (b, p))
                if instr:
                    print("  （另隐去 %d 条插桩自身分配；--raw 查看全部）" % len(instr))
        if data["gc_lua_vm"]:
            print("Lua VM GC Top%s:" % ("" if raw else "（已过滤插桩）"))
            rows = data["gc_lua_vm"] if raw else [
                (b, loc, name) for (b, loc, name) in data["gc_lua_vm"]
                if classify_lua(loc, name) == "signal"]
            for b, loc, name in rows[:top]:
                role = classify_lua_role(loc, name)
                suffix = "" if not role_note(role) else " | role=%s" % role
                print("  %s | %s | %s%s" % (b, loc, name[:80], suffix))
            hidden_vm = len(data["gc_lua_vm"]) - len(rows)
            if hidden_vm and not raw:
                print("  （已隐去 %d 条插桩分配：Profiler.New 等；--raw 查看全部）" % hidden_vm)

    print("\n" + "=" * 70)
    print("待阅读 Lua 源文件（解析到 Lua 源码根下的真实路径%s）" % ("" if raw else "，已剔除插桩与框架分发入口"))
    print("=" * 70)
    seen = _collect_lua_sources(data, src_root, top, raw)
    if not seen:
        print("  (无可解析的 Lua 源位置；热点可能集中在 C#/引擎，或 Miku 无数据)")
    else:
        for raw_path in sorted(seen.keys()):
            resolved = resolve_source(raw_path, src_root)
            lines = sorted(seen[raw_path])
            line_str = ("  关注行: %s" % ",".join(map(str, lines))) if lines else ""
            if resolved:
                base = resolved.split("  (")[0]
                disp = rel(base) if os.path.isabs(base) else resolved
                print("  - %s%s" % (disp, line_str))
                if "(" in resolved:
                    print("      %s" % resolved[resolved.find("  (") + 2:])
            else:
                print("  - [未解析] %s%s  → 用 Glob/Grep 在 Lua 源码根下定位" % (raw_path, line_str))

    print("\n" + "-" * 70)
    print("下一步：Agent 按 SKILL.md，结合 C#/Lua/GPU/内存/GC 各维度，打开源文件归因，")
    print("       给出 文件:行 级优化建议（区分确定能改 / 需确认契约）。")
    print("-" * 70)


def emit_ai_json(data, src_root, top):
    def resolve_lua(loc):
        rawp, line = _split_location(loc)
        return {"raw": rawp, "line": line,
                "resolved": resolve_source(rawp, src_root) if rawp else None}

    out = dict(data)
    out["health"] = compute_health(data)
    out["diagnostics"] = compute_data_quality(data)
    out["bottleneck_profile"] = compute_bottleneck_profile(data)
    # 给每条热点打 noise 标签，便于消费端自行过滤
    for r in out["cs_hotspots"]:
        r["noise"] = classify_cs(r["marker"])
        r["role"] = classify_cs_role(r["marker"])
        r["role_note"] = role_note(r["role"])
        pid, hint = classify_cs_pattern(r["marker"])
        r["pattern"] = pid
        r["pattern_note"] = hint
    for r in out["lua_hotspots"]:
        r["noise"] = classify_lua(r["location"], r["name"])
        r["role"] = classify_lua_role(r["location"], r["name"])
        r["role_note"] = role_note(r["role"])
    _lua_signal = [
        r for r in sorted(data["lua_hotspots"], key=lambda x: x["selfMs"], reverse=True)
        if r.get("location") and r["location"] != "-" and classify_lua_role(r["location"], r["name"]) == "signal"
    ]
    out["resolved_lua_sources"] = [resolve_lua(r["location"]) for r in _lua_signal[:top]]
    print(json.dumps(out, ensure_ascii=False, indent=2))


# ======================================================================
# --diff：两份 AI-Profiler-v1 导出对比（优化落地验证 / 版本回归检测）
# ======================================================================

def run_diff(cur_path, base_path, top, raw=False):
    """当前导出 vs 基线导出：按 walked frames 归一后输出回归/改善榜。返回退出码。"""
    if detect_format(cur_path) != "ai-v1" or detect_format(base_path) != "ai-v1":
        print("[错误] --diff 仅支持两份 AI-Profiler-v1 导出（旧 Lua-only 格式不支持）。", file=sys.stderr)
        return 1
    cur = parse_ai_profiler(cur_path)
    base = parse_ai_profiler(base_path)
    wc = compute_data_quality(cur).get("walked_frames") or 0
    wb = compute_data_quality(base).get("walked_frames") or 0
    normalized = wc > 0 and wb > 0
    nc = wc if normalized else 1
    nb = wb if normalized else 1

    print("#" * 70)
    print("# AI Profiler 对比（当前 vs 基线）")
    print("#" * 70)
    print("当前: %s (walked %s 帧)" % (rel(cur_path), wc or "?"))
    print("基线: %s (walked %s 帧)" % (rel(base_path), wb or "?"))
    if normalized:
        print("口径: 热点耗时/GC 均为「每帧均值」，录制时长不同也可比；界面/场景为单次耗时。")
    else:
        print("⚠ walked frames 缺失，热点退化为绝对值对比——两次录制时长不同时结论不可比，建议重采。")
    if wc == 2000 or wb == 2000:
        print("⚠ 有一侧 walked=2000（live 模式帧缓冲上限）——原生帧可能只覆盖录制尾部而 Lua 数据覆盖全程，")
        print("  每帧归一在该侧被高估；建议两侧都用「无上限录制」重采后再对比。")
    print("提醒: 两次采样须同机器、同场景、同操作路径；Miku 插桩放大绝对值，看相对变化与方向，勿把 ms 当真机值。")

    def _fmt_ms(v):
        return "%.3fms" % v

    def _fmt_b(v):
        return human_bytes(v)

    def _diff_rows(cur_rows, base_rows, key_of, val_of, is_signal, eps):
        ci, bi = {}, {}
        for r in cur_rows:
            if raw or is_signal(r):
                k = key_of(r)
                if k:
                    ci[k] = ci.get(k, 0.0) + val_of(r)
        for r in base_rows:
            if raw or is_signal(r):
                k = key_of(r)
                if k:
                    bi[k] = bi.get(k, 0.0) + val_of(r)
        out = []
        for k in set(ci) | set(bi):
            cv = ci.get(k, 0.0) / nc
            bv = bi.get(k, 0.0) / nb
            d = cv - bv
            if abs(d) >= eps:
                out.append((d, bv, cv, k))
        out.sort(key=lambda x: abs(x[0]), reverse=True)
        return out

    def _emit(title, deltas, fmt):
        print("\n" + "=" * 70)
        print(title)
        print("=" * 70)
        if not deltas:
            print("  (无显著差异)")
            return
        reg = [r for r in deltas if r[0] > 0][:top]
        imp = [r for r in deltas if r[0] < 0][:top]
        if reg:
            print("  [回归/上升 Top]")
            for d, bv, cv, k in reg:
                tag = "  [基线无→新增]" if bv == 0 else ""
                print("    Δ+%s | 基线 %s → 当前 %s | %s%s" % (fmt(d), fmt(bv), fmt(cv), k[:90], tag))
        if imp:
            print("  [改善/下降 Top]")
            for d, bv, cv, k in imp:
                tag = "  [已消失]" if cv == 0 else ""
                print("    Δ-%s | 基线 %s → 当前 %s | %s%s" % (fmt(-d), fmt(bv), fmt(cv), k[:90], tag))

    per_frame = "每帧" if normalized else "总量"
    eps_ms = 0.005 if normalized else 5.0
    eps_gc = 32.0 if normalized else 32.0 * 1024

    _emit("Lua 热点 self 耗时差异（%s，ms%s）" % (per_frame, "" if raw else "，已过滤插桩"),
          _diff_rows(cur.get("lua_hotspots") or [], base.get("lua_hotspots") or [],
                     lambda r: r.get("name"), lambda r: r.get("selfMs", 0.0),
                     lambda r: classify_lua(r.get("location"), r.get("name")) == "signal", eps_ms),
          _fmt_ms)
    _emit("Lua VM GC 差异（%s，bytes%s）" % (per_frame, "" if raw else "，已过滤插桩"),
          _diff_rows(cur.get("lua_hotspots") or [], base.get("lua_hotspots") or [],
                     lambda r: r.get("name"), lambda r: float(r.get("luaGc", 0)),
                     lambda r: classify_lua(r.get("location"), r.get("name")) == "signal", eps_gc),
          _fmt_b)
    _emit("C#/引擎热点 self 耗时差异（%s，ms%s）" % (per_frame, "" if raw else "，已过滤插桩"),
          _diff_rows(cur.get("cs_hotspots") or [], base.get("cs_hotspots") or [],
                     lambda r: r.get("marker"), lambda r: r.get("selfMs", 0.0),
                     lambda r: classify_cs(r.get("marker")) == "signal", eps_ms),
          _fmt_ms)

    # 帧均 CPU 与内存计数器（整体健康度）
    def _tl_avg(d):
        tl = d.get("frame_timeline") or []
        if not tl:
            return None
        return sum(r["cpuMs"] for r in tl) / len(tl)

    ca, ba = _tl_avg(cur), _tl_avg(base)
    print("\n" + "=" * 70)
    print("整体指标差异")
    print("=" * 70)
    if ca is not None and ba is not None:
        print("  帧均 CPU: 基线 %.3fms → 当前 %.3fms (Δ%+.3fms)" % (ba, ca, ca - ba))
    else:
        print("  帧均 CPU: 数据不足（timeline 缺失）")

    def _mem_avgs(d):
        out = {}
        for s in d.get("memory") or []:
            parts = [p.strip() for p in s.split("|")]
            if len(parts) >= 3:
                v = parse_human_bytes(parts[2])
                if v > 0:
                    out[parts[0]] = v
        return out

    ma, mb = _mem_avgs(cur), _mem_avgs(base)
    for label in sorted(set(ma) & set(mb)):
        d = ma[label] - mb[label]
        if abs(d) >= 1024 * 1024:  # ≥1MB 才值得列
            print("  %s(avg): 基线 %s → 当前 %s (Δ%s)" % (label, human_bytes(mb[label]), human_bytes(ma[label]), human_bytes(d)))

    # 界面首开耗时（显示完成）与场景切换（单次耗时，非每帧归一）
    def _view_open_times(d):
        out = {}
        vo_re = re.compile(r"^\d{2}:\d{2}:\d{2}\|(?:\d+\|)?[-!]\|\[ProfilerUtils\]\[ViewOpen\]\s*界面\s*"
                           r"\[([^\]]+)\].*?显示完成耗时:\s*([\d.]+)ms")
        for line in d.get("view_stats") or []:
            m = vo_re.match(line)
            if m and m.group(1) not in out:
                out[m.group(1)] = _to_float(m.group(2))
        return out

    va, vb = _view_open_times(cur), _view_open_times(base)
    common_views = [(va[k] - vb[k], vb[k], va[k], k) for k in set(va) & set(vb) if abs(va[k] - vb[k]) >= 50]
    if common_views:
        common_views.sort(key=lambda x: abs(x[0]), reverse=True)
        print("\n" + "=" * 70)
        print("界面首开「显示完成耗时」差异（≥50ms；仅两次采样都打开过的界面，注意冷/热态与缓冲池差异）")
        print("=" * 70)
        for d, bv, cv, k in common_views[:top]:
            print("  Δ%+.0fms | 基线 %.0fms → 当前 %.0fms | %s" % (d, bv, cv, k))

    def _scene_times(d):
        out = {}
        sw_re = re.compile(r"^\d{2}:\d{2}:\d{2}\|(?:\d+\|)?[-!]\|\[ProfilerUtils\]\[SceneSwitch\]\s*场景\s*"
                           r"\[([^\]]+)\].*?切换耗时:\s*([\d.]+)ms")
        for line in d.get("scene_switch") or []:
            m = sw_re.match(line)
            if m and m.group(1) not in out:
                out[m.group(1)] = _to_float(m.group(2))
        return out

    sa, sb = _scene_times(cur), _scene_times(base)
    common_scenes = [(sa[k] - sb[k], sb[k], sa[k], k) for k in set(sa) & set(sb) if abs(sa[k] - sb[k]) >= 100]
    if common_scenes:
        common_scenes.sort(key=lambda x: abs(x[0]), reverse=True)
        print("\n" + "=" * 70)
        print("场景切换耗时差异（≥100ms；仅两次采样都发生过的切换路线，首次为准）")
        print("=" * 70)
        for d, bv, cv, k in common_scenes[:top]:
            print("  Δ%+.0fms | 基线 %.0fms → 当前 %.0fms | %s" % (d, bv, cv, k))

    print("\n" + "-" * 70)
    print("结论口径：回归榜非空 ≠ 一定劣化（操作路径/场景覆盖差异也会造成），先核对两次采样的操作是否一致；")
    print("验证某项优化是否生效，直接在改善榜里找对应条目（已消失/显著下降），找不到即「未生效或被路径差异淹没」。")
    return 0


def main():
    ap = argparse.ArgumentParser(description="AI Profiler 导出文本预处理器")
    ap.add_argument("--file", help="指定导出文件；省略则取最新")
    ap.add_argument("--dir", default=None, help="ProfilerLogs 目录（默认 配置 log_dir，其次 <项目根>/Assets/ProfilerLogs）")
    ap.add_argument("--src-root", default=None, help="Lua 源码根，用于解析路径（默认 配置 src_root，其次 <项目根>/Assets/Lua）")
    ap.add_argument("--config", default=DEFAULT_CONFIG, help="项目配置 JSON（默认脚本旁的 profiler_config.json，可缺省）")
    ap.add_argument("--top", type=int, default=20, help="每个维度 Top-N（默认 20）")
    ap.add_argument("--list", action="store_true", help="仅列出可分析文件")
    ap.add_argument("--json", action="store_true", help="JSON 输出")
    ap.add_argument("--raw", action="store_true",
                    help="关闭插桩过滤，打印未过滤全貌（默认会过滤 Miku/Profiler/EditorLoop 等噪声）")
    ap.add_argument("--diff", metavar="BASELINE",
                    help="与基线导出对比（当前文件 vs BASELINE 文件），输出按帧归一的回归/改善榜；"
                         "用于验证优化落地效果或版本回归。仅支持两份 AI-Profiler-v1")
    args = ap.parse_args()

    cfg = load_config(args.config)
    apply_config(cfg)
    if not args.dir:
        args.dir = cfg.get("log_dir") or DEFAULT_LOG_DIR
        if not os.path.isabs(args.dir):
            args.dir = os.path.join(PROJECT_ROOT, args.dir)
    if not args.src_root:
        args.src_root = cfg.get("src_root") or DEFAULT_SRC_ROOT
        if not os.path.isabs(args.src_root):
            args.src_root = os.path.join(PROJECT_ROOT, args.src_root)

    if args.list:
        files = list_files(args.dir)
        if not files:
            print("ProfilerLogs 目录无 .txt 文件: %s" % args.dir)
            return 0
        print("可分析文件（旧 → 新）:")
        for p in files:
            print("  %s" % rel(p))
        print("\n最新: %s" % rel(files[-1]))
        return 0

    target = args.file or find_latest_file(args.dir)
    if not target:
        print("[错误] 未找到导出文件。", file=sys.stderr)
        print("  在 Unity 的 AI Profiler 面板（Window/Analysis/AI Profiler）ExportForAI 先产出数据，", file=sys.stderr)
        print("  或用 --file 指定。查找目录: %s" % args.dir, file=sys.stderr)
        return 1
    if not os.path.isfile(target):
        print("[错误] 文件不存在: %s" % target, file=sys.stderr)
        return 1

    top = max(1, args.top)

    if args.diff:
        if not os.path.isfile(args.diff):
            print("[错误] --diff 基线文件不存在: %s" % args.diff, file=sys.stderr)
            return 1
        return run_diff(target, args.diff, top, args.raw)

    fmt = detect_format(target)

    if fmt == "ai-v1":
        data = parse_ai_profiler(target)
        if not data["cs_hotspots"] and not data["lua_hotspots"] and not data["frame_timeline"]:
            print("[警告] AI-Profiler-v1 文件未解析到任何数据，可能为空或格式异常。", file=sys.stderr)
            print("  文件: %s" % rel(target), file=sys.stderr)
        if args.json:
            emit_ai_json(data, args.src_root, top)
        else:
            emit_ai_human(data, args.src_root, top, args.raw)
        return 0

    # 旧 Lua-only 格式（向后兼容）
    meta, records = parse_export(target)
    if not records:
        print("[警告] 未从 SECTION 1 解析到任何热点函数，文件可能格式异常或为空。", file=sys.stderr)
        print("  文件: %s" % rel(target), file=sys.stderr)
        # 仍打印 meta，便于排查
    if args.json:
        emit_json(meta, records, args.src_root, top)
    else:
        emit_human(meta, records, args.src_root, top, args.raw)
    return 0


if __name__ == "__main__":
    sys.exit(main())
