// AI Profiler — 通用运行时采集器（界面打开耗时 / 点击响应 / 开屏帧率卡顿 / 节点使用率 / 场景切换耗时 / 脚本 VM 内存趋势）。
//
// 任何 Unity 工程都能用：纯 C#，无 Editor、无 Lua、无第三方依赖。工程只需在自己的 UI / 场景流程里调下面的 Mark*/Record* 打点，
// AI Profiler 面板 StartRecord 时调用 BeginCapture()，StopRecord 时 EndCapture() 取回文本，导出成
// AI-Profiler-v1 的 VIEW_STATS / SCENE_SWITCH / LUA_MEM_TREND 三个 section。
//
// 行格式契约（分析脚本 analyze_profiler.py 按此解析，改动需同步）：
//   HH:MM:SS|frame|flag|[ProfilerUtils][<Type>] <label> [<subject>] - <message>
//     flag: "-"=正常 "!"=超标；<Type> ∈ ViewOpen / ViewFPS / ViewNode / SceneSwitch；
//     ViewFPS 的逐帧 fps/time 序列以无前缀续行附在同一条目后。
//   脚本 VM 内存样本：HH:MM:SS|frame|MB
//
// 有脚本层（Lua 等）的工程：把打点从脚本层桥到本类（见 ../../../Lua/AIProfilerCapture.lua 的纯 Lua 适配器），
// 并把 ScriptMemoryMBProvider 指到脚本 VM 的内存查询（或由脚本侧周期调用 RecordScriptMemory）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace AIProfiler
{
    public static class AIProfilerCapture
    {
        // ================= 阈值（可按项目调整） =================
        public static class Thresholds
        {
            public static double ViewResourceLoadMs = 400;    // ViewOpen 资源加载耗时超标线
            public static double ViewTotalLoadMs = 500;       // ViewOpen 显示完成耗时超标线
            public static double ClickResponseSlowMs = 2000;  // 点击→开始加载 超过即 slow
            public static double ClickStaleMs = 10000;        // 点击后超过此时长仍无界面加载，视为残留点击、不配对
            public static double ClickMergeWindowSec = 1.0;   // 配对成功后此窗口内的后续加载判为"已合并(父吞)"
            public static double FpsWindowMs = 1000;          // ViewFPS 从界面打开起统计的窗口长度
            public static int NodeTotalStd = 1500;            // ViewNode 节点总数超标线
            public static int NodeInactiveStd = 500;          // ViewNode 未使用节点数超标线
            public static double NodeInactiveRatioStd = 40;   // ViewNode 未使用率(%)超标线
            public static double SceneSwitchSlowMs = 3000;    // 场景切换超标线
            public static float ScriptMemorySampleIntervalSec = 5f;
            public static int MaxCaptureLines = 2000;         // 界面/场景日志环形容量
            public static int MaxMemorySamples = 2000;        // 内存样本容量（到顶停采，保头尾完整）
            // ViewFPS 卡顿口径（PerfDog 前三帧均值×2 突变 + 绝对阈值）
            public static double SmallJankMs = 41.67, JankMs = 83.33, BigJankMs = 125, FreezeMs = 100, JankMultiplier = 2;
            public static int SmallJankStd = 5, JankStd = 1, BigJankStd = 0;
            public static double StutterStdPct = 15, FreezeStdPct = 2, DropStdPct = 5;
        }

        // ================= 接入点 =================
        /// <summary>脚本 VM 内存查询（MB）。有 Lua 的工程指到 collectgarbage("count")/1024；为空则不产出 LUA_MEM_TREND（除非脚本侧主动 RecordScriptMemory）。</summary>
        public static Func<double> ScriptMemoryMBProvider;
        /// <summary>面板开启时调用：关闭工程里与本采样冲突的其他 Lua 插桩（避免双重插桩放大噪声）。没有就留空。</summary>
        public static Action DisableCompetingLuaProfiler;
        /// <summary>导出 META 的 deepLuaNative 位：工程自带的原生 Lua 深度插桩是否仍在开着（正常应为 false）。</summary>
        public static Func<bool> IsCompetingLuaProfilerActive;
        /// <summary>采集开始/结束事件（脚本层适配器据此启停自己的周期任务）。</summary>
        public static event Action CaptureStarted;
        public static event Action CaptureStopped;

        public static bool IsCapturing { get { return _lines != null; } }
        public static int CaptureStartFrame { get; private set; }

        // ================= 采集控制（面板调用） =================
        public static void BeginCapture()
        {
            if (_lines != null)
            {
                EndCapture();
            }
            _lines = new List<string>(256);
            _droppedLines = 0;
            _memSamples = new List<string>(64);
            CaptureStartFrame = Time.frameCount;
            _pendingViews.Clear();
            _pendingClick.Clear();
            _fpsCollectors.Clear();
            _lastClick = null;
            _lastMergeStamp = -1;
            _sceneSwitch = null;
            SampleScriptMemory();
            EnsureDriver();
            var evt = CaptureStarted;
            if (evt != null)
            {
                evt();
            }
        }

        /// <summary>停止采集并取回文本；未在采集中返回空串。</summary>
        public static void EndCapture(out string viewStats, out string memoryTrend)
        {
            viewStats = "";
            memoryTrend = "";
            if (_lines == null)
            {
                return;
            }
            foreach (var c in _fpsCollectors.Values)
            {
                if (c.deltaMs.Count > 0)
                {
                    FinalizeFps(c); // 未满窗口的也结算，避免丢掉停录前刚打开的界面
                }
            }
            _fpsCollectors.Clear();
            SampleScriptMemory(); // 收尾补采一发，保证首末成对
            var lines = _lines;
            _lines = null;
            if (_droppedLines > 0)
            {
                lines.Add(string.Format("(容量截断：更早的 {0} 行已被环形覆盖)", _droppedLines));
            }
            viewStats = string.Join("\n", lines.ToArray());
            memoryTrend = string.Join("\n", _memSamples.ToArray());
            _memSamples = null;
            DestroyDriver();
            var evt = CaptureStopped;
            if (evt != null)
            {
                evt();
            }
        }

        public static void EndCapture()
        {
            string a, b;
            EndCapture(out a, out b);
        }

        // ================= 界面打开（ViewOpen）=================
        /// <summary>点击/触发时刻打点（按钮回调、输入分发处调用）；新点击覆盖旧槽。</summary>
        public static void MarkClick(string id)
        {
            if (!IsCapturing) return;
            _lastClick = new ClickSlot { time = Now(), id = id };
        }

        /// <summary>界面开始加载资源：就地结算「点击 → 开始加载」耗时（点击响应）。同一界面只算第一次。</summary>
        public static void MarkViewLoadStart(string viewName)
        {
            if (!IsCapturing || string.IsNullOrEmpty(viewName)) return;
            if (_pendingClick.ContainsKey(viewName)) return;
            double now = Now();
            var slot = _lastClick;
            if (slot != null && (now - slot.time) * 1000 > Thresholds.ClickStaleMs)
            {
                _lastClick = null;
                slot = null;
            }
            if (slot != null)
            {
                double resp = (now - slot.time) * 1000;
                _pendingClick[viewName] = new ClickInfo { respMs = resp, tag = resp > Thresholds.ClickResponseSlowMs ? "slow" : "normal" };
                _lastMergeStamp = now;
                _lastClick = null;
            }
            else if (_lastMergeStamp >= 0 && (now - _lastMergeStamp) <= Thresholds.ClickMergeWindowSec)
            {
                _pendingClick[viewName] = new ClickInfo { tag = "merged" };
                _lastMergeStamp = now;
            }
            else
            {
                _pendingClick[viewName] = new ClickInfo { tag = "unpaired" };
            }
        }

        /// <summary>界面资源加载完成（prefab/依赖加载耗时）。与 MarkViewShown 凑齐后输出一条 ViewOpen。</summary>
        public static void MarkViewResourceLoaded(string viewName, double resourceLoadMs)
        {
            if (!IsCapturing || string.IsNullOrEmpty(viewName)) return;
            var p = GetPendingView(viewName);
            if (p.resourceMs < 0) p.resourceMs = resourceLoadMs;
            TryFlushViewOpen(viewName, p);
        }

        /// <summary>界面显示完成（从触发加载到首帧完整显示的总耗时）。</summary>
        public static void MarkViewShown(string viewName, double totalLoadMs, bool isSubView = false)
        {
            if (!IsCapturing || string.IsNullOrEmpty(viewName)) return;
            var p = GetPendingView(viewName);
            if (p.totalMs < 0) p.totalMs = totalLoadMs;
            p.isSubView |= isSubView;
            TryFlushViewOpen(viewName, p);
        }

        /// <summary>直接记录一条 ViewOpen（工程已自行算好各段耗时时用）。clickResponseMs 为 null 表示无点击配对信息。</summary>
        public static void RecordViewOpen(string viewName, double resourceLoadMs, double totalLoadMs, bool isSubView = false,
            double? clickResponseMs = null)
        {
            if (!IsCapturing || string.IsNullOrEmpty(viewName)) return;
            ClickInfo ci = null;
            if (clickResponseMs.HasValue)
            {
                ci = new ClickInfo { respMs = clickResponseMs.Value, tag = clickResponseMs.Value > Thresholds.ClickResponseSlowMs ? "slow" : "normal" };
            }
            else
            {
                _pendingClick.TryGetValue(viewName, out ci);
            }
            _pendingClick.Remove(viewName);
            EmitViewOpen(viewName, resourceLoadMs, totalLoadMs, isSubView, ci);
        }

        // ================= 开屏帧率（ViewFPS）=================
        /// <summary>界面打开后启动一个 FPS 统计窗口（默认 1s 后结算）。需要采集器每帧 Tick（Play 中由内置驱动自动完成）。</summary>
        public static void BeginViewFpsWindow(string viewName)
        {
            if (!IsCapturing || string.IsNullOrEmpty(viewName)) return;
            if (_fpsCollectors.ContainsKey(viewName)) return;
            _fpsCollectors[viewName] = new FpsCollector
            {
                viewName = viewName,
                histDelta1 = _lastDelta, histDelta2 = _prevDelta, histDelta3 = _prev2Delta,
            };
        }

        /// <summary>每帧推进（内置驱动在 LateUpdate 调用；无内置驱动的环境可自行每帧调用，传未钳制的 unscaledDeltaTime）。</summary>
        public static void Tick(float unscaledDeltaSeconds)
        {
            if (!IsCapturing) return;
            double deltaMs = unscaledDeltaSeconds * 1000.0;
            if (deltaMs > Thresholds.FpsWindowMs) return; // 后台/暂停/断点回来的超大间隔是停顿不是卡顿，丢弃
            double fps = unscaledDeltaSeconds > 0 ? 1.0 / unscaledDeltaSeconds : 0;
            if (_fpsCollectors.Count > 0)
            {
                _fpsDone.Clear();
                foreach (var kv in _fpsCollectors)
                {
                    var c = kv.Value;
                    c.timeSumMs += deltaMs;
                    c.fps.Add(fps);
                    c.deltaMs.Add(deltaMs);
                    if (c.timeSumMs >= Thresholds.FpsWindowMs)
                    {
                        FinalizeFps(c);
                        _fpsDone.Add(kv.Key);
                    }
                }
                for (int i = 0; i < _fpsDone.Count; i++) _fpsCollectors.Remove(_fpsDone[i]);
            }
            _prev2Delta = _prevDelta;
            _prevDelta = _lastDelta;
            _lastDelta = deltaMs;
        }

        // ================= 节点使用率（ViewNode）=================
        /// <summary>延迟 delaySeconds 后统计 root 下 Transform 总数 / 未激活数（Play 中由内置驱动调度）。</summary>
        public static void ScheduleViewNodeStats(string viewName, Transform root, bool isSubView = false, float delaySeconds = 1f)
        {
            if (!IsCapturing || root == null || string.IsNullOrEmpty(viewName)) return;
            var d = EnsureDriver();
            if (d != null)
            {
                d.StartCoroutine(NodeStatsCo(viewName, root, isSubView, delaySeconds));
            }
            else
            {
                RecordViewNodes(viewName, root, isSubView);
            }
        }

        public static void RecordViewNodes(string viewName, Transform root, bool isSubView = false)
        {
            if (!IsCapturing || root == null) return;
            var all = root.GetComponentsInChildren<Transform>(true);
            int total = all.Length, inactive = 0;
            for (int i = 0; i < total; i++)
            {
                var go = all[i].gameObject;
                if (!go.activeSelf || !go.activeInHierarchy) inactive++;
            }
            RecordViewNodes(viewName, total, inactive, isSubView);
        }

        public static void RecordViewNodes(string viewName, int totalCount, int inactiveCount, bool isSubView = false)
        {
            if (!IsCapturing || string.IsNullOrEmpty(viewName)) return;
            double ratio = totalCount > 0 ? inactiveCount * 100.0 / totalCount : 0;
            bool exceeded = false;
            var sb = new StringBuilder();
            sb.Append("[ProfilerUtils][ViewNode] ").Append(isSubView ? "界面子项" : "界面").Append(" [").Append(viewName).Append("]");
            sb.Append("  ").Append(totalCount <= Thresholds.NodeTotalStd
                ? F("Total(节点总数)={0}", totalCount)
                : Flag(ref exceeded, F("Total(节点总数{0}以下)={1}", Thresholds.NodeTotalStd, totalCount)));
            sb.Append("  ").Append(inactiveCount <= Thresholds.NodeInactiveStd
                ? F("Inactive(未使用数)={0}", inactiveCount)
                : Flag(ref exceeded, F("Inactive(未使用数{0}以下)={1}", Thresholds.NodeInactiveStd, inactiveCount)));
            sb.Append("  ").Append(ratio <= Thresholds.NodeInactiveRatioStd
                ? F("InactiveRatio(未使用率)={0:F2}%", ratio)
                : Flag(ref exceeded, F("InactiveRatio(未使用率{0}%以下)={1:F2}%", (int)Thresholds.NodeInactiveRatioStd, ratio)));
            Push(sb.ToString(), exceeded);
        }

        // ================= 场景切换（SceneSwitch）=================
        /// <summary>场景切换起点（发起切换、过完前置校验后调用）；新切换覆盖旧槽。</summary>
        public static void BeginSceneSwitch(string fromScene, string toScene)
        {
            if (!IsCapturing) return;
            _sceneSwitch = new SceneSlot { time = Now(), from = fromScene ?? "(启动)", to = toScene ?? "?" };
        }

        /// <summary>场景切换终点（loading 已关、新场景生命周期走完、用户可感"切完"时调用）。</summary>
        public static void EndSceneSwitch(string sceneName = null)
        {
            var slot = _sceneSwitch;
            _sceneSwitch = null;
            if (!IsCapturing || slot == null) return;
            double ms = (Now() - slot.time) * 1000;
            RecordSceneSwitch(slot.from, sceneName ?? slot.to, ms);
        }

        public static void RecordSceneSwitch(string fromScene, string toScene, double costMs)
        {
            if (!IsCapturing) return;
            string key = (fromScene ?? "(启动)") + "→" + (toScene ?? "?");
            bool exceeded = costMs > Thresholds.SceneSwitchSlowMs;
            string msg = exceeded
                ? F("[ProfilerUtils][SceneSwitch] 场景 [{0}] - 切换耗时: {1:F2}ms（超过阈值: {2}ms）", key, costMs, (int)Thresholds.SceneSwitchSlowMs)
                : F("[ProfilerUtils][SceneSwitch] 场景 [{0}] - 切换耗时: {1:F2}ms", key, costMs);
            Push(msg, exceeded);
        }

        // ================= 脚本 VM 内存 / 通用行 =================
        /// <summary>记录一发脚本 VM 内存样本（MB）。内置驱动会按 ScriptMemoryMBProvider 周期采样；脚本侧也可自行周期调用。</summary>
        public static void RecordScriptMemory(double megabytes)
        {
            if (_memSamples == null) return;
            if (_memSamples.Count >= Thresholds.MaxMemorySamples) return;
            _memSamples.Add(F("{0}|{1}|{2:F2}", Clock(), Time.frameCount, megabytes));
        }

        /// <summary>通用记录：自定义类型的一条统计（type 例如 "ViewOpen" / 自定义；subject 为界面名/路线名）。</summary>
        public static void RecordLine(string type, string label, string subject, string message, bool exceeded)
        {
            if (!IsCapturing) return;
            Push(F("[ProfilerUtils][{0}] {1} [{2}] - {3}", type, label, subject, message), exceeded);
        }

        // ================= 内部实现 =================
        private class PendingView { public double resourceMs = -1, totalMs = -1; public bool isSubView; }
        private class ClickSlot { public double time; public string id; }
        private class ClickInfo { public double respMs = -1; public string tag; }
        private class SceneSlot { public double time; public string from, to; }
        private class FpsCollector
        {
            public string viewName;
            public double timeSumMs;
            public readonly List<double> fps = new List<double>(128);
            public readonly List<double> deltaMs = new List<double>(128);
            public double histDelta1, histDelta2, histDelta3;
        }

        private static List<string> _lines;
        private static List<string> _memSamples;
        private static int _droppedLines;
        private static readonly Dictionary<string, PendingView> _pendingViews = new Dictionary<string, PendingView>();
        private static readonly Dictionary<string, ClickInfo> _pendingClick = new Dictionary<string, ClickInfo>();
        private static readonly Dictionary<string, FpsCollector> _fpsCollectors = new Dictionary<string, FpsCollector>();
        private static readonly List<string> _fpsDone = new List<string>();
        private static ClickSlot _lastClick;
        private static double _lastMergeStamp = -1;
        private static SceneSlot _sceneSwitch;
        private static double _lastDelta, _prevDelta, _prev2Delta;
        private static float _nextMemSampleTime;

        private static double Now() { return Time.realtimeSinceStartupAsDouble; }
        private static string Clock() { return DateTime.Now.ToString("HH:mm:ss"); }
        private static string F(string fmt, params object[] args) { return string.Format(CultureInfo.InvariantCulture, fmt, args); }
        private static string Flag(ref bool exceeded, string s) { exceeded = true; return s; }

        private static void Push(string message, bool exceeded)
        {
            var lines = _lines;
            if (lines == null) return;
            if (lines.Count >= Thresholds.MaxCaptureLines)
            {
                lines.RemoveAt(0);
                _droppedLines++;
            }
            lines.Add(F("{0}|{1}|{2}|{3}", Clock(), Time.frameCount, exceeded ? "!" : "-", message));
        }

        private static PendingView GetPendingView(string viewName)
        {
            PendingView p;
            if (!_pendingViews.TryGetValue(viewName, out p))
            {
                p = new PendingView();
                _pendingViews[viewName] = p;
            }
            return p;
        }

        private static void TryFlushViewOpen(string viewName, PendingView p)
        {
            if (p.resourceMs < 0 || p.totalMs < 0) return;
            _pendingViews.Remove(viewName);
            ClickInfo ci;
            _pendingClick.TryGetValue(viewName, out ci);
            _pendingClick.Remove(viewName);
            EmitViewOpen(viewName, p.resourceMs, p.totalMs, p.isSubView, ci);
        }

        private static void EmitViewOpen(string viewName, double resourceMs, double totalMs, bool isSubView, ClickInfo click)
        {
            bool exceeded = false;
            var parts = new List<string>(4);
            parts.Add(resourceMs > Thresholds.ViewResourceLoadMs
                ? Flag(ref exceeded, F("资源加载耗时: {0:F2}ms（超过阈值: {1:F2}ms）", resourceMs, Thresholds.ViewResourceLoadMs))
                : F("资源加载耗时: {0:F2}ms", resourceMs));
            parts.Add(totalMs > Thresholds.ViewTotalLoadMs
                ? Flag(ref exceeded, F("显示完成耗时: {0:F2}ms（超过阈值: {1:F2}ms）", totalMs, Thresholds.ViewTotalLoadMs))
                : F("显示完成耗时: {0:F2}ms", totalMs));
            if (totalMs > Thresholds.FpsWindowMs)
            {
                parts.Add(Flag(ref exceeded, F("⚠ 加载耗时 {0:F0}ms 超过统计时长 {1:F0}ms，FPS/Jank/Drop等数据可能不准确", totalMs, Thresholds.FpsWindowMs)));
            }
            if (click != null)
            {
                if (click.respMs >= 0)
                {
                    parts.Add(click.tag == "slow"
                        ? Flag(ref exceeded, F("点击响应耗时: {0:F2}ms（slow，超 {1:F0}ms）", click.respMs, Thresholds.ClickResponseSlowMs))
                        : F("点击响应耗时: {0:F2}ms", click.respMs));
                }
                else
                {
                    parts.Add(click.tag == "merged" ? "点击响应耗时: 已合并(父吞)" : "点击响应耗时: 未配对");
                }
            }
            Push(F("[ProfilerUtils][ViewOpen] {0} [{1}] - {2}", isSubView ? "界面子项" : "界面", viewName, string.Join(" - ", parts.ToArray())), exceeded);
        }

        private static void FinalizeFps(FpsCollector c)
        {
            var deltas = c.deltaMs;
            var fpsList = c.fps;
            int n = deltas.Count;
            if (n == 0) return;
            double timeSum = 0;
            for (int i = 0; i < n; i++) timeSum += deltas[i];
            double avg = timeSum > 0 ? n / timeSum * 1000 : 0;

            int target = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60;
            bool stdEnabled = target == 60 || target == 30;
            int stdFps = target == 30 ? 25 : 50;
            int dropFactor = Math.Max(4, target * 8 / 60);

            Func<int, double> getDelta = k =>
            {
                if (k >= 1) return k <= n ? deltas[k - 1] : 0;
                if (k == 0) return c.histDelta1;
                if (k == -1) return c.histDelta2;
                return c.histDelta3;
            };

            int smallJank = 0, jank = 0, bigJank = 0, freeze = 0, drop = 0;
            double stutterSum = 0;
            for (int i = 1; i <= n; i++)
            {
                double dm = deltas[i - 1];
                double prev3 = (getDelta(i - 1) + getDelta(i - 2) + getDelta(i - 3)) / 3;
                if (dm > prev3 * Thresholds.JankMultiplier)
                {
                    stutterSum += dm;
                    if (dm > Thresholds.BigJankMs) bigJank++;
                    else if (dm > Thresholds.JankMs) jank++;
                    else if (dm > Thresholds.SmallJankMs) smallJank++;
                }
                if (dm > Thresholds.FreezeMs) freeze++;
                double prevFps = i > 1 ? fpsList[i - 2] : (c.histDelta1 > 0 ? 1000 / c.histDelta1 : 0);
                double curFps = dm > 0 ? 1000 / dm : 0;
                if (prevFps > target) prevFps = target;
                if (curFps > target) curFps = target;
                if (prevFps > 0 && prevFps - curFps > dropFactor) drop++;
            }
            int avgFps = (int)Math.Floor(avg);
            double stutterRate = timeSum > 0 ? stutterSum / timeSum * 100 : 0;
            double freezeRate = freeze * 100.0 / n;
            double dropRate = drop * 100.0 / n;
            bool exceeded = false;

            var sb = new StringBuilder(512);
            sb.Append("[ProfilerUtils][ViewFPS] 界面 [").Append(c.viewName).Append("]");
            sb.Append("  ").Append(avgFps >= stdFps || !stdEnabled ? F("FPS(帧率)={0}", avgFps) : Flag(ref exceeded, F("FPS(帧率{0}以上)={1}", stdFps, avgFps)));
            sb.Append("  ").Append(smallJank <= Thresholds.SmallJankStd || !stdEnabled ? F("SmallJank(小卡顿)={0}次", smallJank) : Flag(ref exceeded, F("SmallJank(小卡顿{0}次以下)={1}次", Thresholds.SmallJankStd, smallJank)));
            sb.Append("  ").Append(jank <= Thresholds.JankStd || !stdEnabled ? F("Jank(卡顿)={0}次", jank) : Flag(ref exceeded, F("Jank(卡顿{0}次以下)={1}次", Thresholds.JankStd, jank)));
            sb.Append("  ").Append(bigJank <= Thresholds.BigJankStd || !stdEnabled ? F("BigJank(大卡顿)={0}次", bigJank) : Flag(ref exceeded, F("BigJank(大卡顿{0}次以下)={1}次", Thresholds.BigJankStd, bigJank)));
            sb.Append("  ").Append(stutterRate < Thresholds.StutterStdPct || !stdEnabled ? F("Stutter(卡顿率)={0:F0}%", stutterRate) : Flag(ref exceeded, F("Stutter(卡顿率{0}%以下)={1:F0}%", (int)Thresholds.StutterStdPct, stutterRate)));
            sb.Append("  ").Append(freezeRate < Thresholds.FreezeStdPct || !stdEnabled ? F("Freeze(冻结率)={0:F0}%", freezeRate) : Flag(ref exceeded, F("Freeze(冻结率{0}%以下)={1:F0}%", (int)Thresholds.FreezeStdPct, freezeRate)));
            sb.Append("  ").Append(dropRate < Thresholds.DropStdPct || !stdEnabled ? F("Drop(降帧率)={0:F0}%", dropRate) : Flag(ref exceeded, F("Drop(降帧率{0}%以下)={1:F0}%", (int)Thresholds.DropStdPct, dropRate)));
            sb.Append("  ").Append(F("统计时长(采样窗口)={0:F0}ms", Thresholds.FpsWindowMs));
            sb.Append("\n  fps: ");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append((int)Math.Floor(fpsList[i])); }
            sb.Append("\n  time:  ");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append((int)Math.Floor(deltas[i] + 0.5)); }
            Push(sb.ToString(), exceeded);
        }

        private static void SampleScriptMemory()
        {
            var provider = ScriptMemoryMBProvider;
            if (provider == null || _memSamples == null) return;
            try
            {
                RecordScriptMemory(provider());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AIProfiler] ScriptMemoryMBProvider 调用失败: " + e.Message);
            }
            _nextMemSampleTime = Time.unscaledTime + Thresholds.ScriptMemorySampleIntervalSec;
        }

        private static IEnumerator NodeStatsCo(string viewName, Transform root, bool isSubView, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (root != null && IsCapturing)
            {
                RecordViewNodes(viewName, root, isSubView);
            }
        }

        // ---- 内置驱动：Play 中每帧 Tick + 周期内存采样 ----
        private static CaptureDriver _driver;

        private static CaptureDriver EnsureDriver()
        {
            if (_driver != null) return _driver;
            if (!Application.isPlaying) return null;
            var go = new GameObject("AIProfilerCaptureDriver");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _driver = go.AddComponent<CaptureDriver>();
            return _driver;
        }

        private static void DestroyDriver()
        {
            if (_driver == null) return;
            UnityEngine.Object.Destroy(_driver.gameObject);
            _driver = null;
        }

        private sealed class CaptureDriver : MonoBehaviour
        {
            private void LateUpdate()
            {
                Tick(Time.unscaledDeltaTime);
                if (ScriptMemoryMBProvider != null && Time.unscaledTime >= _nextMemSampleTime)
                {
                    SampleScriptMemory();
                }
            }

            private void OnDestroy()
            {
                if (_driver == this) _driver = null;
            }
        }
    }
}
