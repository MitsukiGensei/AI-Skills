#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace AIProfiler
{
    /// <summary>
    /// AI Profiler 统一导出器：把 Unity 原生 Profiler 帧数据（C# CPU / GC / 时间线）、
    /// Lua 后端采样（LuaProfilerBackend，真实 Lua 自身耗时 + Lua VM GC，可无）、ProfilerRecorder 计数器（内存/渲染）
    /// 合并成一份 AI 可解析的多 section 文本，落地 Assets/ProfilerLogs/yyyy_MM_dd_HH_mm_ss.txt。
    /// 配合 profiler-analysis 技能的 analyze_profiler.py 分析。
    /// </summary>
    public static class AIProfilerExporter
    {
        public const string FORMAT_TAG = "AI-Profiler-v1";

        private const int MAX_HOT_ROWS = 300;        // 每个热点表最多行数
        private const int MAX_TIMELINE_ROWS = 10000; // 时间线顺序采样最多行数（前 N 帧）
        private const int MAX_TOP_CPU_FRAMES = 30;   // 全程按 cpuMs 降序的尖刺榜行数
        private const int MAX_GC_PATHS = 60;         // GC 调用路径最多行数
        internal const int RAW_SAMPLE_GUARD_PER_FRAME = 2000000; // 单帧原始样本扫描上限（Deep 极端帧安全阀；DumpSuspectFrames 复用）
        private const int PROGRESS_UPDATE_INTERVAL = 16;        // 进度条每 N 帧刷一次（DisplayCancelableProgressBar 单次强制重绘 ~ms 级）
        private const int MAX_SEG_FAIL_DETAILS = 20;            // META 里逐段失败原因最多列出条数

        // 计数器头/尾窗口均值（泄漏斜率信号）：各取前/后 TREND_WINDOW_SAMPLES 个样本；
        // 半程样本不足 TREND_MIN_WINDOW 时不给趋势（短录制头尾重叠无意义）。窗口侧 StopRecord 与真机路径共用。
        public const int TREND_WINDOW_SAMPLES = 300;
        public const int TREND_MIN_WINDOW = 30;

        /// <summary>ProfilerRecorder 计数器统计（窗口在 StopRecord 时算好后传入）。</summary>
        public class CounterStat
        {
            public string label;
            public string category;   // "Memory" / "Render"
            public bool isMemory;     // 值是否为字节（用于人类可读格式化）
            public bool valid;
            public int sampleCount;
            public double min, max, avg, last;
            // 头/尾窗口均值（录制前段 vs 后段，各 TREND_WINDOW_SAMPLES 样本）——判内存上升趋势用
            public bool trendValid;
            public double headAvg, tailAvg;
        }

        /// <summary>一次录制采集到的全部数据。</summary>
        public class CaptureResult
        {
            public int firstFrame;
            public int lastFrame;
            public List<CounterStat> counters = new List<CounterStat>();

            // FrameTimingManager 采样（best-effort）
            public bool gpuTimingValid;
            public double gpuMinMs, gpuMaxMs, gpuAvgMs, cpuTimingAvgMs;
            public int gpuTimingSampleCount;

            // 各深度开关在录制时的状态（写进 META 供分析时校准）
            public bool deepProfilingOn;
            public bool deepLuaNativeOn;
            public string luaBackend = "";  // Lua 后端名（LuaProfilerBackend.Current.Name；"None"=未接入）
            public bool mikuDeepOn;          // Lua 深度采样是否开启（字段名沿用 v1 格式：META 里的 mikuDeep=）
            public bool mikuHookReady;       // Lua Hook 是否就绪（META 里的 hookReady=）

            // 采样拓扑（Editor 本地 vs 真机连接）——写进 META，供分析时区分真机/编辑器数据并校准 caveat
            public string captureMode = "editor";     // "editor" | "device"
            public string connectionName = "";         // 连接目标标识（真机模式，如 "Android Player(...)"）
            public string deviceEndpoint = "";          // 真机传输端点（当前为 adb:<serial>）
            public bool countersFromFrameData = false;  // true=内存/渲染计数器从设备帧数据取（真机）；false=ProfilerRecorder（本地）

            // Editor/真机无上限模式的分段 binary log（导出时逐段 LoadProfile 累加）。null/空=走 live 帧路径。
            public List<string> rawSegmentFiles = null;
            // Lua 到达即聚合的结果（窗口侧 OnReceiveLuaSample 增量折叠）。null = 无 Lua 数据。
            public Dictionary<string, LuaAgg> luaAggPre = null;

            // 分段加载统计（无上限路径导出时填）——用于报告里显式标注 LoadProfile 失败，而非静默跳过。
            public int segTotal;
            public int segLoadFailed;
            public int segLoadEmpty;
            public long segBytesTotal;
            public long segBytesMax;

            // 逐段失败原因（导出时捕获 LoadProfile 期间的 Unity 底层 Error 日志透传，与 segLoadFailed 对应）。
            // 失败成因不止内存不足：录制期采样流污染（Begin/End 配对断裂）会让段落盘即损坏，
            // 表现同样是 LoadProfile 反序列化失败——META 必须给真实原因，不能写死猜测。
            public List<string> segFailDetails = new List<string>();
            // 导出重放期捕获的 Profiler Begin/End 配对断裂告警——损坏段回放会重现录制期告警，用于定性失败成因。
            public int replayPollutionCount;
            public string replayPollutionSample = "";
            // 录制期采样流污染统计（窗口侧 logMessageReceived 监听填充；段窗口用于圈定受损段范围）。
            // 注：勿用 AppDomain.FirstChanceException 抓被吞异常——实测 Unity 2022.3 Mono 不派发该事件（fired=false），
            // 计数恒 0 是假阴性；被吞的脚本层异常需脚本侧自行上报（如 Lua 适配器的 pcall 守卫），纯 C# 内部吞掉的异常只能靠调试器 first-chance。
            public int recordPollutionCount;
            public string recordPollutionFirstMsg = "";
            public string recordPollutionSegRange = "";

            // Editor 本地模式由运行时采集器 AIProfilerCapture 取回的界面/场景采集日志：
            // ViewOpen 打开耗时 / 点击响应 / ViewFPS 窗口帧率卡顿 / ViewNode 节点使用率 / SceneSwitch 场景切换耗时，
            // 多行文本（已去富文本，行格式 time|frame|flag|message）。导出时按 [SceneSwitch] 标记拆成两个 section。
            // 空 = 未采集到（真机模式 / 未在 Play 中录制 / 录制期无打点）。
            public string viewStats = "";
            // 脚本 VM 内存周期采样（AIProfilerCapture.ScriptMemoryMBProvider，每 5s 一发，行格式 time|frame|luaVmMB）。空 = 未采集到。
            public string luaMemTrend = "";
            // StartRecord 时 Lua 侧 Time.frameCount 基准（-1=未知）。frame 列与 FRAME_TIMELINE 帧号非同一体系，仅近似对齐。
            public long viewStatsFrameBase = -1;
        }

        #region 内部聚合结构
        private class MarkerAgg
        {
            public string name;
            public double selfMs;
            public double totalMs;   // 跨深度/帧求和，递归 marker 可能偏大，仅参考
            public long calls;
            public double gcBytes;   // marker 子树内 GC（inclusive，仅参考）
        }

        public class LuaAgg
        {
            public string name;
            public string location;
            public double selfMs;
            public double totalMs;
            public long calls;
            public long luaGc;
            public long monoGc;
        }

        private class TimelineRow
        {
            public int frame;
            public double cpuMs;
            public double gcAllocBytes;
        }

        // 真机连接模式：内存/渲染/帧时间计数器从“设备帧数据”里取（ProfilerRecorder 只读本进程=Editor，拿不到设备）。
        // 计数器在帧数据里是一种 marker：GetMarkerId(name) 拿到 id，GetCounterValueAsDouble(id) 拿到当帧值。
        private struct DeviceCounterSpec
        {
            public string name;       // Unity 计数器名（与 ProfilerRecorder 同名）
            public string category;   // "Memory" / "Render"，与本地路径分类一致
            public bool isMemory;     // 值是否为字节（用于 HumanBytes 格式化）
            public double scale;      // 单位换算；时间计数器为纳秒，×1e-6 转 ms，其余为 1
            public DeviceCounterSpec(string n, string c, bool mem, double s)
            {
                name = n; category = c; isMemory = mem; scale = s;
            }
        }

        private static readonly DeviceCounterSpec[] DeviceCounterSpecs =
        {
            new DeviceCounterSpec("Total Reserved Memory", "Memory", true, 1),
            new DeviceCounterSpec("Total Used Memory", "Memory", true, 1),
            new DeviceCounterSpec("GC Reserved Memory", "Memory", true, 1),
            new DeviceCounterSpec("GC Used Memory", "Memory", true, 1),
            new DeviceCounterSpec("Gfx Used Memory", "Memory", true, 1),
            new DeviceCounterSpec("GC Allocated In Frame", "Memory", true, 1),
            new DeviceCounterSpec("Draw Calls Count", "Render", false, 1),
            new DeviceCounterSpec("SetPass Calls Count", "Render", false, 1),
            new DeviceCounterSpec("Batches Count", "Render", false, 1),
            new DeviceCounterSpec("Triangles Count", "Render", false, 1),
            new DeviceCounterSpec("Vertices Count", "Render", false, 1),
            // 帧耗时计数器（纳秒→ms）——真机这两项才是真实 timing，归到 Render 类随 GPU section 一并输出
            new DeviceCounterSpec("CPU Total Frame Time", "Render", false, 1e-6),
            new DeviceCounterSpec("GPU Frame Time", "Render", false, 1e-6),
        };

        private class CounterAccum
        {
            public DeviceCounterSpec spec;
            // markerId 解析态：-2=未解析(下帧重试)；-3=解析/读取失败已禁用；>=0=有效 marker id
            public int markerId = -2;
            public double min = double.MaxValue, max = double.MinValue, sum, last;
            public int count;
            // 头/尾窗口（泄漏斜率）：头窗口累加前 N 帧；尾窗口用环形缓冲保留最后 N 帧。
            // 仅 count >= 2N（头尾不重叠）时输出趋势，短录制不给。
            public double headSum;
            public int headCount;
            public double[] tailRing;
            public int tailIndex;
            public int tailCount;
        }
        #endregion

        public static string Export(CaptureResult r)
        {
            if (r == null)
            {
                return null;
            }

            // ---- 1. 遍历 Unity 原生帧数据：C# 热点 + GC + 时间线 ----
            var csAgg = new Dictionary<string, MarkerAgg>();        // 按 marker 名聚合（合并视图下节点数已很少）
            var gcByMarker = new Dictionary<string, double>();      // GC.Alloc 归因到其父 marker 名（不再用昂贵的 GetItemPath）
            var timeline = new List<TimelineRow>();
            double totalGcBytes = 0;
            int luaNativeMarkerCount = 0;
            int walkedFrames = 0;
            // 真机模式：从设备帧数据累计内存/渲染/帧时间计数器；本地模式为 null（仍用 window 传入的 ProfilerRecorder 结果）
            CounterAccum[] deviceCounters = null;
            if (r.countersFromFrameData)
            {
                deviceCounters = new CounterAccum[DeviceCounterSpecs.Length];
                for (int i = 0; i < DeviceCounterSpecs.Length; i++)
                {
                    deviceCounters[i] = new CounterAccum { spec = DeviceCounterSpecs[i] };
                }
            }
            try
            {
                if (r.rawSegmentFiles != null && r.rawSegmentFiles.Count > 0)
                {
                    // 无上限模式：逐段 LoadProfile，每段 walk 其 ≤2000 帧，累加进同一组聚合字典。
                    // （LoadProfile 不会突破 2000 帧上限——Unity 官方限制，故必须分段加载，见录制侧分段轮转。）
                    TryWidenFrameWindow();
                    r.segTotal = r.rawSegmentFiles.Count;
                    int frameBase = 0;
                    // LoadProfile 失败的底层原因（如 "Deserializer encountered error"）只进 Editor.log，
                    // API 仅返回 bool——挂日志回调把真实原因透传进 META，报告不再写死"段过大/内存不足"的猜测。
                    // 损坏段回放还会重现录制期的 Begin/End 配对断裂告警，单独计数用于定性失败成因（采样流污染）。
                    string segLoadError = null;
                    Application.LogCallback segLogCapture = (condition, stackTrace, logType) =>
                    {
                        if (string.IsNullOrEmpty(condition) || condition.StartsWith("[AIProfiler]", StringComparison.Ordinal))
                        {
                            return;
                        }
                        if (condition.IndexOf("Profiler.EndSample", StringComparison.Ordinal) >= 0 ||
                            condition.IndexOf("Profiler.BeginSample", StringComparison.Ordinal) >= 0)
                        {
                            r.replayPollutionCount++;
                            if (string.IsNullOrEmpty(r.replayPollutionSample))
                            {
                                r.replayPollutionSample = OneLine(condition, 300);
                            }
                            return;
                        }
                        if (segLoadError == null &&
                            (logType == LogType.Error || logType == LogType.Exception || logType == LogType.Assert))
                        {
                            segLoadError = OneLine(condition, 300);
                        }
                    };
                    Application.logMessageReceived += segLogCapture;
                    try
                    {
                        for (int si = 0; si < r.rawSegmentFiles.Count; si++)
                        {
                            string seg = r.rawSegmentFiles[si];
                            if (string.IsNullOrEmpty(seg) || !File.Exists(seg))
                            {
                                r.segLoadFailed++;
                                r.segFailDetails.Add((string.IsNullOrEmpty(seg) ? "(空路径)" : Path.GetFileName(seg)) + ": 分段文件缺失");
                                Debug.LogWarning("[AIProfiler] 分段文件缺失，跳过: " + seg);
                                continue;
                            }
                            long segBytes = 0;
                            try
                            {
                                segBytes = new FileInfo(seg).Length;
                                r.segBytesTotal += segBytes;
                                if (segBytes > r.segBytesMax)
                                {
                                    r.segBytesMax = segBytes;
                                }
                            }
                            catch
                            {
                                // 体积只用于诊断，读取失败不影响 LoadProfile 尝试。
                            }
                            segLoadError = null;
                            bool loaded = ProfilerDriver.LoadProfile(seg, false);
                            if (!loaded)
                            {
                                r.segLoadFailed++;
                                string reason = segLoadError ?? "未捕获底层错误日志（可能段过大/内存不足，详见 Editor.log）";
                                r.segFailDetails.Add(string.Format("{0} ({1}): {2}",
                                    Path.GetFileName(seg), HumanBytes(segBytes), reason));
                                Debug.LogWarning(string.Format("[AIProfiler] LoadProfile 失败，跳过: {0} ({1}) — {2}",
                                    seg, HumanBytes(segBytes), reason));
                                continue;
                            }
                            int segFirst = ProfilerDriver.firstFrameIndex;
                            int segLast = ProfilerDriver.lastFrameIndex;
                            int w = WalkUnityFrames(segFirst, segLast, csAgg, gcByMarker, timeline,
                                ref totalGcBytes, ref luaNativeMarkerCount, deviceCounters, frameBase);
                            if (w <= 0)
                            {
                                r.segLoadEmpty++;
                                Debug.LogWarning(string.Format(
                                    "[AIProfiler] LoadProfile 成功但分段没有可遍历帧: {0} ({1}), frameIndex={2}..{3}",
                                    seg, HumanBytes(segBytes), segFirst, segLast));
                            }
                            walkedFrames += w;
                            frameBase += w;
                            // 走完即释放本段帧缓冲，避免逐段 LoadProfile 把已加载帧累积到内存耗尽
                            // （实测 33GB 段加载后紧接的段就 LoadProfile 失败）。聚合结果已落 csAgg/timeline，清缓冲不丢数据。
                            ProfilerDriver.ClearAllFrames();
                        }
                    }
                    finally
                    {
                        Application.logMessageReceived -= segLogCapture;
                    }
                    // 段路径下用累计帧数表达帧区间（live first/last 仅是录制尾部，不代表整段）
                    r.firstFrame = walkedFrames > 0 ? 0 : -1;
                    r.lastFrame = walkedFrames > 0 ? walkedFrames - 1 : -1;
                }
                else
                {
                    walkedFrames = WalkUnityFrames(r.firstFrame, r.lastFrame, csAgg, gcByMarker, timeline,
                        ref totalGcBytes, ref luaNativeMarkerCount, deviceCounters);
                }
                // 把按父 marker 归因的 GC 回填到 CS 热点 gc 列（免逐节点再读 gc 列）
                foreach (var m in csAgg.Values)
                {
                    double g;
                    if (gcByMarker.TryGetValue(m.name, out g))
                    {
                        m.gcBytes = g;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AIProfiler] 遍历 Unity 帧数据失败: " + e.Message);
            }

            // 真机模式：把从帧数据累计的设备计数器materialize成 CounterStat，覆盖 r.counters（window 在真机模式下未填 ProfilerRecorder）
            if (r.countersFromFrameData && deviceCounters != null)
            {
                r.counters = BuildCounterStats(deviceCounters);
            }

            // ---- 2. Lua 采样：窗口侧到达即聚合的结果（无后端 / 无数据时为空字典） ----
            Dictionary<string, LuaAgg> luaAgg = r.luaAggPre ?? new Dictionary<string, LuaAgg>();

            // ---- 2.5 估算"插桩自身"占比（信噪比体检，写进 META 供分析时校准）----
            double csNoiseSelf = 0, csTotalSelf = 0;
            foreach (var m in csAgg.Values)
            {
                csTotalSelf += m.selfMs;
                if (IsInstrumentCsMarker(m.name))
                {
                    csNoiseSelf += m.selfMs;
                }
            }
            double luaNoiseSelf = 0, luaTotalSelf = 0;
            foreach (var a in luaAgg.Values)
            {
                luaTotalSelf += a.selfMs;
                if (IsInstrumentLua(a))
                {
                    luaNoiseSelf += a.selfMs;
                }
            }
            double csNoiseShare = csTotalSelf > 0 ? csNoiseSelf / csTotalSelf : 0;
            double luaNoiseShare = luaTotalSelf > 0 ? luaNoiseSelf / luaTotalSelf : 0;

            // ---- 3. 拼装文本 ----
            var sb = new StringBuilder(1 << 18);
            WriteMeta(sb, r, walkedFrames, csAgg.Count, luaAgg.Count, luaNativeMarkerCount,
                totalGcBytes, csNoiseShare, luaNoiseShare);
            WriteFrameTimeline(sb, timeline);
            WriteCsHotspots(sb, csAgg);
            WriteLuaHotspots(sb, luaAgg, r.mikuDeepOn);
            WriteGpu(sb, r);
            WriteMemory(sb, r);
            WriteGc(sb, gcByMarker, timeline, luaAgg);
            WriteViewStats(sb, r);
            WriteLuaMemTrend(sb, r);

            // ---- 4. 落地 ----
            string dir = Path.Combine(Application.dataPath, "ProfilerLogs");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string fileName = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss") + ".txt";
            string fullPath = Path.Combine(dir, fileName);
            File.WriteAllText(fullPath, sb.ToString(), new UTF8Encoding(false));
            AssetDatabase.Refresh();

            string assetRel = "Assets/ProfilerLogs/" + fileName;
            Debug.Log(string.Format(
                "<color=#00ff00>[AIProfiler] Export For AI 完成</color>\n{0}\n相对路径: {1}\n用 /profiler-analysis 分析此文件。",
                fullPath, assetRel));
            return fullPath;
        }

        #region Unity 原生帧数据遍历
        // 加载分段 .raw 前尽量把帧窗口拉到上限（默认 300，硬顶 2000）。跨版本 SetMaxFrameHistoryLength 可能 internal，用反射兜底。
        private static void TryWidenFrameWindow()
        {
            try
            {
                var mi = typeof(ProfilerDriver).GetMethod("SetMaxFrameHistoryLength",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(int) }, null);
                if (mi != null)
                {
                    mi.Invoke(null, new object[] { 2000 });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AIProfiler] SetMaxFrameHistoryLength 反射失败（帧窗口保持默认）: " + e.Message);
            }
        }

        // 线性扫描的栈帧：raw 样本按深度优先顺序存储，用 childrenCount 还原树形，
        // self 耗时 = 自身 inclusive − 直接子级 inclusive 之和，在子树收尾（出栈）时结算。
        private struct RawStackEntry
        {
            public int remaining;         // 尚未收尾的直接子样本数
            public double childrenTime;   // 已完成直接子级 inclusive 之和（算 self 用）
            public double timeMs;         // 自身 inclusive
            public RawMarkerInfo info;    // null = 线程根（不聚合、不作 GC 归因父）
        }

        // markerId 首次解析出的分类结果，同一份 capture 内复用（消灭逐样本 GetItemName 字符串分配）
        private class RawMarkerInfo
        {
            public string name;
            public bool isLua;
            public bool isGcAlloc;
            public MarkerAgg agg;         // 常规 C# marker 的聚合项；lua / GC.Alloc / 空名为 null
        }

        // frameLabelBase: <0 用原始帧号（单段 live 路径，保持旧行为）；>=0 用 base+段内偏移连续编号（多段累加路径）。
        // 实现说明：用 RawFrameDataView 线性扫描主线程原始样本流，而非 GetHierarchyFrameDataView 合并层级视图——
        // 后者每帧要在原生侧对全部样本按名合并建树，Deep Profiling 下（百万级样本/帧）单帧可达几十秒，是导出慢的大头；
        // 线性扫描 + markerId→聚合项缓存后，每样本只剩 3~4 次 int/double interop、零字符串分配。
        private static int WalkUnityFrames(int firstFrame, int lastFrame,
            Dictionary<string, MarkerAgg> csAgg, Dictionary<string, double> gcByMarker,
            List<TimelineRow> timeline, ref double totalGcBytes, ref int luaNativeMarkerCount,
            CounterAccum[] deviceCounters, int frameLabelBase = -1)
        {
            int walked = 0;
            if (lastFrame < firstFrame || firstFrame < 0)
            {
                return 0;
            }

            int total = Mathf.Max(1, lastFrame - firstFrame + 1);
            // markerId 在同一份已加载 capture 内稳定；本方法每次调用（= 每段 LoadProfile 之后）新建，避免跨段 id 错位
            var markerCache = new Dictionary<int, RawMarkerInfo>(4096);
            var stack = new RawStackEntry[256];

            for (int frame = firstFrame; frame <= lastFrame; frame++)
            {
                if ((frame - firstFrame) % PROGRESS_UPDATE_INTERVAL == 0 &&
                    EditorUtility.DisplayCancelableProgressBar("AI Profiler 导出",
                        string.Format("解析帧 {0}/{1}（可取消，已解析的会照常导出）", frame - firstFrame + 1, total),
                        (float)(frame - firstFrame) / total))
                {
                    break; // 用户取消：保留已解析数据
                }

                using (var raw = AcquireMainThreadRawView(frame))
                {
                    if (raw == null || !raw.valid || raw.sampleCount <= 0)
                    {
                        continue;
                    }
                    walked++;

                    // 真机模式：从当帧数据取计数器（内存/渲染/帧时间）。本地模式 deviceCounters==null 跳过。
                    if (deviceCounters != null)
                    {
                        FeedDeviceCounters(raw, deviceCounters);
                    }

                    if (stack.Length < raw.maxDepth + 2)
                    {
                        stack = new RawStackEntry[Mathf.NextPowerOfTwo(raw.maxDepth + 2)];
                    }

                    // 样本 0 = 线程根，其 inclusive 即当帧主线程总耗时（与旧合并视图 root columnTotalTime 同口径）
                    double frameCpu = raw.GetSampleTimeMs(0);
                    double frameGcBytes = 0;
                    stack[0] = new RawStackEntry
                    {
                        remaining = raw.GetSampleChildrenCount(0),
                        timeMs = frameCpu,
                        info = null
                    };
                    int sp = 1;

                    int scanEnd = Mathf.Min(raw.sampleCount, RAW_SAMPLE_GUARD_PER_FRAME);
                    for (int i = 1; i < scanEnd && sp > 0; i++)
                    {
                        int childCount = raw.GetSampleChildrenCount(i);
                        double timeMs = raw.GetSampleTimeMs(i);
                        int markerId = raw.GetSampleMarkerId(i);

                        RawMarkerInfo info;
                        if (!markerCache.TryGetValue(markerId, out info))
                        {
                            info = ResolveMarker(raw, markerId, csAgg);
                            markerCache[markerId] = info;
                        }

                        if (info.isGcAlloc)
                        {
                            // GC.Alloc 样本：分配字节在 metadata[0]，归因到“父 marker”（栈顶）
                            double gc = raw.GetSampleMetadataCount(i) > 0 ? raw.GetSampleMetadataAsLong(i, 0) : 0;
                            if (gc > 0)
                            {
                                frameGcBytes += gc;
                                var parentInfo = stack[sp - 1].info;
                                if (parentInfo != null && parentInfo.name.Length > 0)
                                {
                                    double cur;
                                    gcByMarker.TryGetValue(parentInfo.name, out cur);
                                    gcByMarker[parentInfo.name] = cur + gc;
                                }
                            }
                        }
                        else if (info.isLua)
                        {
                            luaNativeMarkerCount++; // 原生 lua marker 计数即可；权威 Lua 数据走 Miku
                        }
                        else if (info.agg != null)
                        {
                            info.agg.totalMs += timeMs;
                            info.agg.calls++;
                        }

                        if (childCount > 0)
                        {
                            stack[sp++] = new RawStackEntry { remaining = childCount, timeMs = timeMs, info = info };
                        }
                        else
                        {
                            // 叶子：self = inclusive；随后向父级冒泡“子树完成”，收尾节点结算 self
                            if (info.agg != null)
                            {
                                info.agg.selfMs += timeMs;
                            }
                            double completed = timeMs;
                            while (sp > 0)
                            {
                                stack[sp - 1].childrenTime += completed;
                                if (--stack[sp - 1].remaining > 0)
                                {
                                    break;
                                }
                                sp--;
                                var done = stack[sp];
                                if (done.info != null && done.info.agg != null)
                                {
                                    done.info.agg.selfMs += Math.Max(0, done.timeMs - done.childrenTime);
                                }
                                completed = done.timeMs;
                            }
                        }
                    }

                    // guard 截断/异常数据兜底：栈上未收尾节点按已累计子级时间结算 self（正常路径此时 sp 已为 0）
                    while (sp > 0)
                    {
                        sp--;
                        var left = stack[sp];
                        if (left.info != null && left.info.agg != null)
                        {
                            left.info.agg.selfMs += Math.Max(0, left.timeMs - left.childrenTime);
                        }
                    }

                    totalGcBytes += frameGcBytes;
                    int frameLabel = frameLabelBase >= 0 ? frameLabelBase + (frame - firstFrame) : frame;
                    timeline.Add(new TimelineRow { frame = frameLabel, cpuMs = frameCpu, gcAllocBytes = frameGcBytes });
                }
            }
            EditorUtility.ClearProgressBar();
            return walked;
        }

        // 取当帧主线程的 raw 视图；找不到名为 "Main Thread" 的线程时退回线程 0；该帧无数据返回 null。
        internal static RawFrameDataView AcquireMainThreadRawView(int frame)
        {
            RawFrameDataView thread0 = null;
            for (int t = 0; t < 64; t++)
            {
                RawFrameDataView v;
                try
                {
                    v = ProfilerDriver.GetRawFrameDataView(frame, t);
                }
                catch (Exception)
                {
                    v = null;
                }
                if (v == null || !v.valid)
                {
                    if (v != null)
                    {
                        v.Dispose();
                    }
                    break; // 线程索引连续，首个无效即到头
                }
                if (v.threadName == "Main Thread")
                {
                    if (thread0 != null)
                    {
                        thread0.Dispose();
                    }
                    return v;
                }
                if (t == 0)
                {
                    thread0 = v;
                }
                else
                {
                    v.Dispose();
                }
            }
            return thread0;
        }

        // markerId 首次出现时解析名字并分类；常规 C# marker 直接挂上（跨帧共享的）聚合项，热路径免字典二跳。
        private static RawMarkerInfo ResolveMarker(RawFrameDataView raw, int markerId, Dictionary<string, MarkerAgg> csAgg)
        {
            string name;
            try
            {
                name = raw.GetMarkerName(markerId) ?? string.Empty;
            }
            catch (Exception)
            {
                name = string.Empty; // invalidMarkerId 等异常样本：不聚合，仅维持树形推进
            }
            var info = new RawMarkerInfo { name = name };
            if (name.Length == 0)
            {
                return info;
            }
            if (name == "GC.Alloc")
            {
                info.isGcAlloc = true;
            }
            else if (IsLuaMarker(name))
            {
                info.isLua = true;
            }
            else
            {
                MarkerAgg m;
                if (!csAgg.TryGetValue(name, out m))
                {
                    m = new MarkerAgg { name = name };
                    csAgg[name] = m;
                }
                info.agg = m;
            }
            return info;
        }

        private static bool IsLuaMarker(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            if (name.IndexOf(".lua", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (name.IndexOf("[lua]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (name.IndexOf("lua:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }

        // 真机模式：从一帧的帧数据视图取各计数器当帧值并累计 min/avg/max/last。
        // 计数器在帧数据里以 marker 形式存在：GetMarkerId(name) 解析 id，GetCounterValueAsDouble(id) 取值
        // （两者均为 FrameDataView 基类 API，Hierarchy/Raw 视图通用）。
        // 防御性：marker 不存在/读取异常时，对该计数器永久禁用（标 -3），最终 count=0 → 输出 NO DATA。
        private static void FeedDeviceCounters(FrameDataView fdv, CounterAccum[] accums)
        {
            for (int i = 0; i < accums.Length; i++)
            {
                var a = accums[i];
                if (a.markerId == -3)
                {
                    continue; // 已禁用
                }
                if (a.markerId == -2)
                {
                    try
                    {
                        a.markerId = fdv.GetMarkerId(a.spec.name);
                    }
                    catch
                    {
                        a.markerId = -3;
                        continue;
                    }
                }
                if (a.markerId < 0)
                {
                    // GetMarkerId 返回 invalidMarkerId（负）——该帧无此计数器，下帧再试（保持 -2 重试 vs 直接禁用：
                    // 计数器一般每帧都在，这里直接禁用避免反复异常；若首帧偶发缺失可改回 -2）
                    a.markerId = -3;
                    continue;
                }
                double v;
                try
                {
                    v = fdv.GetCounterValueAsDouble(a.markerId);
                }
                catch
                {
                    a.markerId = -3; // 读取异常：永久禁用，避免每帧抛异常
                    continue;
                }
                v *= a.spec.scale;
                if (v < a.min) a.min = v;
                if (v > a.max) a.max = v;
                a.sum += v;
                a.last = v;
                a.count++;
                if (a.headCount < TREND_WINDOW_SAMPLES)
                {
                    a.headSum += v;
                    a.headCount++;
                }
                if (a.tailRing == null)
                {
                    a.tailRing = new double[TREND_WINDOW_SAMPLES];
                }
                a.tailRing[a.tailIndex] = v;
                a.tailIndex = (a.tailIndex + 1) % TREND_WINDOW_SAMPLES;
                if (a.tailCount < TREND_WINDOW_SAMPLES)
                {
                    a.tailCount++;
                }
            }
        }

        private static List<CounterStat> BuildCounterStats(CounterAccum[] accums)
        {
            var list = new List<CounterStat>(accums.Length);
            foreach (var a in accums)
            {
                // 帧时间计数器换算成 ms，标签加后缀以示单位
                string label = a.spec.scale != 1 ? a.spec.name + "(ms)" : a.spec.name;
                var cs = new CounterStat
                {
                    label = label,
                    category = a.spec.category,
                    isMemory = a.spec.isMemory
                };
                if (a.count > 0)
                {
                    cs.valid = true;
                    cs.min = a.min;
                    cs.max = a.max;
                    cs.avg = a.sum / a.count;
                    cs.last = a.last;
                    cs.sampleCount = a.count;
                    // 头尾窗口不重叠才给趋势（count >= 2N）；真机长录制基本恒满足
                    if (a.count >= TREND_WINDOW_SAMPLES * 2 && a.headCount > 0 && a.tailCount > 0)
                    {
                        double tailSum = 0;
                        for (int ti = 0; ti < a.tailCount; ti++)
                        {
                            tailSum += a.tailRing[ti];
                        }
                        cs.headAvg = a.headSum / a.headCount;
                        cs.tailAvg = tailSum / a.tailCount;
                        cs.trendValid = true;
                    }
                }
                list.Add(cs);
            }
            return list;
        }
        #endregion

        #region Lua 聚合
        /// <summary>把后端回调的一个采样节点折叠进按 name 聚合的字典（窗口侧到达即调用）。</summary>
        public static void AggregateLua(LuaSampleNode node, Dictionary<string, LuaAgg> dict)
        {
            string name = node.name;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            LuaAgg a;
            if (!dict.TryGetValue(name, out a))
            {
                a = new LuaAgg { name = name, location = ParseLuaLocation(name) };
                dict[name] = a;
            }
            a.selfMs += node.selfMs;
            a.totalMs += node.totalMs;
            a.calls += node.calls;
            a.luaGc += node.luaGcBytes;
            a.monoGc += node.monoGcBytes;
        }

        // Miku 名格式: "[lua]: <file>&<func>:<line>" —— 解析出 file:line，失败返回 "-"
        private static string ParseLuaLocation(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || name.Length < 6 || name.Substring(0, 6) != "[lua]:")
                {
                    return "-";
                }
                var arr = name.Split(new[] { ',' }, 2);
                if (arr.Length != 2)
                {
                    return "-";
                }
                string rest = arr[1];
                var arr2 = rest.Split(new[] { '&' }, 2);
                if (arr2.Length != 2)
                {
                    return "-";
                }
                string file = arr2[0].Trim();
                var lineParts = arr2[1].Split(new[] { ':' }, 2);
                if (lineParts.Length == 2)
                {
                    return file + ":" + lineParts[1].Trim();
                }
                return file;
            }
            catch
            {
                return "-";
            }
        }
        #endregion

        #region 噪声分类（信噪比体检）
        // "测量工具自身"的 marker——多套插桩互相计量，常占据榜首淹没真实热点。
        // 与 analyze_profiler.py 的分类保持一致（脚本侧通过 profiler_config.json 配置同样的项目特征）。
        /// <summary>工程自带插桩的 C# marker 特征（子串，不区分大小写），计入"测量工具自身"噪声。按项目在启动时追加。</summary>
        public static readonly List<string> ExtraInstrumentCsMarkers = new List<string>();
        /// <summary>工程自带 Lua 插桩的 location 特征（子串，不区分大小写）。</summary>
        public static readonly List<string> ExtraInstrumentLuaLocations = new List<string>();
        private static bool IsInstrumentCsMarker(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            if (string.Equals(name, "EditorLoop", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (name.IndexOf("MikuLuaProfiler", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            for (int i = 0; i < ExtraInstrumentCsMarkers.Count; i++)
            {
                if (name.IndexOf(ExtraInstrumentCsMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsInstrumentLua(LuaAgg a)
        {
            if (a == null)
            {
                return false;
            }
            string loc = a.location ?? string.Empty;
            string nm = a.name ?? string.Empty;
            for (int i = 0; i < ExtraInstrumentLuaLocations.Count; i++)
            {
                if (loc.IndexOf(ExtraInstrumentLuaLocations[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            if (loc.IndexOf("Tolua/misc/misc", StringComparison.OrdinalIgnoreCase) >= 0) // Miku reimport 注桩位置
            {
                return true;
            }
            if (nm.IndexOf("reimport", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }
        #endregion

        #region 文本拼装
        /// <summary>把多行日志压成单行并截断，供 META / 状态栏 / Console 引用（录制侧窗口也复用）。</summary>
        internal static string OneLine(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            s = s.Replace("\r", "").Replace('\n', ' ');
            if (s.Length > maxLen)
            {
                s = s.Substring(0, maxLen) + "…";
            }
            return s;
        }

        private static void WriteMeta(StringBuilder sb, CaptureResult r, int walkedFrames,
            int csCount, int luaCount, int luaNativeMarkerCount, double totalGcBytes,
            double csNoiseShare, double luaNoiseShare)
        {
            bool unityHasData = walkedFrames > 0;
            bool mikuHasData = luaCount > 0;

            sb.AppendLine("============================================================");
            sb.AppendLine(" AI Profiler - Export For AI");
            sb.AppendLine("============================================================");
            sb.AppendLine("Format      : " + FORMAT_TAG);
            sb.AppendLine("Export Time : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(string.Format("Frame Range : {0}..{1} (walked {2} frames)", r.firstFrame, r.lastFrame, walkedFrames));
            sb.AppendLine();
            bool isDevice = r.captureMode == "device";
            sb.AppendLine("[Target]");
            sb.AppendLine(string.Format("  Capture Mode : {0}", isDevice ? "device (真机连接采样)" : "editor (Editor 本地采样)"));
            if (isDevice)
            {
                sb.AppendLine(string.Format("  Connection   : {0}", string.IsNullOrEmpty(r.connectionName) ? "(未知/未记录)" : r.connectionName));
                sb.AppendLine(string.Format("  Transport    : {0}", string.IsNullOrEmpty(r.deviceEndpoint) ? "(未连接真机)" : r.deviceEndpoint));
            }
            sb.AppendLine();
            sb.AppendLine("[Sources]");
            sb.AppendLine(string.Format("  Unity native profiler : {0} (deepProfiling={1})",
                unityHasData ? "captured" : "NO DATA", r.deepProfilingOn));
            if (r.segTotal > 0)
            {
                sb.AppendLine(string.Format(
                    "  Native segments: total={0}, failed={1}, empty={2}, max={3}, bytes={4}",
                    r.segTotal, r.segLoadFailed, r.segLoadEmpty,
                    HumanBytes(r.segBytesMax), HumanBytes(r.segBytesTotal)));
            }
            if (r.segLoadFailed > 0)
            {
                sb.AppendLine(string.Format(
                    "  ⚠ 分段加载: {0}/{1} 段 LoadProfile 失败被跳过 → 原生数据残缺，报告必须标灰并重采。逐段原因（捕获自 Unity 底层日志）：",
                    r.segLoadFailed, r.segTotal));
                for (int i = 0; i < r.segFailDetails.Count && i < MAX_SEG_FAIL_DETAILS; i++)
                {
                    sb.AppendLine("    - " + r.segFailDetails[i]);
                }
                if (r.segFailDetails.Count > MAX_SEG_FAIL_DETAILS)
                {
                    sb.AppendLine(string.Format("    - …其余 {0} 段略", r.segFailDetails.Count - MAX_SEG_FAIL_DETAILS));
                }
            }
            if (r.segLoadEmpty > 0)
            {
                sb.AppendLine(string.Format(
                    "  ⚠ 分段空帧: {0}/{1} 段 LoadProfile 成功但 walked 0（段过短/未 flush/文件异常）→ 原生数据残缺，报告必须标灰并重采",
                    r.segLoadEmpty, r.segTotal));
            }
            if (r.recordPollutionCount > 0)
            {
                sb.AppendLine(string.Format(
                    "  ⚠ 采样流污染(录制期): {0} 条 Profiler Begin/End 配对断裂告警{1} → 污染窗口内落盘的段大概率损坏。" +
                    "注意：告警 Previous samples 的尾部通常只是帧内最后执行的 Update（校验点旁观者），未必是泄漏源；" +
                    "定位泄漏源：污染现场用 Unity Profiler 窗口 Record(Deep) 复现后执行菜单 Window/Analysis/AI Profiler Dump Suspect Frames，" +
                    "在 dump 中找\"吞掉后续兄弟系统的异常长样本\"即泄漏方法（再查其内部手动 BeginSample 的早退路径）",
                    r.recordPollutionCount,
                    string.IsNullOrEmpty(r.recordPollutionSegRange) ? "" : "（约 " + r.recordPollutionSegRange + "）"));
                if (!string.IsNullOrEmpty(r.recordPollutionFirstMsg))
                {
                    sb.AppendLine("    首条: " + r.recordPollutionFirstMsg);
                }
            }
            if (r.replayPollutionCount > 0)
            {
                sb.AppendLine(string.Format(
                    "  ⚠ 采样流污染(重放期): 逐段回放触发 {0} 条 Begin/End 配对断裂告警 → 失败段成因指向录制期采样流污染，而非段过大/内存不足",
                    r.replayPollutionCount));
                if (!string.IsNullOrEmpty(r.replayPollutionSample))
                {
                    sb.AppendLine("    样例: " + r.replayPollutionSample);
                }
            }
            sb.AppendLine(string.Format("  Native Deep Lua markers: {0} markers (deepLuaNative={1})",
                luaNativeMarkerCount, r.deepLuaNativeOn));
            sb.AppendLine(string.Format("  Lua profiler (Lua VM, backend={3}): {0} unique funcs (mikuDeep={1}, hookReady={2})",
                mikuHasData ? luaCount.ToString() : "NO DATA", r.mikuDeepOn, r.mikuHookReady,
                string.IsNullOrEmpty(r.luaBackend) ? "None" : r.luaBackend));
            int viewStatsLines = CountLines(r.viewStats);
            sb.AppendLine(string.Format("  UI ViewStats (AIProfilerCapture): {0}",
                viewStatsLines > 0 ? viewStatsLines + " lines (见 VIEW_STATS / SCENE_SWITCH section)" : "NO DATA"));
            int luaMemLines = CountLines(r.luaMemTrend);
            sb.AppendLine(string.Format("  Lua VM mem samples: {0}",
                luaMemLines > 0 ? luaMemLines + " samples (见 LUA_MEM_TREND section)" : "NO DATA"));
            sb.AppendLine();
            sb.AppendLine("[Units] cpu/gpu time = millisecond(ms) ; gc/memory = byte(B)");
            if (isDevice)
            {
                sb.AppendLine(r.mikuDeepOn
                    ? "[Note]  真机连接采样：C#/GPU/内存/GC 来自设备帧数据，Lua 来自 Lua 后端远程回传。Lua 插桩仍放大 Lua 绝对耗时，看相对占比。"
                    : "[Note]  真机原生安全模式：仅采 C#/GPU/内存/GC；Lua 采样被主动禁用（或未接入 Lua 后端），Lua NO DATA 是预期结果。");
                sb.AppendLine("        self* 不含子级；total* 含子级（跨帧/递归求和仅参考）。");
                sb.AppendLine("        GC 归因不含编辑器工件（FileUtil.GetPhysicalPath / UnityEditor.* 等真机本无）——出现的都是真机真实分配。");
                sb.AppendLine("        GPU/渲染计数器来自设备相对可信（DrawCall/三角面/GPU Frame Time）；C# 为设备 marker 层级，完整 deep C# 取决于打包是否开 deep profiling。");
                sb.AppendLine("        CS_HOTSPOTS 已剔除 lua-marker；权威 Lua 数据见 LUA_HOTSPOTS（来自 Lua 后端）。");
            }
            else
            {
                sb.AppendLine("[Note]  Unity Deep + Lua 后端 hook（工程自带的冲突插桩已关），插桩仍放大绝对耗时，分析看相对占比。");
                sb.AppendLine("        self* 不含子级；total* 含子级（跨帧/递归求和仅参考）。");
                sb.AppendLine("        Editor 内 GPU 逐 marker 不可靠，GPU section 以渲染计数器为主。");
                sb.AppendLine("        CS_HOTSPOTS 已剔除 lua-marker；权威 Lua 数据见 LUA_HOTSPOTS（来自 Lua 后端）。");
            }
            sb.AppendLine(string.Format("[Total] GC.Alloc over recording = {0}", HumanBytes(totalGcBytes)));
            sb.AppendLine(string.Format(
                "[Health] 插桩自身占比：C# self~{0:f0}% / Lua self~{1:f0}%",
                csNoiseShare * 100, luaNoiseShare * 100));
            sb.AppendLine("         Lua 数据来自 Lua 后端单 hook（本面板已自动关闭工程自带的冲突插桩）；后端自身插桩仍会放大绝对耗时，看相对占比/尖刺，勿把 ms 当真机值。");
            sb.AppendLine("         · Lua 耗时/GC 用此默认采样即可（Lua VM GC 只有 Lua 后端能拿到）；");
            sb.AppendLine("         · 想要干净的 C#/引擎 CPU 榜，另采一次关掉 Lua 深度采样（仅 Unity Deep），避免 Lua 后端运行时占据 C# 榜；");
            sb.AppendLine("         · 若 Lua 占比高且榜上出现工程自带插桩的条目（deepLuaNative=True），说明冲突插桩漏关了——关掉后重采。");
            sb.AppendLine("         分析脚本(analyze_profiler.py)默认过滤上述噪声，--raw 看未过滤全貌。");
            sb.AppendLine();
        }

        private static void WriteFrameTimeline(StringBuilder sb, List<TimelineRow> timeline)
        {
            sb.AppendLine("#### SECTION: FRAME_TIMELINE ####");
            sb.AppendLine("# 每帧主线程 CPU 总耗时(root) + 当帧 GC.Alloc 合计。找尖刺帧。");
            sb.AppendLine("# fields: frame | cpuMs | gcAllocB");
            sb.AppendLine(string.Format("## TIMELINE (前 {0} 帧顺序采样) ##", MAX_TIMELINE_ROWS));
            int n = Mathf.Min(timeline.Count, MAX_TIMELINE_ROWS);
            for (int i = 0; i < n; i++)
            {
                var t = timeline[i];
                sb.AppendLine(string.Format("{0} | {1:f3} | {2:f0}", t.frame, t.cpuMs, t.gcAllocBytes));
            }
            if (timeline.Count > n)
            {
                sb.AppendLine(string.Format("... ({0} more frames omitted)", timeline.Count - n));
            }
            // 全程按 cpuMs 降序的尖刺榜——TIMELINE 是前 N 帧顺序截断，长录制时后段帧不在其中；
            // 这里对“所有走过的帧”排序取 Top，保证晚期 CPU 尖刺仍可见（与 GC 的 TOP_GC_FRAMES 对称）。
            if (timeline.Count > 0)
            {
                sb.AppendLine("## TOP_CPU_FRAMES (全程按 cpuMs 降序，覆盖 TIMELINE 截断后的尖刺) ##");
                sb.AppendLine("# fields: frame | cpuMs | gcAllocB");
                var byCpu = new List<TimelineRow>(timeline);
                byCpu.Sort((a, b) => b.cpuMs.CompareTo(a.cpuMs));
                int cn = Mathf.Min(byCpu.Count, MAX_TOP_CPU_FRAMES);
                for (int i = 0; i < cn; i++)
                {
                    var t = byCpu[i];
                    sb.AppendLine(string.Format("{0} | {1:f3} | {2:f0}", t.frame, t.cpuMs, t.gcAllocBytes));
                }
            }
            sb.AppendLine();
        }

        private static void WriteCsHotspots(StringBuilder sb, Dictionary<string, MarkerAgg> csAgg)
        {
            sb.AppendLine("#### SECTION: CS_HOTSPOTS ####");
            sb.AppendLine("# C# / 引擎 marker，按自身耗时降序（已剔除 lua marker 与 GC.Alloc）。");
            sb.AppendLine("# fields: rank | selfMs | totalMs | calls | gcAllocB(attr) | marker");
            var list = new List<MarkerAgg>(csAgg.Values);
            list.Sort((a, b) => b.selfMs.CompareTo(a.selfMs));
            int n = Mathf.Min(list.Count, MAX_HOT_ROWS);
            for (int i = 0; i < n; i++)
            {
                var m = list[i];
                sb.AppendLine(string.Format("{0} | {1:f3} | {2:f3} | {3} | {4:f0} | {5}",
                    i + 1, m.selfMs, m.totalMs, m.calls, m.gcBytes, m.name));
            }
            if (list.Count > n)
            {
                sb.AppendLine(string.Format("... ({0} more markers omitted)", list.Count - n));
            }
            sb.AppendLine();
        }

        private static void WriteLuaHotspots(StringBuilder sb, Dictionary<string, LuaAgg> luaAgg, bool mikuDeepOn)
        {
            sb.AppendLine("#### SECTION: LUA_HOTSPOTS ####");
            sb.AppendLine("# Lua 后端真实 Lua VM 数据，按自身耗时降序。luaGc 为 Lua VM GC（Unity 拿不到）。");
            sb.AppendLine("# fields: rank | selfMs | totalMs | calls | luaGcB | monoGcB | location | name");
            var list = new List<LuaAgg>(luaAgg.Values);
            list.Sort((a, b) => b.selfMs.CompareTo(a.selfMs));
            int n = Mathf.Min(list.Count, MAX_HOT_ROWS);
            for (int i = 0; i < n; i++)
            {
                var a = list[i];
                sb.AppendLine(string.Format("{0} | {1:f3} | {2:f3} | {3} | {4} | {5} | {6} | {7}",
                    i + 1, a.selfMs, a.totalMs, a.calls, a.luaGc, a.monoGc, a.location, a.name));
            }
            if (list.Count == 0)
            {
                sb.AppendLine(mikuDeepOn
                    ? "(NO DATA - Lua 后端未捕获到 Lua 采样：Editor 检查 Play/Hook；真机检查独立心跳 hookReady=True 且采样窗口覆盖目标操作)"
                    : "(NO DATA - Lua 采样已禁用或未接入 Lua 后端；原生安全模式 / 无 Lua 工程下这是预期结果)");
            }
            else if (list.Count > n)
            {
                sb.AppendLine(string.Format("... ({0} more funcs omitted)", list.Count - n));
            }
            sb.AppendLine();
        }

        private static void WriteGpu(StringBuilder sb, CaptureResult r)
        {
            bool isDevice = r.captureMode == "device";
            sb.AppendLine("#### SECTION: GPU ####");
            sb.AppendLine(isDevice
                ? "# 真机渲染计数器（来自设备帧数据，相对可信）+ GPU/CPU 帧耗时计数器。"
                : "# Editor 内 GPU 逐 marker 不可靠，这里给渲染计数器汇总 + GPU 帧耗时(best-effort)。");
            sb.AppendLine("# fields: counter | min | avg | max | last");
            bool any = false;
            foreach (var c in r.counters)
            {
                if (c.category != "Render")
                {
                    continue;
                }
                any = true;
                if (!c.valid)
                {
                    sb.AppendLine(string.Format("{0} | (NO DATA - 设备未上报该计数器)", c.label));
                    continue;
                }
                sb.AppendLine(string.Format("{0} | {1:f1} | {2:f1} | {3:f1} | {4:f1}",
                    c.label, c.min, c.avg, c.max, c.last));
            }
            if (!any)
            {
                sb.AppendLine("(无渲染计数器数据)");
            }
            if (isDevice)
            {
                // 真机帧耗时已作为 "GPU Frame Time(ms)" / "CPU Total Frame Time(ms)" 计数器在上面输出；不再用 Editor FrameTimingManager。
                sb.AppendLine("# 真机 GPU/CPU 帧耗时见上方 *(ms) 计数器行；逐 Pass/Shader 级瓶颈仍需 FrameDebugger / 真机 GPU profiler。");
            }
            else if (r.gpuTimingValid && r.gpuTimingSampleCount > 0)
            {
                sb.AppendLine(string.Format("GpuFrameTime(ms) | {0:f3} | {1:f3} | {2:f3} | (samples={3})",
                    r.gpuMinMs, r.gpuAvgMs, r.gpuMaxMs, r.gpuTimingSampleCount));
                sb.AppendLine(string.Format("CpuFrameTime(ms,avg) | {0:f3}", r.cpuTimingAvgMs));
            }
            else
            {
                sb.AppendLine("GpuFrameTime | (不可用 - Editor/平台未提供 GPU 帧计时)");
            }
            sb.AppendLine();
        }

        private static void WriteMemory(StringBuilder sb, CaptureResult r)
        {
            sb.AppendLine("#### SECTION: MEMORY ####");
            sb.AppendLine("# 内存计数器统计（录制期间逐帧采样）。");
            sb.AppendLine(string.Format(
                "# headAvg/tailAvg = 录制前/后各 {0} 样本窗口均值，trend = (tail-head)/head——持续上升是泄漏/累积信号；样本不足时为 '-'。",
                TREND_WINDOW_SAMPLES));
            sb.AppendLine("# fields: counter | min | avg | max | last | headAvg | tailAvg | trend");
            bool any = false;
            foreach (var c in r.counters)
            {
                if (c.category != "Memory")
                {
                    continue;
                }
                any = true;
                if (!c.valid)
                {
                    sb.AppendLine(string.Format("{0} | (NO DATA - 设备未上报该计数器)", c.label));
                    continue;
                }
                string headStr = "-", tailStr = "-", trendStr = "-";
                if (c.trendValid)
                {
                    headStr = HumanBytes(c.headAvg);
                    tailStr = HumanBytes(c.tailAvg);
                    if (c.headAvg > 0)
                    {
                        trendStr = string.Format("{0:+0.0;-0.0}%", (c.tailAvg - c.headAvg) / c.headAvg * 100);
                    }
                }
                sb.AppendLine(string.Format("{0} | {1} | {2} | {3} | {4} | {5} | {6} | {7}",
                    c.label, HumanBytes(c.min), HumanBytes(c.avg), HumanBytes(c.max), HumanBytes(c.last),
                    headStr, tailStr, trendStr));
            }
            if (!any)
            {
                sb.AppendLine("(无内存计数器数据)");
            }
            sb.AppendLine();
        }

        private static void WriteGc(StringBuilder sb, Dictionary<string, double> gcByMarker,
            List<TimelineRow> timeline, Dictionary<string, LuaAgg> luaAgg)
        {
            sb.AppendLine("#### SECTION: GC ####");
            sb.AppendLine("# GC 三视图：大 GC 帧 / GC.Alloc 归因 marker / Lua VM GC。");

            // 大 GC 帧 Top
            sb.AppendLine("## TOP_GC_FRAMES (frame | gcAllocB) ##");
            var byGc = new List<TimelineRow>(timeline);
            byGc.Sort((a, b) => b.gcAllocBytes.CompareTo(a.gcAllocBytes));
            int fn = Mathf.Min(byGc.Count, 30);
            for (int i = 0; i < fn; i++)
            {
                if (byGc[i].gcAllocBytes <= 0)
                {
                    break;
                }
                sb.AppendLine(string.Format("{0} | {1:f0}  ({2})", byGc[i].frame, byGc[i].gcAllocBytes, HumanBytes(byGc[i].gcAllocBytes)));
            }

            // GC.Alloc 归因 marker Top（Mono GC，按 GC.Alloc 子节点归因到其父 marker）
            sb.AppendLine("## TOP_GC_ALLOC_PATHS (mono gc; gcB | allocating marker) ##");
            var paths = new List<KeyValuePair<string, double>>(gcByMarker);
            paths.Sort((a, b) => b.Value.CompareTo(a.Value));
            int pn = Mathf.Min(paths.Count, MAX_GC_PATHS);
            for (int i = 0; i < pn; i++)
            {
                sb.AppendLine(string.Format("{0} | {1}", HumanBytes(paths[i].Value), paths[i].Key));
            }
            if (paths.Count == 0)
            {
                sb.AppendLine("(无 GC.Alloc 路径数据)");
            }

            // Lua VM GC Top（来自 Miku）
            sb.AppendLine("## TOP_LUA_VM_GC (luaGcB | location | name) ##");
            var luaList = new List<LuaAgg>(luaAgg.Values);
            luaList.Sort((a, b) => b.luaGc.CompareTo(a.luaGc));
            int ln = Mathf.Min(luaList.Count, 40);
            int written = 0;
            for (int i = 0; i < ln; i++)
            {
                if (luaList[i].luaGc <= 0)
                {
                    break;
                }
                sb.AppendLine(string.Format("{0} | {1} | {2}", HumanBytes(luaList[i].luaGc), luaList[i].location, luaList[i].name));
                written++;
            }
            if (written == 0)
            {
                sb.AppendLine("(无 Lua VM GC 数据)");
            }
            sb.AppendLine();
        }

        private static void WriteViewStats(StringBuilder sb, CaptureResult r)
        {
            bool isDevice = r.captureMode == "device";
            // 同一份采集文本按行拆成 界面(VIEW_STATS) 与 场景切换(SCENE_SWITCH) 两个 section
            var viewLines = new List<string>();
            var sceneLines = new List<string>();
            if (!string.IsNullOrEmpty(r.viewStats))
            {
                foreach (var line in r.viewStats.Split('\n'))
                {
                    if (line.IndexOf("[ProfilerUtils][SceneSwitch]", StringComparison.Ordinal) >= 0)
                    {
                        sceneLines.Add(line);
                    }
                    else if (line.Length > 0)
                    {
                        viewLines.Add(line);
                    }
                }
            }
            string frameNote = r.viewStatsFrameBase >= 0
                ? string.Format("# frame 列为运行时 Time.frameCount（StartRecord 基准={0}）；与 FRAME_TIMELINE 帧号非同一体系，仅作近似对齐。", r.viewStatsFrameBase)
                : "# frame 列为运行时 Time.frameCount；与 FRAME_TIMELINE 帧号非同一体系，仅作近似对齐。";
            string noDataReason = isDevice
                ? "(NO DATA - 真机模式暂不采集)"
                : "(NO DATA - 录制期无对应打点，或未在 Play 中 StartRecord；打点接入见 AIProfilerCapture)";

            sb.AppendLine("#### SECTION: VIEW_STATS ####");
            sb.AppendLine("# 界面打开性能统计（运行时 AIProfilerCapture 采集），按界面逐条记录：");
            sb.AppendLine("#   ViewOpen: 资源加载耗时 / 显示完成耗时 / 点击响应耗时(ms)（点击→开始加载的静默期；已合并=父界面吞并，未配对=非点击触发）");
            sb.AppendLine("#   ViewFPS : 界面打开后统计窗口内 FPS/SmallJank/Jank/BigJank/Stutter(卡顿率)/Freeze(冻结率)/Drop(降帧率)，");
            sb.AppendLine("#             PerfDog 前三帧口径；附逐帧 fps / time(ms) 序列（续行，无行首前缀）");
            sb.AppendLine("#   ViewNode: 加载完成 1s 后节点 Total(总数)/Inactive(未使用数)/InactiveRatio(未使用率)");
            sb.AppendLine("# fields: time|frame|flag|message  （flag: -=正常 !=超标；超标正文含「超过阈值/slow/N以下」等提示；(N) 后缀=相邻同名合并 N 条）");
            sb.AppendLine(frameNote);
            sb.AppendLine("# 注意: 采集自身有开销（ViewNode 全树扫描 / 日志输出），CS/LUA 榜中 AIProfilerCapture 相关条目属测量开销，勿立项优化。");
            if (viewLines.Count == 0)
            {
                sb.AppendLine(noDataReason);
            }
            else
            {
                foreach (var line in viewLines)
                {
                    sb.AppendLine(line);
                }
            }
            sb.AppendLine();

            sb.AppendLine("#### SECTION: SCENE_SWITCH ####");
            sb.AppendLine("# 场景切换耗时（SwitchScene 调用 → SwitchToSceneOver：loading 已关、场景生命周期走完，用户可感的\"切完\"）。");
            sb.AppendLine("# fields: time|frame|flag|message  （flag !=超过阈值(3000ms)；message: 场景 [来源→目标] - 切换耗时: Nms）");
            sb.AppendLine(frameNote);
            sb.AppendLine("# 超标切换按六段分解诊断（前摇/Unity场景加载/最小 loading 时长白等/业务资源/业务初始化/揭幕），先算结构性等待占比再优化真实加载。");
            if (sceneLines.Count == 0)
            {
                sb.AppendLine(noDataReason);
            }
            else
            {
                foreach (var line in sceneLines)
                {
                    sb.AppendLine(line);
                }
            }
            sb.AppendLine();
        }

        private static void WriteLuaMemTrend(StringBuilder sb, CaptureResult r)
        {
            bool isDevice = r.captureMode == "device";
            sb.AppendLine("#### SECTION: LUA_MEM_TREND ####");
            sb.AppendLine("# 脚本 VM（如 Lua）总内存周期采样（AIProfilerCapture.ScriptMemoryMBProvider；每 5s 一发 + 起止各一发）。持续上升是脚本侧泄漏/累积信号；");
            sb.AppendLine("# 与 MEMORY section 的 Mono/Native 计数器 trend 列互补（Lua VM 存量 Unity 计数器拿不到）。");
            sb.AppendLine("# fields: time|frame|luaVmMB");
            if (string.IsNullOrEmpty(r.luaMemTrend))
            {
                sb.AppendLine(isDevice
                    ? "(NO DATA - 真机模式暂不采集；脚本 VM 内存需 Editor 本地录制)"
                    : "(NO DATA - 未配置 AIProfilerCapture.ScriptMemoryMBProvider / 脚本侧未调用 RecordScriptMemory，或未在 Play 中 StartRecord)");
            }
            else
            {
                sb.AppendLine(r.luaMemTrend);
            }
            sb.AppendLine();
        }

        /// <summary>统计多行文本的行数（空串为 0）。</summary>
        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return 0;
            }
            int n = 1;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\n')
                {
                    n++;
                }
            }
            return n;
        }

        private static string HumanBytes(double bytes)
        {
            double v = Math.Abs(bytes);
            string sign = bytes < 0 ? "-" : "";
            if (v < 1024)
            {
                return string.Format("{0}{1:f0}B", sign, v);
            }
            if (v < 1024 * 1024)
            {
                return string.Format("{0}{1:f2}KB", sign, v / 1024);
            }
            if (v < 1024d * 1024 * 1024)
            {
                return string.Format("{0}{1:f2}MB", sign, v / 1024 / 1024);
            }
            return string.Format("{0}{1:f2}GB", sign, v / 1024 / 1024 / 1024);
        }
        #endregion
    }
}
#endif
