#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace AIProfiler
{
    /// <summary>
    /// AI Profiler 整合面板：一键录制 Unity 原生 Profiler（C#/GPU/内存/GC）+ 可选的 Lua profiler 后端（Lua VM，见 LuaProfilerBackend）
    /// + 运行时采集器 AIProfilerCapture（界面/场景/脚本内存），一键导出供 AI 分析。打开面板即自动开启 Unity Deep Profile（及 Lua 深度采样，若有后端）。
    /// 入口：Window/Analysis/AI Profiler。
    /// </summary>
    public class AIProfilerWindow : EditorWindow
    {
        private enum State { Idle, Recording, Stopped }

        /// <summary>
        /// 采样拓扑：EditorLocal=Editor 本地（ProfilerRecorder + Lua 后端进程内回调）；
        /// RemoteDevice=真机连接（ADB USB 转发 Unity Profiler + Lua 后端 TCP，不依赖局域网 IP）。
        /// </summary>
        private enum CaptureMode { EditorLocal, RemoteDevice }

        private class ActiveCounter
        {
            public ProfilerRecorder recorder;
            public string label;
            public string category;  // "Memory" / "Render"
            public bool isMemory;
        }

        // 要采集的内存 / 渲染计数器
        private struct CounterDef
        {
            public ProfilerCategory category;
            public string name;
            public bool isMemory;
            public string categoryLabel;
            public CounterDef(ProfilerCategory c, string n, bool mem, string cl)
            {
                category = c; name = n; isMemory = mem; categoryLabel = cl;
            }
        }

        private static readonly CounterDef[] CounterDefs =
        {
            new CounterDef(ProfilerCategory.Memory, "Total Reserved Memory", true, "Memory"),
            new CounterDef(ProfilerCategory.Memory, "Total Used Memory", true, "Memory"),
            new CounterDef(ProfilerCategory.Memory, "GC Reserved Memory", true, "Memory"),
            new CounterDef(ProfilerCategory.Memory, "GC Used Memory", true, "Memory"),
            new CounterDef(ProfilerCategory.Memory, "Gfx Used Memory", true, "Memory"),
            new CounterDef(ProfilerCategory.Memory, "GC Allocated In Frame", true, "Memory"),
            new CounterDef(ProfilerCategory.Render, "Draw Calls Count", false, "Render"),
            new CounterDef(ProfilerCategory.Render, "SetPass Calls Count", false, "Render"),
            new CounterDef(ProfilerCategory.Render, "Batches Count", false, "Render"),
            new CounterDef(ProfilerCategory.Render, "Triangles Count", false, "Render"),
            new CounterDef(ProfilerCategory.Render, "Vertices Count", false, "Render"),
        };

        // Unity Profiler 帧环形缓冲上限：Unity 默认只保留最近 300 帧（超出即丢早期帧），
        // 这是 firstFrameIndex..lastFrameIndex 的真实约束。这里拉到 Unity 的硬上限 2000，
        // 让较长录制（约 2000 帧 ≈ 30-60s）不被截断。代价是 Editor 内存占用更大（已确认可接受）。
        // 注：2000 是 Unity "retained-frame" 模式的天花板；要真正"无上限"需改为录制期逐帧增量聚合
        //     （把 WalkUnityFrames 从 Export 时一次性遍历，改成每帧消费后丢弃），属较大重构，未做。
        private const int MAX_FRAME_HISTORY = 2000;
        // 计数器样本容量与帧数对齐，否则内存/渲染计数器的 min/avg/max 只覆盖最后 1000 帧。
        private const int RECORDER_CAPACITY = MAX_FRAME_HISTORY;

        private State _state = State.Idle;
        private CaptureMode _mode = CaptureMode.EditorLocal;
        private static readonly string[] _modeTabs = { "Editor 本地", "真机连接(手机)" };
        private readonly object _luaLock = new object();
        // Lua 到达即聚合（替代旧的无界 _luaSamples 缓冲，内存降到 O(unique func)）。_luaLock 保护。
        private readonly Dictionary<string, AIProfilerExporter.LuaAgg> _luaAgg =
            new Dictionary<string, AIProfilerExporter.LuaAgg>(2048);
        private int _luaSampleCount; // 收到的 Lua 采样节点数（仅状态展示）
        private volatile bool _remoteHookReady; // 真机独立状态心跳确认 Lua Hook 就绪；不同于 TCP connected
        private volatile bool _remoteHookCapturing;
        private bool _remoteLuaCapture = true; // 崩溃敏感场景可关：只采 Unity 原生帧，不启用设备 Lua 采样
        private static ILuaProfilerBackend Lua { get { return LuaProfilerBackend.Current; } }
        /// <summary>真机 Lua Hook 是否就绪：后端支持状态心跳时按心跳，否则按 TCP 连接状态兜底。</summary>
        private bool RemoteHookReady { get { return Lua.RemoteStatusSupported ? _remoteHookReady : IsLuaRemoteConnected(); } }
        private readonly List<ActiveCounter> _activeCounters = new List<ActiveCounter>();
        private readonly List<AIProfilerExporter.CounterStat> _counterStats = new List<AIProfilerExporter.CounterStat>();

        private int _firstFrame = -1;
        private int _lastFrame = -1;
        private bool _hasCapture = false;
        private string _statusLine = "";
        private int _effectiveFrameBudget = -1; // 最近一次 StartRecord 实际生效的帧缓冲上限；-1=未设置/反射失败

        // GPU/CPU 帧耗时 best-effort（EditorApplication.update 采样）
        private double _gpuMin, _gpuMax, _gpuSum, _cpuSum;
        private int _gpuCount;
        private bool _gpuSampling;

        // === 编辑器本地"无上限"录制（分段 binary log）===
        private bool _unlimited = true;                  // 默认开：磁盘分段 binary log，导出时逐段解析，突破 2000 帧上限
        private const int SEG_FRAMES = 600;              // 非 Deep 的帧数安全闸
        private const int SEG_DEEP_FRAMES = 16;          // Deep 单帧可达 ~18MB；硬闸避免磁盘长度缓冲滞后形成 1GB+ 段
        private const int UNLIMITED_RECORDER_CAPACITY = 60000; // 无上限模式计数器样本容量（覆盖长录制；与帧窗口无关）
        private const long BINLOG_MAX_USED_MEMORY = 256L * 1024 * 1024; // Profiler 流式缓冲，防丢帧
        // 段体积上限：Deep Profiling 下每帧可达 ~18MB，单纯按帧数(1900)轮转会让单段胀到几十 GB，
        // ProfilerDriver.LoadProfile 因内存失败被静默跳过 → 原生 CPU/CS/时间线数据空。改为体积优先轮转，
        // on-disk 大小受 256MB 流式缓冲影响，单靠 FileInfo.Length 无法硬封顶；Deep 另加 16 帧硬闸。
        // 2026-07-10 样本出现 1.38GB 段并导致 LoadProfile 失败，不能再只依赖磁盘长度轮转。
        private const int SEG_MAX_MB = 256;
        private const long SEG_MAX_BYTES = SEG_MAX_MB * 1024L * 1024L;
        private string _segDir;                          // 本次录制的分段文件目录（Assets 外）
        private readonly List<string> _segFiles = new List<string>(); // 已写出的 .raw 段（含扩展名）
        private int _segIndex;
        private int _segStartFrame;
        private bool _binLogging;
        private bool _prevAllocCallstacks; // 录制前的 Profiler.enableAllocationCallstacks，停录时还原

        // === 录制期采样流污染监听 ===
        // Profiler Begin/End 配对断裂（Missing/Non-matching Profiler.EndSample 告警）时，不完整采样流被
        // 流式写进当前滚动段，段落盘即损坏（导出 LoadProfile 反序列化失败）。录制中就地捕获：
        // Console 即时告警 + 面板红字 + 导出透传 META，让排查直指污染源而非误猜"段过大/内存不足"。
        private bool _pollutionWatching;
        private int _pollutionCount;
        private string _pollutionFirstMsg = "";
        private int _pollutionFirstSeg = -1;
        private int _pollutionLastSeg = -1;
        // 注：曾尝试 AppDomain.FirstChanceException 监听被吞异常辅助定位污染源——2026-07-13 探针实测
        // Unity 2022.3 Mono 不派发该事件，已移除；被吞的脚本层异常需脚本侧自行上报（如 Lua 适配器的 pcall 守卫）。

        // === 界面/场景采集（AIProfilerCapture）===
        // Editor 本地录制期间开启运行时采集器 AIProfilerCapture（工程在 UI/场景流程里打点：
        // ViewOpen 打开耗时 / 点击响应 / ViewFPS 窗口帧率卡顿 / ViewNode 节点使用率 / SceneSwitch 场景切换耗时），
        // 并周期采样脚本 VM 内存。StopRecord 时取回文本，导出为 VIEW_STATS / SCENE_SWITCH / LUA_MEM_TREND 三个 section。
        // 真机模式数据在设备进程内、Editor 够不到，暂不采集。
        private string _viewStatsText = "";
        private string _luaMemTrendText = "";
        private long _viewStatsFrameBase = -1; // StartRecord 时运行时 Time.frameCount，供导出侧标注帧号对齐基准

        // 真机模式：从设备 adb pull 下来的 .raw 段（设备侧 DeviceFrameRecorder 写的）。导出时复用分段解析路径累加。
        private readonly List<string> _deviceSegFiles = new List<string>();
        private string _deviceSegLocalDir; // 本次 pull 落地的本地临时目录
        private string _deviceCaptureSession = "";
        private sealed class DeviceSegmentPullResult
        {
            public bool found;
            public int segmentIndex = -1;
            public string localPath;
            public string error;
        }
        private System.Threading.Tasks.Task<DeviceSegmentPullResult> _deviceSegmentPullTask;
        private bool _deviceSegmentPullPolling;
        private double _nextDeviceSegmentPullPoll;
        private string _deviceSegmentPullError = "";
        private const double DEVICE_START_TIMEOUT_SECONDS = 5.0;
        private const double DEVICE_STOP_TIMEOUT_SECONDS = 10.0;
        private const double DEVICE_PULL_POLL_INTERVAL_SECONDS = 1.0;
        private const int DEVICE_PULL_TIMEOUT_MS = 120000;
        private const string kDeviceRecoverySession = "AIProfiler.DeviceRecovery.Session";
        private const string kDeviceRecoverySerial = "AIProfiler.DeviceRecovery.Serial";
        private const string kDeviceRecoveryPackage = "AIProfiler.DeviceRecovery.Package";
        private const string kDeviceRecoveryLocalDir = "AIProfiler.DeviceRecovery.LocalDir";

        // 真机 ADB USB 一键连接：Unity Profiler 走 34999 + device:// ADB 通道，Lua 后端走 2333 TCP 转发（Miku 默认端口）。
        private const int UNITY_PROFILER_ADB_PORT = 34999;
        private const int LUA_PROFILER_ADB_PORT = 2333;
        private string _adbSerial = "";
        private string _adbPackage = "";
        private bool _adbProfilerSelectPending;
        private double _adbProfilerSelectDeadline;

        [MenuItem("Window/Analysis/AI Profiler", priority = 201)]
        public static void ShowWindow()
        {
            var window = GetWindow<AIProfilerWindow>();
            window.titleContent = new GUIContent("AI Profiler");
            window.minSize = new Vector2(420, 220);
            window.Show();
        }

        // 采样流污染现场诊断：泄漏的 BeginSample 会被 Unity 在校验点强制闭合并照常记录，
        // 表现为帧内某个样本的时长异常拉长（横跨多个兄弟系统）。趁 flood 现场扫最近 live 帧，
        // 按“子样本时长占父级比例异常”捞嫌疑并落盘，直接读出泄漏样本的名字。
        // 需要 live 帧数据：用 Unity Profiler 窗口 Record（Deep）复现——AI Profiler 无上限 binlog 模式下 live 环无数据。
        private const string SUSPECT_DUMP_PATH = "Assets/ProfilerLogs/suspect_frames_dump.txt";

        [MenuItem("Window/Analysis/AI Profiler Dump Suspect Frames", priority = 202)]
        public static void DumpSuspectFrames()
        {
            int last = ProfilerDriver.lastFrameIndex;
            int first = ProfilerDriver.firstFrameIndex;
            if (last < 0 || first < 0)
            {
                Debug.LogWarning("[AIProfiler] 无 live 帧可扫：请用 Unity Profiler 窗口开启 Record（Deep Profile）复现告警后再执行本命令。");
                return;
            }
            int begin = Mathf.Max(first, last - 8);
            var sb = new System.Text.StringBuilder(64 * 1024);
            sb.AppendLine(string.Format("[AIProfiler] suspect dump frames {0}..{1} @ {2}", begin, last, System.DateTime.Now.ToString("HH:mm:ss")));
            try
            {
                for (int f = begin; f <= last; f++)
                {
                    // Deep 极端帧可达百万级样本，与 WalkUnityFrames 同款安全约束：可取消进度条 + 单帧样本上限
                    if (EditorUtility.DisplayCancelableProgressBar("AI Profiler",
                            string.Format("扫描嫌疑帧 {0}/{1}", f - begin + 1, last - begin + 1),
                            (float)(f - begin) / Mathf.Max(1, last - begin + 1)))
                    {
                        sb.AppendLine("(用户取消，剩余帧未扫描)");
                        break;
                    }
                    using (var raw = AIProfilerExporter.AcquireMainThreadRawView(f))
                    {
                        if (raw == null || !raw.valid || raw.sampleCount <= 1)
                        {
                            sb.AppendLine("---- frame " + f + ": no data ----");
                            continue;
                        }
                        double frameMs = raw.GetSampleTimeMs(0);
                        sb.AppendLine(string.Format("---- frame {0}: root {1:f2}ms, samples={2} ----", f, frameMs, raw.sampleCount));
                        // DFS：remain[d]=该层剩余子样本数，parentMs[d]=该层父样本时长；当前样本深度 = sp
                        int cap = Mathf.NextPowerOfTwo(raw.maxDepth + 2);
                        var remain = new int[cap];
                        var parentMs = new double[cap];
                        remain[0] = raw.GetSampleChildrenCount(0);
                        parentMs[0] = frameMs;
                        int sp = 1;
                        int printed = 0;
                        int scanEnd = Mathf.Min(raw.sampleCount, AIProfilerExporter.RAW_SAMPLE_GUARD_PER_FRAME);
                        for (int i = 1; i < scanEnd && sp > 0; i++)
                        {
                            int depth = sp;
                            double t = raw.GetSampleTimeMs(i);
                            int cc = raw.GetSampleChildrenCount(i);
                            double pMs = parentMs[depth - 1];
                            // 结构行：浅层大头；嫌疑行：占父级比例异常的深层长样本（被强制闭合的泄漏样本特征）
                            bool structural = depth <= 3 && t >= frameMs * 0.02;
                            bool suspicious = depth >= 3 && pMs > 1 && t >= pMs * 0.5 && t >= 2;
                            if ((structural || suspicious) && printed < 120)
                            {
                                printed++;
                                string name;
                                try { name = raw.GetMarkerName(raw.GetSampleMarkerId(i)) ?? "(null)"; }
                                catch { name = "(invalid marker)"; }
                                sb.Append(' ', depth * 2);
                                sb.AppendLine(string.Format("{0}{1:f2}ms d{2} c{3} {4}",
                                    suspicious ? "[SUSPECT] " : "", t, depth, cc, name));
                            }
                            if (cc > 0)
                            {
                                if (sp + 1 >= remain.Length)
                                {
                                    System.Array.Resize(ref remain, remain.Length * 2);
                                    System.Array.Resize(ref parentMs, parentMs.Length * 2);
                                }
                                remain[sp] = cc;
                                parentMs[sp] = t;
                                sp++;
                            }
                            else
                            {
                                while (sp > 0 && --remain[sp - 1] == 0)
                                {
                                    sp--;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SUSPECT_DUMP_PATH));
            System.IO.File.WriteAllText(SUSPECT_DUMP_PATH, sb.ToString());
            Debug.Log("[AIProfiler] suspect dump 已写入 " + SUSPECT_DUMP_PATH + "（" + sb.Length + " chars）");
        }

        private void OnEnable()
        {
            if (!Lua.IsAvailable)
            {
                _remoteLuaCapture = false;
            }
            RestoreDeviceRecoveryState();
            AutoEnableDeepSwitches();
            RegisterLuaReceiver();
        }

        private void OnDisable()
        {
            bool wasRecording = _state == State.Recording;
            if (wasRecording)
            {
                // 关闭窗口/域重载必须先关采样总闸；真机 stop 命令由后面的 DisconnectAdb 兜底发送。
                _state = State.Stopped;
                ProfilerDriver.enabled = false;
                if (_mode == CaptureMode.EditorLocal)
                {
                    Lua.IsSampling = false;
                    UnityEngine.Profiling.Profiler.enabled = false;
                    // 对称清理运行时采集态（不清则 AIProfilerCapture 驱动常驻、持续统计）
                    TryStopViewStatsCapture();
                }
            }
            if (wasRecording && _mode == CaptureMode.RemoteDevice)
            {
                string recoverError;
                if (!StopDeviceFrameCaptureAndPull(out recoverError))
                {
                    Debug.LogWarning("[AIProfiler] 关闭面板时设备段自动回收失败，将在重开面板后重试: " + recoverError);
                }
            }
            if (_gpuSampling)
            {
                EditorApplication.update -= OnEditorUpdateSampleGpu;
                _gpuSampling = false;
            }
            StopPollutionWatch();
            if (_binLogging)
            {
                StopBinLog(); // 关面板时收尾 binary log，避免 .raw 句柄悬挂
            }
            DisposeActiveCounters();
            UnregisterLuaReceiver();
            if (_mode == CaptureMode.RemoteDevice)
            {
                DisconnectAdb(false);
            }
            // 不主动关 Deep Profile（避免再次触发重编译），留给用户。
            Lua.WindowOpen = false;
        }

        /// <summary>按当前模式注册 Lua 采样接收：本地走后端进程内回调，真机走远程回传 + 状态心跳。无后端时全部 no-op。</summary>
        private void RegisterLuaReceiver()
        {
            Lua.UnregisterReceivers();
            if (_mode == CaptureMode.RemoteDevice)
            {
                _remoteHookReady = false;
                _remoteHookCapturing = false;
                // 远程：设备经 TCP 回传的采样在后端接收线程里触发（非主线程），_luaAgg 用 _luaLock 保护
                Lua.RegisterRemoteReceiver(OnReceiveLuaSample, OnReceiveLuaStatus);
            }
            else
            {
                Lua.RegisterLocalReceiver(OnReceiveLuaSample);
            }
        }

        private void UnregisterLuaReceiver()
        {
            Lua.UnregisterReceivers();
        }

        // 本编辑器会话是否已做过一次"深度开关自动开启"。用 SessionState 存：域重载存活、重启编辑器清空。
        private const string kDeepAutoConfiguredKey = "AIProfiler.DeepAutoConfigured";

        /// <summary>
        /// 自动配置深度开关。深度开关（Unity Deep Profile / Lua 深度采样）**只在本会话首次打开面板时强制开启一次**；
        /// 之后（域重载 / 重开面板 / 用户手动切换）不再强制，尊重用户当前选择，可在面板上点 ON/OFF 切换。
        /// 否则：用户关掉 Unity Deep Profile 会触发脚本重编译 → 域重载 → OnEnable 再次强制开启 → 表现为"关不掉"。
        /// 模式相关路由（Lua 后端本地/远程、录制标志、面板打开标志、关闭工程自带的冲突插桩）与深度强制无关，每次都设。
        /// </summary>
        private void AutoEnableDeepSwitches()
        {
            var lua = Lua;
            bool firstConfigure = !SessionState.GetBool(kDeepAutoConfiguredKey, false);

            if (_mode == CaptureMode.RemoteDevice)
            {
                // 真机模式不触碰 Editor 的 ProfilerDriver.deepProfiling（设备 deep 由打包期决定）。
                if (firstConfigure)
                    lua.DeepLuaEnabled = true; // 首次：开 Lua 深度采样
                lua.IsLocal = false;           // 路由：每次都设
                lua.RecordEnabled = true;
                lua.WindowOpen = true;
            }
            else
            {
                if (firstConfigure)
                {
                    // Unity 原生 Deep Profile —— 改它会触发脚本重编译，只能在非 Play 时开。仅首次强制。
                    if (!EditorApplication.isPlaying && !ProfilerDriver.deepProfiling)
                        ProfilerDriver.deepProfiling = true;
                    lua.DeepLuaEnabled = true; // 首次：开 Lua 深度采样
                }
                lua.IsLocal = true;
                lua.RecordEnabled = true;
                lua.WindowOpen = true;         // 部分后端以此为进 Play 时安装 hook 的前置条件
            }

            // 主动关闭工程自带的、与本采样冲突的 Lua 插桩（避免双重插桩），与深度强制无关，每次都确保。
            var disable = AIProfilerCapture.DisableCompetingLuaProfiler;
            if (disable != null)
            {
                disable();
            }

            SessionState.SetBool(kDeepAutoConfiguredKey, true);
        }

        /// <summary>切换采样模式：清并重设深度开关 + Lua 接收路径。录制中禁止切换（由 GUI 置灰保证）。</summary>
        private void SwitchMode(CaptureMode mode)
        {
            if (_mode == mode)
            {
                return;
            }
            // 离开真机模式时断连
            if (_mode == CaptureMode.RemoteDevice)
            {
                DisconnectAdb(false);
            }
            _mode = mode;
            AutoEnableDeepSwitches();
            RegisterLuaReceiver();
            _statusLine = _mode == CaptureMode.RemoteDevice
                ? "已切到真机模式：需要 Lua 时先在设备上触发 AIProfilerDeviceControl.OpenLuaProfiler()（如 GM 菜单），完整重启游戏，再点 ADB 一键连接。"
                : "已切到 Editor 本地模式。";
        }

        // 注意：真机模式下本回调在后端接收线程触发（非主线程），_luaAgg 已用 _luaLock 保护。
        private void OnReceiveLuaSample(LuaSampleNode node)
        {
            if (_state != State.Recording)
            {
                return;
            }
            // 本地模式额外受后端自身录制开关约束；真机模式由设备控制是否发送，editor 侧开关无意义，只按窗口状态收。
            if ((_mode == CaptureMode.EditorLocal && !Lua.IsSampling) ||
                (_mode == CaptureMode.RemoteDevice && !_remoteLuaCapture))
            {
                return;
            }
            // 到达即折叠进聚合字典——内存 O(unique func)，不缓冲原始采样。
            lock (_luaLock)
            {
                AIProfilerExporter.AggregateLua(node, _luaAgg);
                _luaSampleCount++;
            }
        }

        private void OnReceiveLuaStatus(bool hookReady, bool captureActive)
        {
            if (_mode != CaptureMode.RemoteDevice)
            {
                return;
            }
            _remoteHookReady = hookReady;
            _remoteHookCapturing = captureActive;
        }

        #region 录制控制
        private void StartRecord()
        {
            if (_state == State.Recording)
            {
                return;
            }
            if (_mode == CaptureMode.RemoteDevice &&
                (string.IsNullOrEmpty(_adbSerial) || string.IsNullOrEmpty(_adbPackage) || !IsAdbProfilerConnected()))
            {
                EditorUtility.DisplayDialog("AI Profiler", "请先完成 ADB 一键连接，再开始真机采样。", "OK");
                return;
            }
            if (_mode == CaptureMode.RemoteDevice && !string.IsNullOrEmpty(_deviceCaptureSession))
            {
                string recoveryError;
                if (StopDeviceFrameCaptureAndPull(out recoveryError))
                {
                    _hasCapture = true;
                    _state = State.Stopped;
                    _statusLine = string.Format("已先恢复上一设备帧段 {0} 个，请 ExportForAI 或 CleanRecord 后再开始新采样。",
                        _deviceSegFiles.Count);
                    Repaint();
                }
                else
                {
                    EditorUtility.DisplayDialog("AI Profiler",
                        "上一设备分段会话恢复失败；现有采样数据已保留。\n" + recoveryError, "OK");
                }
                return;
            }
            if (_mode == CaptureMode.RemoteDevice && _remoteLuaCapture && (!IsLuaRemoteConnected() || !RemoteHookReady))
            {
                _statusLine = IsLuaRemoteConnected()
                    ? "无法开始：Lua 远程通道已连接，但设备 Lua Hook 未就绪。"
                    : "无法开始：Lua 远程通道尚未连接。";
                EditorUtility.DisplayDialog("AI Profiler",
                    _statusLine +
                    "\n首次开启流程：设备上触发 AIProfilerDeviceControl.OpenLuaProfiler()（如 GM 菜单）→ 完整退出并重启游戏 → ADB 一键连接。" +
                    "\n刚完成重启和连接时可等待 1 秒再重试 StartRecord。", "OK");
                return;
            }
            // 重新确保开关（用户可能中途改过）——按模式配置
            AutoEnableDeepSwitches();
            var lua = Lua;

            if (_mode == CaptureMode.EditorLocal)
            {
                if (!EditorApplication.isPlaying)
                {
                    _statusLine = "无法开始：Editor 本地采样必须先进入 Play。";
                    EditorUtility.DisplayDialog("AI Profiler",
                        _statusLine + "\n正确顺序：保持本面板打开 → 进入 Play → 等游戏启动完成 → StartRecord。", "OK");
                    return;
                }
                if (lua.IsAvailable && lua.DeepLuaEnabled && !lua.IsHookReady)
                {
                    bool initialized = lua.IsHookInitialized;
                    _statusLine = initialized
                        ? "无法开始：Lua Hook 已初始化，但 Lua VM 尚未就绪。"
                        : "无法开始：本次 Play 启动时没有安装 Lua Hook。";
                    string action = initialized
                        ? "请等待游戏 Lua 初始化完成后重试 StartRecord。"
                        : "请退出 Play，保持 AI Profiler 面板打开，再重新进入 Play；看到绿色 OnStartGame 日志后重试。";
                    EditorUtility.DisplayDialog("AI Profiler", _statusLine + "\n" + action, "OK");
                    return;
                }
            }

            ClearCapturedData();

            if (_mode == CaptureMode.RemoteDevice)
            {
                ClearDeviceSegments();
                string deviceError;
                if (!StartDeviceFrameCapture(out deviceError))
                {
                    _state = State.Idle;
                    _statusLine = "真机自动分段启动失败：" + deviceError;
                    EditorUtility.DisplayDialog("AI Profiler", _statusLine, "OK");
                    return;
                }
            }

            // Unity 原生 profiler 开录（连接模式下 ProfilerDriver 录的是连接目标=设备的帧）
            // 先拉大帧缓冲上限（Profiler "Frame Count" 默认仅 300 帧），再清帧开录，避免长录制丢早期帧。
            _effectiveFrameBudget = TrySetProfilerFrameCount(MAX_FRAME_HISTORY);
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.enabled = true;

            if (_mode == CaptureMode.RemoteDevice)
            {
                // 真机：计数器从设备帧数据取（导出时），不启动只读本进程的 ProfilerRecorder，也不用 Editor FrameTiming。
                // Lua 由设备经 TCP 主动回传（远程接收已注册），window 只按 _state 收。
            }
            else
            {
                // Editor 本地：Lua 后端录制开关 + ProfilerRecorder 计数器 + Editor FrameTiming 采样
                lua.IsSampling = true;

                // 关键：live(关无上限) 路径的原生帧来自 ProfilerDriver 内存环形 buffer，而 buffer 只有在运行时
                // Profiler 真正发样本时才会被填充。ProfilerDriver.enabled=true 只表示“在录”，运行时
                // UnityEngine.Profiling.Profiler.enabled 才是样本发射总闸——它若为 false（如 Profiler 窗口
                // 未在录），原生侧一帧都进不来，导出就是 walked 0 帧、FRAME_TIMELINE/CS_HOTSPOTS 全空。
                // 无上限路径靠 StartBinLogSegment 顺带打开了它，live 路径必须在这里显式打开，两条路径对齐。
                UnityEngine.Profiling.Profiler.enabled = true;

                DisposeActiveCounters();
                // 无上限模式：计数器样本容量放大到覆盖长录制（ProfilerRecorder 自有 ring，与 2000 帧窗口无关）。
                int recCap = _unlimited ? UNLIMITED_RECORDER_CAPACITY : RECORDER_CAPACITY;
                foreach (var def in CounterDefs)
                {
                    var rec = ProfilerRecorder.StartNew(def.category, def.name, recCap);
                    _activeCounters.Add(new ActiveCounter
                    {
                        recorder = rec,
                        label = def.name,
                        category = def.categoryLabel,
                        isMemory = def.isMemory
                    });
                }

                if (!_gpuSampling)
                {
                    EditorApplication.update += OnEditorUpdateSampleGpu;
                    _gpuSampling = true;
                }

                if (_unlimited)
                {
                    StartBinLog();
                }

                // 录制期监听采样流污染（live 与无上限两条路径都受害；无上限下还能圈定受损段范围）
                StartPollutionWatch();

                // 运行时采集（AIProfilerCapture）：录制期界面打开耗时/点击响应/窗口帧率/节点使用率/场景切换 → VIEW_STATS
                TryStartViewStatsCapture();
            }

            _state = State.Recording;
            if (_mode == CaptureMode.RemoteDevice)
            {
                _statusLine = _remoteLuaCapture ? "录制中…（真机 ADB 已连接，Lua Hook 已就绪）" : "录制中…（真机 ADB 已连接，原生安全模式）";
            }
            else
            {
                _statusLine = "录制中…";
            }
        }

        /// <summary>清空已采集的数据（Lua 采样 / 计数器统计 / 帧区间 / GPU 统计），供 StartRecord 与 CleanRecord 复用。</summary>
        private void ClearCapturedData()
        {
            lock (_luaLock)
            {
                _luaAgg.Clear();
                _luaSampleCount = 0;
            }
            _counterStats.Clear();
            _viewStatsText = "";
            _luaMemTrendText = "";
            _viewStatsFrameBase = -1;
            _hasCapture = false;
            _firstFrame = _lastFrame = -1;
            _gpuMin = double.MaxValue;
            _gpuMax = 0;
            _gpuSum = 0;
            _cpuSum = 0;
            _gpuCount = 0;
            _pollutionCount = 0;
            _pollutionFirstMsg = "";
            _pollutionFirstSeg = _pollutionLastSeg = -1;
        }

        /// <summary>丢弃当前记录：清空已采集数据 + 清掉 Unity 原生帧缓冲，回到空闲态。仅在已有记录（非录制中）时可用。</summary>
        private void CleanRecord()
        {
            if (_state == State.Recording)
            {
                return; // 录制中不清，先 StopRecord
            }
            ClearCapturedData();
            ProfilerDriver.ClearAllFrames(); // 释放原生帧缓冲，避免下次 Export 误用旧帧
            TryDeleteSegments();             // 删除 ProfilerLogs/raw 下全部录制分段（含历史 session）
            ClearDeviceSegments();           // 删除从设备 pull 下来的临时段
            _state = State.Idle;
            _statusLine = "已清空记录及 ProfilerLogs/raw。";
        }

        /// <summary>
        /// 拉大 Unity Profiler 帧缓冲上限（默认仅 300 帧，是 firstFrameIndex..lastFrameIndex 的真实约束）。
        /// 控制帧数的 API 跨 Unity 版本不统一：新版是 UnityEditor.ProfilerUserSettings.frameCount（internal，
        /// 不可直接访问），旧版是 ProfilerDriver.maxHistoryLength（本版本已不存在）——直接调用要么编译不过
        /// 要么不可访问，故用反射兼容。失败则保持默认 300 帧，不影响录制本身。setter 内部会 clamp 到 [300, 2000]。
        /// </summary>
        private static int TrySetProfilerFrameCount(int frames)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                var t = FindEditorType("UnityEditor.ProfilerUserSettings");
                if (t == null)
                {
                    Debug.LogWarning("[AIProfiler] 未找到类型 UnityEditor.ProfilerUserSettings，帧上限保持默认 300。");
                    return -1;
                }

                // 1) 属性 frameCount
                var prop = t.GetProperty("frameCount", F);
                if (prop != null && prop.CanRead && prop.CanWrite && prop.PropertyType == typeof(int))
                {
                    prop.SetValue(null, frames);
                    return (int)prop.GetValue(null);
                }
                // 2) 字段 frameCount
                var fld = t.GetField("frameCount", F);
                if (fld != null && fld.FieldType == typeof(int))
                {
                    fld.SetValue(null, frames);
                    return (int)fld.GetValue(null);
                }
                // 3) 兜底：任意名字含 "frame" 的可读写 static int 属性（自动发现成员名）
                foreach (var p in t.GetProperties(F))
                {
                    if (p.PropertyType == typeof(int) && p.CanRead && p.CanWrite &&
                        p.Name.IndexOf("frame", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        p.SetValue(null, frames);
                        int v = (int)p.GetValue(null);
                        Debug.Log("[AIProfiler] 帧上限经 ProfilerUserSettings." + p.Name + " 设为 " + v);
                        return v;
                    }
                }
                Debug.LogWarning("[AIProfiler] ProfilerUserSettings 未找到可写的帧数 int 成员；帧上限保持默认 300。" +
                    "请把它的静态成员名反馈给我修正。");
                return -1;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 设置帧上限失败，保持默认 300：" + e.Message);
                return -1;
            }
        }

        // 按全名 / 简单名查找类型——ProfilerUserSettings 的命名空间跨版本不定
        // （UnityEditor / UnityEditor.Profiling / UnityEditorInternal 都可能），故全名找不到时
        // 回退到在 UnityEditor* 程序集里按"简单名"扫描，命名空间无关。
        private static System.Type FindEditorType(string fullName)
        {
            var t = System.Type.GetType(fullName);
            if (t != null)
            {
                return t;
            }
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                t = asm.GetType(fullName);
                if (t != null)
                {
                    return t;
                }
            }
            // 回退：按简单名在 UnityEditor 相关程序集里扫
            int dot = fullName.LastIndexOf('.');
            string simpleName = dot >= 0 ? fullName.Substring(dot + 1) : fullName;
            foreach (var asm in assemblies)
            {
                if (asm.GetName().Name.IndexOf("UnityEditor", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                System.Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch
                {
                    continue; // 个别程序集 GetTypes 可能抛 ReflectionTypeLoadException，跳过
                }
                foreach (var ty in types)
                {
                    if (ty.Name == simpleName)
                    {
                        return ty;
                    }
                }
            }
            return null;
        }

        // === 真机 Lua 远程连接（经 ILuaProfilerBackend）===
        private static bool IsLuaRemoteConnected()
        {
            return Lua.IsAvailable && Lua.IsRemoteConnected;
        }

        private string GetRemoteLuaStatus()
        {
            if (!Lua.IsAvailable)
            {
                return "未接入 Lua 后端";
            }
            if (!_remoteLuaCapture)
            {
                return "已禁用（原生安全模式）";
            }
            if (!IsLuaRemoteConnected())
            {
                return "TCP 未连接";
            }
            if (!RemoteHookReady)
            {
                return "TCP 已连接但 Hook 未就绪（需完整重启游戏）";
            }
            if (!Lua.RemoteStatusSupported)
            {
                return "TCP 已连接（后端无状态心跳，按连接即就绪）";
            }
            return _remoteHookCapturing ? "Hook 已就绪，采样中" : "Hook 已就绪，等待 StartRecord";
        }

        // === 连接目标（Profiler 连接的设备）===
        // ProfilerDriver 的连接相关成员（GetAvailableProfilers / connectedProfiler / GetConnectionIdentifier）
        // 在不同 Unity 版本 public/internal 不一，直接调用有编译断裂风险——故走反射（含 NonPublic），与 TrySetProfilerFrameCount 同风格。
        private const BindingFlags PD_FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static int[] GetAvailableProfilersReflect()
        {
            try
            {
                var mi = typeof(ProfilerDriver).GetMethod("GetAvailableProfilers", PD_FLAGS, null, System.Type.EmptyTypes, null);
                if (mi != null)
                {
                    return mi.Invoke(null, null) as int[];
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] GetAvailableProfilers 反射失败: " + e.Message);
            }
            return null;
        }

        private static int GetConnectedProfilerReflect()
        {
            try
            {
                var p = typeof(ProfilerDriver).GetProperty("connectedProfiler", PD_FLAGS);
                if (p != null && p.CanRead)
                {
                    return (int)p.GetValue(null);
                }
            }
            catch { /* ignore */ }
            return -1;
        }

        private static void SetConnectedProfilerReflect(int guid)
        {
            try
            {
                var p = typeof(ProfilerDriver).GetProperty("connectedProfiler", PD_FLAGS);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(null, guid);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 切换连接目标失败: " + e.Message);
            }
        }

        private static string GetConnectionIdentifierReflect(int guid)
        {
            try
            {
                var mi = typeof(ProfilerDriver).GetMethod("GetConnectionIdentifier", PD_FLAGS, null, new[] { typeof(int) }, null);
                if (mi != null)
                {
                    return mi.Invoke(null, new object[] { guid }) as string;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static bool DirectUrlConnectReflect(string url)
        {
            try
            {
                var mi = typeof(ProfilerDriver).GetMethod("DirectURLConnect", PD_FLAGS, null, new[] { typeof(string) }, null);
                if (mi == null)
                {
                    Debug.LogError("[AIProfiler] 当前 Unity 版本没有 ProfilerDriver.DirectURLConnect(string)");
                    return false;
                }
                mi.Invoke(null, new object[] { url });
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] ADB Unity Profiler 设备连接失败: " + e.Message);
                return false;
            }
        }

        private static string GetDirectConnectionUrlReflect()
        {
            try
            {
                var p = typeof(ProfilerDriver).GetProperty("directConnectionUrl", PD_FLAGS);
                return p != null ? p.GetValue(null, null) as string : null;
            }
            catch { /* ignore */ }
            return null;
        }

        private static string GetCurrentConnectionName()
        {
            int guid = GetConnectedProfilerReflect();
            string id = GetConnectionIdentifierReflect(guid);
            return string.IsNullOrEmpty(id) ? "(无法读取，用 Profiler 窗口连接下拉确认)" : id;
        }

        private void ShowConnectionTargetMenu()
        {
            var menu = new GenericMenu();
            int[] guids = GetAvailableProfilersReflect();
            int current = GetConnectedProfilerReflect();
            if (guids == null || guids.Length == 0)
            {
                menu.AddDisabledItem(new GUIContent("无可用连接目标——先在设备装 Development 包并连上（或用 Profiler 窗口连接下拉）"));
            }
            else
            {
                foreach (int g in guids)
                {
                    int captured = g;
                    string id = GetConnectionIdentifierReflect(g);
                    menu.AddItem(new GUIContent(string.IsNullOrEmpty(id) ? ("Profiler#" + g) : id), g == current,
                        () => SetConnectedProfilerReflect(captured));
                }
            }
            menu.ShowAsContext();
        }

        private struct AdbDeviceInfo
        {
            public string serial;
            public string state;
            public string description;
        }

        /// <summary>ADB USB 一键连接：自动发现设备、识别运行中的 Unity 包、建立两条转发并选中 ADB Profiler 目标。</summary>
        private void ConnectAdb()
        {
            if (_state == State.Recording)
            {
                EditorUtility.DisplayDialog("AI Profiler", "请先 StopRecord，再重新连接 ADB 设备。", "OK");
                return;
            }
            if (!string.IsNullOrEmpty(_deviceCaptureSession))
            {
                string recoveryError;
                bool recovered;
                try
                {
                    EditorUtility.DisplayProgressBar("AI Profiler", "正在先回收上一设备分段会话…", 0.5f);
                    recovered = StopDeviceFrameCaptureAndPull(out recoveryError);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                if (!recovered)
                {
                    EditorUtility.DisplayDialog("AI Profiler",
                        "上一设备分段会话恢复失败：" + recoveryError +
                        "\n请保持应用前台与 USB 连接后重试。", "OK");
                    return;
                }
                _hasCapture = true;
                _state = State.Stopped;
                _statusLine = string.Format("已先恢复上一设备帧段 {0} 个，请 ExportForAI 或 CleanRecord 后再连接。",
                    _deviceSegFiles.Count);
                Repaint();
                return;
            }
            string error;
            List<AdbDeviceInfo> devices = ListAdbDevices(out error);
            if (!string.IsNullOrEmpty(error))
            {
                EditorUtility.DisplayDialog("AI Profiler", error, "OK");
                return;
            }

            var ready = new List<AdbDeviceInfo>();
            foreach (var device in devices)
            {
                if (device.state == "device")
                {
                    ready.Add(device);
                }
            }

            if (ready.Count == 0)
            {
                string detail = devices.Count == 0
                    ? "adb devices 没发现设备。请插入 USB、开启 USB 调试并在手机上允许授权。"
                    : "没有可用设备（状态不是 device）。请处理 unauthorized/offline 后重试。";
                EditorUtility.DisplayDialog("AI Profiler", detail, "OK");
                return;
            }

            if (ready.Count == 1)
            {
                ConnectAdbDevice(ready[0].serial);
                return;
            }

            var menu = new GenericMenu();
            foreach (var device in ready)
            {
                string serial = device.serial;
                string label = string.IsNullOrEmpty(device.description)
                    ? serial
                    : serial + "  " + device.description;
                menu.AddItem(new GUIContent(label), serial == _adbSerial, () => ConnectAdbDevice(serial));
            }
            menu.ShowAsContext();
        }

        private void ConnectAdbDevice(string serial)
        {
            string adb = FindAdbPath();
            string packageError;
            string packageName = DetectUnityProfilerPackage(adb, serial, out packageError);
            if (string.IsNullOrEmpty(packageName))
            {
                EditorUtility.DisplayDialog("AI Profiler", packageError, "OK");
                return;
            }

            DisconnectAdb(false);

            string stdout, stderr;
            int exit;
            // 清理同一设备的陈旧转发（Editor 域重载/上次异常退出可能没走 OnDisable）。
            RunProcess(adb, BuildAdbArgs(serial, "forward --remove tcp:" + UNITY_PROFILER_ADB_PORT),
                out stdout, out stderr, out exit);
            RunProcess(adb, BuildAdbArgs(serial, "forward --remove tcp:" + LUA_PROFILER_ADB_PORT),
                out stdout, out stderr, out exit);

            string unityForward = string.Format("forward tcp:{0} localabstract:Unity-{1}",
                UNITY_PROFILER_ADB_PORT, packageName);
            if (!RunProcess(adb, BuildAdbArgs(serial, unityForward), out stdout, out stderr, out exit) || exit != 0)
            {
                EditorUtility.DisplayDialog("AI Profiler",
                    "Unity Profiler ADB 转发失败：\n" + (string.IsNullOrEmpty(stderr) ? stdout : stderr), "OK");
                return;
            }

            if (_remoteLuaCapture)
            {
                string luaForward = string.Format("forward tcp:{0} tcp:{1}",
                    LUA_PROFILER_ADB_PORT, LUA_PROFILER_ADB_PORT);
                if (!RunProcess(adb, BuildAdbArgs(serial, luaForward), out stdout, out stderr, out exit) || exit != 0)
                {
                    RunProcess(adb, BuildAdbArgs(serial, "forward --remove tcp:" + UNITY_PROFILER_ADB_PORT),
                        out stdout, out stderr, out exit);
                    EditorUtility.DisplayDialog("AI Profiler",
                        "Lua 采样通道 ADB 转发失败：\n" + (string.IsNullOrEmpty(stderr) ? stdout : stderr), "OK");
                    return;
                }
            }

            _adbSerial = serial;
            _adbPackage = packageName;

            Lua.RemoteDisconnect();
            RegisterLuaReceiver();
            if (_remoteLuaCapture)
            {
                Lua.SetRemoteEndpoint("127.0.0.1", LUA_PROFILER_ADB_PORT);
                Lua.RemoteConnect("127.0.0.1", LUA_PROFILER_ADB_PORT);
            }

            // Unity 连接下拉对 USB Android 设备使用 device://<adb-serial>，不依赖当前 Editor Build Target。
            if (!DirectUrlConnectReflect("device://" + serial))
            {
                RunProcess(adb, BuildAdbArgs(serial, "forward --remove tcp:" + UNITY_PROFILER_ADB_PORT),
                    out stdout, out stderr, out exit);
                RunProcess(adb, BuildAdbArgs(serial, "forward --remove tcp:" + LUA_PROFILER_ADB_PORT),
                    out stdout, out stderr, out exit);
                _adbSerial = "";
                _adbPackage = "";
                _statusLine = "Unity Profiler 无法通过 ADB 连接设备；详见 Console。";
                EditorUtility.DisplayDialog("AI Profiler", "Unity Profiler 无法通过 ADB 连接该设备。", "OK");
                return;
            }

            _statusLine = string.Format(
                "ADB 转发已建立：{0} / {1}。Unity Profiler 目标正在连接；Lua {2}。",
                serial, packageName, GetRemoteLuaStatus());
            if (!TrySelectAdbProfilerTarget())
            {
                BeginAdbProfilerTargetPolling();
            }
            Repaint();
        }

        private static List<AdbDeviceInfo> ListAdbDevices(out string error)
        {
            error = null;
            var result = new List<AdbDeviceInfo>();
            string adb = FindAdbPath();
            string stdout, stderr;
            int exit;
            if (!RunProcess(adb, "devices -l", out stdout, out stderr, out exit) || exit != 0)
            {
                error = "执行 adb devices -l 失败。请配置 Android SDK/adb：\n" +
                        (string.IsNullOrEmpty(stderr) ? stdout : stderr);
                return result;
            }

            string[] lines = stdout.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string[] parts = Regex.Split(line, @"\s+");
                if (parts.Length < 2)
                {
                    continue;
                }
                result.Add(new AdbDeviceInfo
                {
                    serial = parts[0],
                    state = parts[1],
                    description = parts.Length > 2 ? string.Join(" ", parts, 2, parts.Length - 2) : "",
                });
            }
            return result;
        }

        /// <summary>从设备的抽象 socket 表识别真实运行包，避免 PlayerSettings 与 gp/gray/cn 等包变体不一致。</summary>
        private static string DetectUnityProfilerPackage(string adb, string serial, out string error)
        {
            error = null;
            string stdout, stderr;
            int exit;
            if (!RunProcess(adb, BuildAdbArgs(serial, "shell cat /proc/net/unix"), out stdout, out stderr, out exit) || exit != 0)
            {
                error = "读取设备 Unity Profiler socket 失败：\n" + (string.IsNullOrEmpty(stderr) ? stdout : stderr);
                return null;
            }

            var packages = new List<string>();
            MatchCollection matches = Regex.Matches(stdout, @"@Unity-([A-Za-z0-9._-]+)");
            foreach (Match match in matches)
            {
                string packageName = match.Groups[1].Value;
                if (!packages.Contains(packageName))
                {
                    packages.Add(packageName);
                }
            }

            if (packages.Count == 0)
            {
                error = "设备上没有检测到 @Unity-<package> Profiler socket。\n" +
                        "请确认目标应用正在前台运行，并且安装的是 Development Build。";
                return null;
            }

            string configured = PlayerSettings.applicationIdentifier;
            if (!string.IsNullOrEmpty(configured) && packages.Contains(configured))
            {
                return configured;
            }
            if (packages.Count == 1)
            {
                return packages[0];
            }

            if (RunProcess(adb, BuildAdbArgs(serial, "shell dumpsys window"), out stdout, out stderr, out exit) && exit == 0)
            {
                Match focus = Regex.Match(stdout,
                    @"(?:mCurrentFocus|mFocusedApp).*?\s([A-Za-z0-9._-]+)/[A-Za-z0-9._$-]+");
                if (focus.Success && packages.Contains(focus.Groups[1].Value))
                {
                    return focus.Groups[1].Value;
                }
            }

            error = "设备上同时存在多个运行中的 Unity Development Player，无法自动判断目标：\n" +
                    string.Join("\n", packages.ToArray()) + "\n请只保留目标应用运行后重试。";
            return null;
        }

        private static string BuildAdbArgs(string serial, string command)
        {
            return string.IsNullOrEmpty(serial)
                ? command
                : "-s \"" + serial + "\" " + command;
        }

        private bool StartDeviceFrameCapture(out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(_deviceCaptureSession))
            {
                error = "上一设备分段会话尚未回收";
                return false;
            }
            _deviceCaptureSession = System.DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!PrepareDeviceSegmentTransferSession(_deviceCaptureSession, out error))
            {
                _deviceCaptureSession = "";
                return false;
            }
            PersistDeviceRecoverySession();
            if (!SendDeviceFrameCommand("start", _deviceCaptureSession, true, out error) ||
                !WaitForDeviceFrameState(DeviceFrameRecorder.STATE_RECORDING, _deviceCaptureSession,
                    DEVICE_START_TIMEOUT_SECONDS, out error))
            {
                // 用同 session 的 stop 覆盖迟到的 start，避免 Editor 已报失败后设备又开始长期落盘。
                string startError = error;
                string ignored;
                bool stopConfirmed = SendDeviceFrameCommand("stop", _deviceCaptureSession, false, out ignored) &&
                    WaitForDeviceFrameState(DeviceFrameRecorder.STATE_STOPPED, _deviceCaptureSession, 2.0, out ignored);
                if (stopConfirmed)
                {
                    _deviceCaptureSession = "";
                    ClearDeviceSegments();
                }
                error = startError;
                return false;
            }
            BeginDeviceSegmentPullPolling();
            return true;
        }

        private bool StopDeviceFrameCaptureAndPull(out string error)
        {
            error = null;
            string session = _deviceCaptureSession;
            if (string.IsNullOrEmpty(session))
            {
                error = "设备分段会话不存在";
                return false;
            }

            EndDeviceSegmentPullPolling();
            if (!SendDeviceFrameCommand("stop", session, false, out error) ||
                !WaitForDeviceFrameState(DeviceFrameRecorder.STATE_STOPPED, session,
                    DEVICE_STOP_TIMEOUT_SECONDS, out error))
            {
                return false;
            }

            ProfilerDriver.enabled = false;
            if (!FinishActiveDeviceSegmentPull(out error))
            {
                return false;
            }
            bool pulled = PullDeviceFrameSegments(out error);
            if (pulled)
            {
                _deviceCaptureSession = "";
            }
            return pulled;
        }

        private bool StopDeviceFrameCaptureBestEffort()
        {
            if (string.IsNullOrEmpty(_deviceCaptureSession) || string.IsNullOrEmpty(_adbSerial))
            {
                return string.IsNullOrEmpty(_deviceCaptureSession);
            }

            string ignored;
            string session = _deviceCaptureSession;
            EndDeviceSegmentPullPolling();
            bool stopped = SendDeviceFrameCommand("stop", session, false, out ignored) &&
                WaitForDeviceFrameState(DeviceFrameRecorder.STATE_STOPPED, session, 2.0, out ignored);
            return stopped;
        }

        private bool SendDeviceFrameCommand(string action, string session, bool resetState, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(_adbSerial) || string.IsNullOrEmpty(_adbPackage))
            {
                error = "ADB 设备或应用未识别";
                return false;
            }

            string adb = FindAdbPath();
            string controlDir = GetDeviceFilesRoot() + DeviceFrameRecorder.CONTROL_DIR;
            string commandPath = controlDir + "/" + DeviceFrameRecorder.COMMAND_FILE;
            string commandTempPath = commandPath + ".tmp";
            string statePath = controlDir + "/" + DeviceFrameRecorder.STATE_FILE;
            string stdout, stderr;
            int exit;

            if (!RunProcess(adb, BuildAdbArgs(_adbSerial, "shell mkdir -p " + controlDir),
                    out stdout, out stderr, out exit, 3000) || exit != 0)
            {
                error = "创建设备控制目录失败：" + (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim();
                return false;
            }

            if (resetState)
            {
                RunProcess(adb, BuildAdbArgs(_adbSerial,
                        "shell rm -f " + statePath + " " + commandPath + " " + commandTempPath),
                    out stdout, out stderr, out exit, 3000);
            }

            string content = action + ":" + session;
            if (action == "start")
            {
                content += _remoteLuaCapture ? ":lua=1" : ":lua=0";
            }
            // 同目录临时文件 + mv，避免设备轮询撞上 printf 原地写入而吞掉空/半条命令。
            string shell = "shell \"printf '" + content + "' > '" + commandTempPath +
                           "' && mv -f '" + commandTempPath + "' '" + commandPath + "'\"";
            if (!RunProcess(adb, BuildAdbArgs(_adbSerial, shell),
                    out stdout, out stderr, out exit, 3000) || exit != 0)
            {
                error = "发送设备分段命令失败：" + (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim();
                return false;
            }
            return true;
        }

        private bool WaitForDeviceFrameState(string expectedState, string session, double timeoutSeconds, out string error)
        {
            error = null;
            string adb = FindAdbPath();
            string statePath = GetDeviceFilesRoot() + DeviceFrameRecorder.CONTROL_DIR + "/" + DeviceFrameRecorder.STATE_FILE;
            string expectedPrefix = expectedState + ":" + session + ":";
            double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
            string lastState = "";
            while (EditorApplication.timeSinceStartup < deadline)
            {
                string stdout, stderr;
                int exit;
                if (RunProcess(adb, BuildAdbArgs(_adbSerial, "shell cat " + statePath),
                        out stdout, out stderr, out exit, 1000) && exit == 0)
                {
                    lastState = stdout.Trim();
                    if (lastState.StartsWith(expectedPrefix, System.StringComparison.Ordinal))
                    {
                        return true;
                    }
                    if (lastState.StartsWith("error:" + session + ":", System.StringComparison.Ordinal))
                    {
                        error = lastState.Substring(("error:" + session + ":").Length);
                        return false;
                    }
                }
                System.Threading.Thread.Sleep(100);
            }

            error = string.Format("等待设备状态 {0} 超时{1}", expectedState,
                string.IsNullOrEmpty(lastState) ? "" : "（最后状态：" + lastState + "）");
            return false;
        }

        private string GetDeviceFilesRoot()
        {
            return "/sdcard/Android/data/" + _adbPackage + "/files";
        }

        private bool TrySelectAdbProfilerTarget()
        {
            if (IsAdbProfilerConnected())
            {
                EndAdbProfilerTargetPolling();
                _statusLine = string.Format("ADB 已连接：{0} / {1}；Lua {2}。",
                    _adbSerial, _adbPackage, GetRemoteLuaStatus());
                Repaint();
                return true;
            }
            return false;
        }

        private bool IsAdbProfilerConnected()
        {
            if (string.IsNullOrEmpty(_adbSerial))
            {
                return false;
            }

            int guid = GetConnectedProfilerReflect();
            string id = GetConnectionIdentifierReflect(guid);
            bool isDeviceUrl = guid == 65262 &&
                string.Equals(GetDirectConnectionUrlReflect(), "device://" + _adbSerial,
                    System.StringComparison.OrdinalIgnoreCase);
            bool isAndroidDevice = !string.IsNullOrEmpty(id) &&
                id.IndexOf("@ADB:" + _adbSerial, System.StringComparison.OrdinalIgnoreCase) >= 0;
            return isDeviceUrl || isAndroidDevice;
        }

        private void BeginAdbProfilerTargetPolling()
        {
            EndAdbProfilerTargetPolling();
            _adbProfilerSelectPending = true;
            _adbProfilerSelectDeadline = EditorApplication.timeSinceStartup + 10.0;
            EditorApplication.update += PollAdbProfilerTarget;
        }

        private void PollAdbProfilerTarget()
        {
            if (!_adbProfilerSelectPending)
            {
                return;
            }
            if (TrySelectAdbProfilerTarget())
            {
                return;
            }
            if (EditorApplication.timeSinceStartup >= _adbProfilerSelectDeadline)
            {
                EndAdbProfilerTargetPolling();
                _statusLine = "ADB 已识别设备，但 Unity Profiler 未连接成功。" +
                              "确认应用为 Development Build 且仍在运行后点重新连接。";
                Repaint();
            }
        }

        private void EndAdbProfilerTargetPolling()
        {
            if (_adbProfilerSelectPending)
            {
                EditorApplication.update -= PollAdbProfilerTarget;
                _adbProfilerSelectPending = false;
            }
        }

        private void DisconnectAdb(bool updateStatus)
        {
            EndAdbProfilerTargetPolling();
            bool deviceCaptureStopped = StopDeviceFrameCaptureBestEffort();
            if (!deviceCaptureStopped && updateStatus)
            {
                _statusLine = "设备分段会话尚未确认停止，已保留连接供重试。";
                EditorUtility.DisplayDialog("AI Profiler",
                    _statusLine + "\n请保持应用前台与 USB 连接后再次点断开。", "OK");
                Repaint();
                return;
            }
            if (!deviceCaptureStopped)
            {
                Debug.LogWarning("[AIProfiler] 关闭窗口时未收到设备分段停止确认；stop 命令若已写入，会在应用恢复后执行。");
            }
            Lua.RemoteDisconnect();

            if (IsAdbProfilerConnected())
            {
                int[] guids = GetAvailableProfilersReflect();
                if (guids != null)
                {
                    foreach (int guid in guids)
                    {
                        if (string.Equals(GetConnectionIdentifierReflect(guid), "Editor",
                            System.StringComparison.OrdinalIgnoreCase))
                        {
                            SetConnectedProfilerReflect(guid);
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(_adbSerial))
            {
                string adb = FindAdbPath();
                string stdout, stderr;
                int exit;
                RunProcess(adb, BuildAdbArgs(_adbSerial, "forward --remove tcp:" + UNITY_PROFILER_ADB_PORT),
                    out stdout, out stderr, out exit);
                RunProcess(adb, BuildAdbArgs(_adbSerial, "forward --remove tcp:" + LUA_PROFILER_ADB_PORT),
                    out stdout, out stderr, out exit);
            }

            _adbSerial = "";
            _adbPackage = "";
            _remoteHookReady = false;
            _remoteHookCapturing = false;
            if (updateStatus)
            {
                _statusLine = "ADB 已断开。";
                Repaint();
            }
        }

        private void StopRecord()
        {
            if (_state != State.Recording)
            {
                return;
            }
            // 以用户点击 Stop 为采样边界；后续设备 flush / adb pull 期间不再接收 Lua 样本。
            _state = State.Stopped;
            StopPollutionWatch(); // 只停监听，污染统计保留到导出/CleanRecord
            string deviceSegmentError = null;
            if (_mode == CaptureMode.RemoteDevice)
            {
                try
                {
                    EditorUtility.DisplayProgressBar("AI Profiler", "正在停止设备分段并自动拉取…", 0.5f);
                    StopDeviceFrameCaptureAndPull(out deviceSegmentError);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }
            if (_mode == CaptureMode.EditorLocal)
            {
                Lua.IsSampling = false;
                // 取回界面/场景/内存采集并关闭运行时采集器
                TryStopViewStatsCapture();
            }
            ProfilerDriver.enabled = false; // 冻结帧缓冲（连接模式下冻结的是设备帧缓冲）

            if (_mode == CaptureMode.EditorLocal && _binLogging)
            {
                StopBinLog(); // 收尾最后一段 .raw（导出从这些段读，不依赖 live 帧缓冲）
                PruneInvalidSegments();
            }
            if (_mode == CaptureMode.EditorLocal)
            {
                // 与 StartRecord 对称：还原运行时 Profiler 采样总闸。无上限路径 StopBinLog 已关，这里幂等兜底；
                // 关无上限的 live 路径必须靠这里关，否则录制结束后运行时 Profiler 仍开着、白白叠加采样开销。
                // 帧缓冲此前已由 ProfilerDriver.enabled=false 冻结，关样本闸不影响已采集帧的读取。
                UnityEngine.Profiling.Profiler.enabled = false;
            }

            _firstFrame = ProfilerDriver.firstFrameIndex;
            _lastFrame = ProfilerDriver.lastFrameIndex;

            // 读取计数器统计并释放。真机模式 _activeCounters 为空（未启动 ProfilerRecorder），此循环空跑，
            // _counterStats 留空 → 由 AIProfilerExporter 从设备帧数据填充。
            _counterStats.Clear();
            var samples = new List<ProfilerRecorderSample>();
            foreach (var ac in _activeCounters)
            {
                var cs = new AIProfilerExporter.CounterStat
                {
                    label = ac.label,
                    category = ac.category,
                    isMemory = ac.isMemory
                };
                if (ac.recorder.Valid)
                {
                    samples.Clear();
                    if (ac.recorder.Count > 0)
                    {
                        ac.recorder.CopyTo(samples, false); // CopyTo(List,bool) 返回 void
                    }
                    if (samples.Count > 0)
                    {
                        double min = double.MaxValue, max = double.MinValue, sum = 0;
                        for (int i = 0; i < samples.Count; i++)
                        {
                            double v = samples[i].Value;
                            if (v < min) min = v;
                            if (v > max) max = v;
                            sum += v;
                        }
                        cs.min = min;
                        cs.max = max;
                        cs.avg = sum / samples.Count;
                        cs.last = samples[samples.Count - 1].Value;
                        cs.sampleCount = samples.Count;
                        cs.valid = true;
                        // 头/尾窗口均值（泄漏斜率信号）：各取 min(300, n/2) 个样本，样本太少不给趋势
                        int win = Mathf.Min(AIProfilerExporter.TREND_WINDOW_SAMPLES, samples.Count / 2);
                        if (win >= AIProfilerExporter.TREND_MIN_WINDOW)
                        {
                            double headSum = 0, tailSum = 0;
                            for (int i = 0; i < win; i++)
                            {
                                headSum += samples[i].Value;
                                tailSum += samples[samples.Count - win + i].Value;
                            }
                            cs.headAvg = headSum / win;
                            cs.tailAvg = tailSum / win;
                            cs.trendValid = true;
                        }
                    }
                    else
                    {
                        double v = ac.recorder.LastValue;
                        cs.min = cs.max = cs.avg = cs.last = v;
                        cs.sampleCount = 0;
                        cs.valid = true;
                    }
                }
                _counterStats.Add(cs);
            }
            DisposeActiveCounters();

            if (_gpuSampling)
            {
                EditorApplication.update -= OnEditorUpdateSampleGpu;
                _gpuSampling = false;
            }

            int luaFuncs, luaSamples;
            lock (_luaLock)
            {
                luaFuncs = _luaAgg.Count;
                luaSamples = _luaSampleCount;
            }
            _hasCapture = true;
            _state = State.Stopped;
            string segNote = "";
            if (_mode == CaptureMode.EditorLocal && _unlimited && _segFiles.Count > 0)
            {
                segNote = string.Format("，.raw 段 {0}", _segFiles.Count);
            }
            else if (_mode == CaptureMode.RemoteDevice && _deviceSegFiles.Count > 0)
            {
                segNote = string.Format("，设备 .raw 段 {0}（已自动拉取）", _deviceSegFiles.Count);
            }
            else if (_mode == CaptureMode.RemoteDevice && !string.IsNullOrEmpty(deviceSegmentError))
            {
                segNote = "，⚠ 自动分段失败：" + deviceSegmentError;
            }
            _statusLine = string.Format("已停止：Lua 函数 {0} 个/采样 {1} 条{2}。{3}",
                luaFuncs, luaSamples, segNote,
                _mode == CaptureMode.RemoteDevice && _deviceSegFiles.Count == 0
                    ? "ExportForAI 时会自动重试设备分段收尾/拉取。"
                    : "可 Export For AI。");
        }

        /// <summary>
        /// Editor 本地模式 StartRecord：开启运行时采集器 AIProfilerCapture（界面/场景采集 + 脚本 VM 内存周期采样）。
        /// 非 Play 时跳过、不阻断录制——导出时对应 section 标 NO DATA。
        /// </summary>
        private void TryStartViewStatsCapture()
        {
            _viewStatsText = "";
            _luaMemTrendText = "";
            _viewStatsFrameBase = -1;
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[AIProfiler] 未在 Play 中，本次导出将无 VIEW_STATS/SCENE_SWITCH/LUA_MEM 数据");
                return;
            }
            AIProfilerCapture.BeginCapture();
            _viewStatsFrameBase = AIProfilerCapture.CaptureStartFrame;
        }

        /// <summary>
        /// Editor 本地模式 StopRecord/OnDisable：取回并关闭运行时采集器。已退出 Play 时缓冲随域重载丢失（字段留空）。
        /// </summary>
        private void TryStopViewStatsCapture()
        {
            _viewStatsText = "";
            _luaMemTrendText = "";
            if (!AIProfilerCapture.IsCapturing)
            {
                return;
            }
            string view, mem;
            AIProfilerCapture.EndCapture(out view, out mem);
            _viewStatsText = view ?? "";
            _luaMemTrendText = mem ?? "";
        }

        private void OnEditorUpdateSampleGpu()
        {
            FrameTimingManager.CaptureFrameTimings();
            var timings = new FrameTiming[1];
            uint n = FrameTimingManager.GetLatestTimings(1, timings);
            if (n > 0)
            {
                double gpu = timings[0].gpuFrameTime;
                double cpu = timings[0].cpuFrameTime;
                if (gpu > 0)
                {
                    if (gpu < _gpuMin) _gpuMin = gpu;
                    if (gpu > _gpuMax) _gpuMax = gpu;
                    _gpuSum += gpu;
                    _cpuSum += cpu;
                    _gpuCount++;
                }
            }

            if (_binLogging)
            {
                RotateSegmentIfNeeded();
            }
        }

        // === 编辑器本地"无上限"分段 binary log ===
        // 思路：录制期只把 Unity 帧流式落盘（不在主线程解析）；Deep 用更小的帧数硬闸，
        // 非 Deep 用 SEG_FRAMES，另以文件体积作双重保护。
        // （因为 LoadProfile 单次最多回放 2000 帧，是 Unity 硬限制）；导出时逐段 LoadProfile + 累加。
        private void StartBinLog()
        {
            try
            {
                string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
                string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _segDir = System.IO.Path.Combine(projectRoot, "ProfilerLogs", "raw", stamp);
                System.IO.Directory.CreateDirectory(_segDir);
                _segFiles.Clear();
                _segIndex = 0;
                UnityEngine.Profiling.Profiler.maxUsedMemory = (int)BINLOG_MAX_USED_MEMORY; // 防"Receiver can not keep up"丢帧
                // 关闭 GC.Alloc 调用栈记录：导出只把 GC 归因到父 marker 名（不消费调用栈），
                // 但开着会让每个 alloc 在 binary log 里附整条 managed 调用栈，是单帧体积的大头。
                // 关掉可在保留 Deep 计时数据的前提下大幅缩小单帧体积。停录时还原原值，不影响其他工具。
                _prevAllocCallstacks = UnityEngine.Profiling.Profiler.enableAllocationCallstacks;
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = false;
                _binLogging = true;
                StartBinLogSegment();
            }
            catch (System.Exception e)
            {
                _binLogging = false;
                Debug.LogWarning("[AIProfiler] 启动分段 binary log 失败，回退为有上限录制: " + e.Message);
            }
        }

        // 开一个新段：先显式关闭当前 binary log，再设置新 logFile。
        // 只改 logFile 依赖 Unity 内部隐式收尾，Deep 长录时容易留下 LoadProfile 读不回来的段。
        private void StartBinLogSegment()
        {
            if (_binLogging && _segFiles.Count > 0)
            {
                UnityEngine.Profiling.Profiler.enabled = false;
                UnityEngine.Profiling.Profiler.enableBinaryLog = false;
                UnityEngine.Profiling.Profiler.logFile = ""; // flush/close 当前段
            }
            string baseName = System.IO.Path.Combine(_segDir, "seg_" + _segIndex.ToString("D4"));
            UnityEngine.Profiling.Profiler.logFile = baseName;
            UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            UnityEngine.Profiling.Profiler.enabled = true;
            _segFiles.Add(baseName + ".raw"); // 实际文件名带 .raw，供导出 LoadProfile
            _segStartFrame = ProfilerDriver.lastFrameIndex;
        }

        private void RotateSegmentIfNeeded()
        {
            // 体积优先：Deep 下按帧数轮转会胀到几十 GB，改为当前段 .raw 累积字节到顶就轮转；
            // 帧数仅作次级安全闸（避免极端低分配场景下单段帧数超过 LoadProfile 2000 帧回放上限）。
            long curBytes = 0;
            if (_segFiles.Count > 0)
            {
                try
                {
                    var fi = new System.IO.FileInfo(_segFiles[_segFiles.Count - 1]);
                    if (fi.Exists) curBytes = fi.Length;
                }
                catch { }
            }
            int cur = ProfilerDriver.lastFrameIndex;
            int frameLimit = GetSegmentFrameLimit();
            bool bySize = curBytes >= SEG_MAX_BYTES;
            bool byFrames = cur - _segStartFrame >= frameLimit;
            if (bySize || byFrames)
            {
                _segIndex++;
                StartBinLogSegment();
            }
        }

        private static int GetSegmentFrameLimit()
        {
            return ProfilerDriver.deepProfiling ? SEG_DEEP_FRAMES : SEG_FRAMES;
        }

        private void StopBinLog()
        {
            try
            {
                UnityEngine.Profiling.Profiler.enabled = false;
                UnityEngine.Profiling.Profiler.enableBinaryLog = false;
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = _prevAllocCallstacks; // 还原 alloc 调用栈开关
                UnityEngine.Profiling.Profiler.logFile = ""; // 置空会 flush/close 最后一段
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 停止 binary log 异常: " + e.Message);
            }
            _binLogging = false;
        }

        // === 录制期采样流污染监听（字段注释见声明处）===
        private void StartPollutionWatch()
        {
            if (_pollutionWatching)
            {
                return;
            }
            Application.logMessageReceived += OnLogMessagePollutionWatch;
            _pollutionWatching = true;
        }

        private void StopPollutionWatch()
        {
            if (!_pollutionWatching)
            {
                return;
            }
            Application.logMessageReceived -= OnLogMessagePollutionWatch;
            _pollutionWatching = false;
        }

        private void OnLogMessagePollutionWatch(string condition, string stackTrace, LogType type)
        {
            if (_state != State.Recording || string.IsNullOrEmpty(condition))
            {
                return;
            }
            if (condition.IndexOf("Profiler.EndSample", System.StringComparison.Ordinal) < 0 &&
                condition.IndexOf("Profiler.BeginSample", System.StringComparison.Ordinal) < 0)
            {
                return;
            }
            _pollutionCount++;
            _pollutionLastSeg = _binLogging ? _segIndex : -1;
            if (_pollutionCount == 1)
            {
                // 告警正文的 Previous samples 列着污染源所在 dll——留首条全量线索给面板/META。
                _pollutionFirstMsg = AIProfilerExporter.OneLine(condition, 400);
                _pollutionFirstSeg = _pollutionLastSeg;
                // 只主动告警一次：污染告警本身已在每帧刷 Console，这里不再放大
                Debug.LogWarning("[AIProfiler] 检测到采样流污染（Profiler Begin/End 配对断裂）：污染期写出的 .raw 段大概率损坏。" +
                    "定位泄漏源：用 Unity Profiler 窗口 Record(Deep) 复现后执行菜单 Window/Analysis/AI Profiler Dump Suspect Frames。首条：" + _pollutionFirstMsg);
                Repaint();
            }
        }

        private void PruneInvalidSegments()
        {
            for (int i = _segFiles.Count - 1; i >= 0; i--)
            {
                string seg = _segFiles[i];
                bool keep = false;
                try
                {
                    var fi = new System.IO.FileInfo(seg);
                    keep = fi.Exists && fi.Length > 0;
                }
                catch
                {
                    keep = false;
                }
                if (!keep)
                {
                    Debug.LogWarning("[AIProfiler] 移除无效 .raw 段（缺失或 0 字节）: " + seg);
                    _segFiles.RemoveAt(i);
                }
            }
        }

        private void TryDeleteSegments()
        {
            try
            {
                string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
                string rawRoot = System.IO.Path.Combine(projectRoot, "ProfilerLogs", "raw");
                if (System.IO.Directory.Exists(rawRoot))
                {
                    System.IO.Directory.Delete(rawRoot, true);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 清理 ProfilerLogs/raw 失败: " + e.Message);
            }
            _segFiles.Clear();
            _segDir = null;
        }

        private void ExportForAI()
        {
            if (!_hasCapture)
            {
                EditorUtility.DisplayDialog("AI Profiler",
                    "还没有可导出的数据。\n请先 StartRecord → 操作一段 → StopRecord。", "OK");
                return;
            }

            if (_mode == CaptureMode.RemoteDevice && _deviceSegFiles.Count == 0)
            {
                string retryError;
                bool ready;
                try
                {
                    EditorUtility.DisplayProgressBar("AI Profiler", "正在重试设备分段收尾/拉取…", 0.5f);
                    ready = string.IsNullOrEmpty(_deviceCaptureSession)
                        ? PullDeviceFrameSegments(out retryError)
                        : StopDeviceFrameCaptureAndPull(out retryError);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                if (!ready)
                {
                    _statusLine = "设备自动分段尚未就绪：" + retryError;
                    EditorUtility.DisplayDialog("AI Profiler", _statusLine + "\n请保持应用前台与 USB 连接后重试 ExportForAI。", "OK");
                    return;
                }
            }

            bool isDevice = _mode == CaptureMode.RemoteDevice;
            if (_mode == CaptureMode.EditorLocal && _unlimited && _segFiles.Count > 0)
            {
                PruneInvalidSegments();
            }
            var result = new AIProfilerExporter.CaptureResult
            {
                firstFrame = _firstFrame,
                lastFrame = _lastFrame,
                counters = new List<AIProfilerExporter.CounterStat>(_counterStats),
                deepProfilingOn = ProfilerDriver.deepProfiling,
                deepLuaNativeOn = AIProfilerCapture.IsCompetingLuaProfilerActive != null && AIProfilerCapture.IsCompetingLuaProfilerActive(),
                luaBackend = Lua.Name,
                mikuDeepOn = Lua.IsAvailable && (isDevice ? _remoteLuaCapture : Lua.DeepLuaEnabled),
                mikuHookReady = Lua.IsAvailable && (isDevice ? (_remoteLuaCapture && RemoteHookReady) : Lua.IsHookReady),
                captureMode = isDevice ? "device" : "editor",
                countersFromFrameData = isDevice, // 真机：计数器从设备帧数据取（_counterStats 为空，由 Exporter 填充）
                connectionName = isDevice ? GetCurrentConnectionName() : "",
                deviceEndpoint = isDevice
                    ? (string.IsNullOrEmpty(_adbSerial) ? "adb:未连接" : ("adb:" + _adbSerial))
                    : "",
                recordPollutionCount = _pollutionCount,
                recordPollutionFirstMsg = _pollutionFirstMsg,
                recordPollutionSegRange = _pollutionFirstSeg >= 0
                    ? string.Format("seg_{0:D4}..seg_{1:D4}", _pollutionFirstSeg, _pollutionLastSeg)
                    : "",
                viewStats = _viewStatsText ?? "",
                luaMemTrend = _luaMemTrendText ?? "",
                viewStatsFrameBase = _viewStatsFrameBase,
            };
            lock (_luaLock)
            {
                result.luaAggPre = new Dictionary<string, AIProfilerExporter.LuaAgg>(_luaAgg);
            }
            if (_mode == CaptureMode.EditorLocal && _unlimited && _segFiles.Count > 0)
            {
                result.rawSegmentFiles = new List<string>(_segFiles);
            }
            else if (_mode == CaptureMode.RemoteDevice && _deviceSegFiles.Count > 0)
            {
                // 真机：用 adb pull 下来的设备帧段替代 live 2000 帧 ring（突破上限）。
                // countersFromFrameData 已为 true，内存/GPU 计数器也从段内全区间逐段累加。
                result.rawSegmentFiles = new List<string>(_deviceSegFiles);
            }
            if (_gpuCount > 0)
            {
                result.gpuTimingValid = true;
                result.gpuMinMs = _gpuMin == double.MaxValue ? 0 : _gpuMin;
                result.gpuMaxMs = _gpuMax;
                result.gpuAvgMs = _gpuSum / _gpuCount;
                result.cpuTimingAvgMs = _cpuSum / _gpuCount;
                result.gpuTimingSampleCount = _gpuCount;
            }

            string path = AIProfilerExporter.Export(result);
            if (!string.IsNullOrEmpty(path))
            {
                string relativePath = "Assets/ProfilerLogs/" + System.IO.Path.GetFileName(path);
                if (result.segLoadFailed > 0 || result.segLoadEmpty > 0)
                {
                    _statusLine = string.Format("已导出但原生数据不完整：失败 {0} 段，空段 {1} 段。",
                        result.segLoadFailed, result.segLoadEmpty);
                    string pollutionHint = (result.recordPollutionCount > 0 || result.replayPollutionCount > 0)
                        ? "\n检测到采样流污染（Profiler Begin/End 配对断裂）——失败段成因是污染期段落盘损坏，非内存不足；泄漏源排查见 META 污染条目。"
                        : "";
                    EditorUtility.DisplayDialog("AI Profiler 数据不完整",
                        _statusLine + pollutionHint + "\n报告必须标灰并重采。文件：" + relativePath + "\n逐段失败原因见其 META。", "OK");
                }
                else if (result.mikuDeepOn && result.luaAggPre.Count == 0)
                {
                    _statusLine = "已导出但 Lua 数据为空。";
                    EditorUtility.DisplayDialog("AI Profiler 数据不完整",
                        _statusLine + "\n报告必须标灰并重采。文件：" + relativePath, "OK");
                }
                else
                {
                    _statusLine = "已导出: " + relativePath;
                }
            }
        }

        private void DisposeActiveCounters()
        {
            foreach (var ac in _activeCounters)
            {
                if (ac.recorder.Valid)
                {
                    ac.recorder.Dispose();
                }
            }
            _activeCounters.Clear();
        }
        #endregion

        #region GUI
        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("AI Profiler — 一键录制 + 导出供 AI 分析", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            // 采样模式（录制中禁切）
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("采样模式", GUILayout.Width(60));
                GUI.enabled = _state == State.Idle && string.IsNullOrEmpty(_deviceCaptureSession);
                int newModeIdx = GUILayout.Toolbar((int)_mode, _modeTabs, GUILayout.Height(24));
                if (newModeIdx != (int)_mode)
                {
                    SwitchMode((CaptureMode)newModeIdx);
                }
                GUI.enabled = true;
            }
            if (_mode == CaptureMode.RemoteDevice)
            {
                DrawRemoteDevicePanel();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = _state != State.Recording;
                    bool nu = EditorGUILayout.ToggleLeft(
                        "无上限录制（磁盘分段 binary log，突破 2000 帧；导出时逐段解析）", _unlimited);
                    if (nu != _unlimited)
                    {
                        _unlimited = nu;
                    }
                    GUI.enabled = true;
                }
            }
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _state != State.Recording;
                if (GUILayout.Button("StartRecord", GUILayout.Height(38)))
                {
                    StartRecord();
                }
                GUI.enabled = _state == State.Recording;
                if (GUILayout.Button("StopRecord", GUILayout.Height(38)))
                {
                    StopRecord();
                }
                GUI.enabled = _state == State.Stopped && _hasCapture;
                if (GUILayout.Button("ExportForAI", GUILayout.Height(38)))
                {
                    ExportForAI();
                }
                // 没有记录时置灰：仅在已采集到记录（非录制中）时可清
                GUI.enabled = _hasCapture && _state != State.Recording;
                if (GUILayout.Button("CleanRecord", GUILayout.Height(38)))
                {
                    CleanRecord();
                }
                GUI.enabled = true;
            }

            if (_pollutionCount > 0)
            {
                EditorGUILayout.HelpBox(string.Format(
                    "采样流污染：录制期捕获 {0} 条 Profiler Begin/End 配对断裂告警，污染期写出的段大概率损坏（导出 LoadProfile 会失败）。\n" +
                    "定位泄漏源：用 Unity Profiler 窗口 Record(Deep) 复现后执行菜单 Window/Analysis/AI Profiler Dump Suspect Frames。\n首条：{1}",
                    _pollutionCount, _pollutionFirstMsg), MessageType.Error);
            }

            EditorGUILayout.Space(8);

            // 开关状态
            EditorGUILayout.LabelField("深度开关（首次打开自动开启一次，可点 ON/OFF 切换）", EditorStyles.miniBoldLabel);
            if (_mode == CaptureMode.RemoteDevice)
            {
                EditorGUILayout.LabelField("Unity Deep Profile：由设备打包期决定，面板不强制", EditorStyles.miniLabel);
                DrawSwitch("Lua 远程 (" + Lua.Name + " via TCP)", _remoteLuaCapture && Lua.IsAvailable && !Lua.IsLocal && Lua.DeepLuaEnabled);
            }
            else
            {
                if (DrawSwitchButton("Unity Deep Profile (C#/GPU/内存/GC)", ProfilerDriver.deepProfiling))
                    ToggleUnityDeepProfile();
                if (Lua.IsAvailable)
                {
                    if (DrawSwitchButton("Lua 深度采样 (" + Lua.Name + ")", Lua.DeepLuaEnabled))
                        Lua.DeepLuaEnabled = !Lua.DeepLuaEnabled;
                }
                else
                {
                    EditorGUILayout.LabelField("Lua 深度采样：未接入 Lua 后端（无 Lua 工程可忽略；Miku 工程加 AI_PROFILER_MIKU 宏）", EditorStyles.miniLabel);
                }
            }
            if (_mode == CaptureMode.EditorLocal)
            {
                EditorGUILayout.LabelField(string.Format("帧缓冲上限：{0}（目标 {1}；StartRecord 时设置）",
                    _effectiveFrameBudget > 0 ? _effectiveFrameBudget.ToString() : "未知/默认 300",
                    MAX_FRAME_HISTORY), EditorStyles.miniLabel);
                if (_unlimited)
                {
                    EditorGUILayout.LabelField(string.Format(
                        "无上限模式：Deep 每 {0} 帧（非 Deep {1} 帧）或约 {2}MB 滚动一段，CPU/GC/时间线总帧数不受 2000 限制。",
                        SEG_DEEP_FRAMES, SEG_FRAMES, SEG_MAX_MB),
                        EditorStyles.miniLabel);
                }
            }
            else
            {
                EditorGUILayout.LabelField("设备帧：32MB / 600 帧滚小段，完整段实时后台 ADB 拉取。", EditorStyles.miniLabel);
            }

            if (_mode == CaptureMode.EditorLocal && !ProfilerDriver.deepProfiling && EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Deep Profile 未开启。请退出 Play、重新打开本面板（会触发一次脚本重编译），再进 Play。",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(6);
            if (_state == State.Recording)
            {
                if (_mode == CaptureMode.RemoteDevice)
                {
                    string pullState = string.IsNullOrEmpty(_deviceSegmentPullError)
                        ? string.Format("PC 已接收 {0} 段", _deviceSegFiles.Count)
                        : "实时传输重试中";
                    EditorGUILayout.LabelField("状态：录制中（真机小段实时传输，" + pullState + "）");
                }
                else
                {
                    int cur = ProfilerDriver.lastFrameIndex - ProfilerDriver.firstFrameIndex;
                    string segInfo = _binLogging ? string.Format("  已写 {0} 段(.raw)", _segFiles.Count) : "";
                    EditorGUILayout.LabelField(string.Format("状态：录制中  已录约 {0} 帧{1}", Mathf.Max(0, cur), segInfo));
                }
            }
            else if (!string.IsNullOrEmpty(_statusLine))
            {
                EditorGUILayout.LabelField("状态：" + _statusLine, EditorStyles.wordWrappedLabel);
            }
            else
            {
                EditorGUILayout.LabelField("状态：空闲");
            }

            EditorGUILayout.Space(6);
            if (_mode == CaptureMode.RemoteDevice)
            {
                EditorGUILayout.HelpBox(
                    "真机流程：1) 设备装 Development 包(含 AI_PROFILER_DEVICE；要 Lua 再加后端宏如 USE_LUA_PROFILER)，USB 连接并授权 adb\n" +
                    "  2) 需要 Lua 时：设备上触发 AIProfilerDeviceControl.OpenLuaProfiler()（如 GM 菜单）→ 完整退出并重启（标记仅生效一次）\n" +
                    "  3) 本面板点 ADB 一键连接；等 Lua 显示 Hook 已就绪，再 StartRecord → 操作 → StopRecord → ExportForAI。\n" +
                    "若场景易崩，连接前关闭“同时采集 Lua”，先用原生安全模式采 CPU/GPU/内存/GC。无需手机 IP/同一局域网。\n" +
                    "数据来源：C#/GPU/内存/GC 走 adb→Unity Profiler，Lua 走 adb→Lua 后端 TCP；导出到 Assets/ProfilerLogs/，用 profiler-analysis 技能分析。\n" +
                    "注意：真机 GC 不含编辑器工件，GPU 渲染计数器相对可信；Lua 插桩仍放大 Lua 绝对耗时（看相对占比）。\n" +
                    "Unity 帧：设备按 32MB / 600 帧滚小段，完整段实时后台拉到 PC；StopRecord 只收尾并拉取最后剩余段。",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "流程：1) 保持本面板打开  2) 进 Play（有 Lua 后端时确认其 Hook 已装，如 Miku 的绿色 OnStartGame）  3) StartRecord  4) 操作几秒  5) StopRecord → ExportForAI。\n" +
                    "数据来源：C#/GPU/内存/GC 来自 Unity 原生 Profiler，Lua 来自 Lua 后端（含 Lua VM GC；无后端则 NO DATA）；\n" +
                    "导出到 Assets/ProfilerLogs/，用 profiler-analysis 技能分析。\n" +
                    "注意：Deep 插桩绝对耗时被放大（看相对占比）；Editor 内 GPU 逐项不可靠。",
                    MessageType.Info);
                if (_unlimited)
                {
                    EditorGUILayout.HelpBox(
                        "无上限模式已开：录制期把 Unity 帧分段流式写到 <项目>/ProfilerLogs/raw/<时间戳>/seg_*.raw（不进 Assets，不丢早期帧）。\n" +
                        string.Format("默认按约 {0}MB / Deep {1} 帧（非 Deep {2} 帧）滚段，ExportForAI 时逐段 LoadProfile 解析累加；CleanRecord 会删除这些 .raw 段。\n",
                            SEG_MAX_MB, SEG_DEEP_FRAMES, SEG_FRAMES) +
                        "注意：① binary log 在被测进程内写盘有少量开销（本地绝对值本就只看相对占比）；② Lua 采样到达即聚合，同样无上限。",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("当前未开无上限：CPU/GC/时间线仍受 Unity 原生 2000 帧上限，长录会丢早期帧。", MessageType.None);
                }
            }

            if (_state == State.Recording)
            {
                Repaint();
            }
        }

        /// <summary>真机模式控制面板：ADB 自动发现设备并同时建立 Unity Profiler 与 Miku Lua 两条 USB 转发。</summary>
        private void DrawRemoteDevicePanel()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("真机 ADB USB 连接（Development 包 + AI_PROFILER_DEVICE）", EditorStyles.miniBoldLabel);

                using (new EditorGUI.DisabledScope(_state == State.Recording || !string.IsNullOrEmpty(_adbSerial) || !Lua.IsAvailable))
                {
                    _remoteLuaCapture = EditorGUILayout.ToggleLeft(
                        "同时采集 Lua（关闭 = 原生安全模式，适合先定位易崩场景）", _remoteLuaCapture && Lua.IsAvailable);
                }

                using (new EditorGUI.DisabledScope(_state == State.Recording))
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(string.IsNullOrEmpty(_adbSerial) ? "ADB 一键连接" : "ADB 重新连接", GUILayout.Width(120)))
                    {
                        ConnectAdb();
                    }
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_adbSerial)))
                    {
                        if (GUILayout.Button("断开", GUILayout.Width(56)))
                        {
                            DisconnectAdb(true);
                        }
                    }
                }

                EditorGUILayout.LabelField("设备: " + (string.IsNullOrEmpty(_adbSerial) ? "未连接" : _adbSerial),
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField("应用: " + (string.IsNullOrEmpty(_adbPackage) ? "未识别" : _adbPackage),
                    EditorStyles.miniLabel);
                DrawSwitch("Unity Profiler: " + GetCurrentConnectionName(), IsAdbProfilerConnected());
                DrawSwitch(_remoteLuaCapture ? "Lua: adb tcp:" + LUA_PROFILER_ADB_PORT : "Lua: 已禁用",
                    _remoteLuaCapture && IsLuaRemoteConnected());
                EditorGUILayout.LabelField(_remoteLuaCapture
                        ? "设备侧先触发 AIProfilerDeviceControl.OpenLuaProfiler()（如 GM 菜单）；标记只对下一次完整启动生效。"
                        : "原生安全模式不会建立 Lua TCP，也不会打开设备 Lua 采样；导出允许 Lua 数据为空。",
                    EditorStyles.miniLabel);
            }
        }

        private bool PrepareDeviceSegmentTransferSession(string session, out string error)
        {
            error = null;
            try
            {
                string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai_profiler_device_frames");
                string localDir = System.IO.Path.Combine(root, session);
                if (System.IO.Directory.Exists(localDir))
                {
                    System.IO.Directory.Delete(localDir, true);
                }
                System.IO.Directory.CreateDirectory(localDir);
                _deviceSegLocalDir = localDir;
                _deviceSegFiles.Clear();
                _deviceSegmentPullError = "";
                return true;
            }
            catch (System.Exception e)
            {
                error = "准备设备小段本地目录失败：" + e.Message;
                return false;
            }
        }

        private bool EnsureDeviceSegmentLocalDir(out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(_deviceSegLocalDir) && System.IO.Directory.Exists(_deviceSegLocalDir))
            {
                return true;
            }

            string session = string.IsNullOrEmpty(_deviceCaptureSession)
                ? System.DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : _deviceCaptureSession;
            try
            {
                _deviceSegLocalDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "ai_profiler_device_frames", session);
                System.IO.Directory.CreateDirectory(_deviceSegLocalDir);
                return true;
            }
            catch (System.Exception e)
            {
                error = "准备设备小段本地目录失败：" + e.Message;
                return false;
            }
        }

        private void BeginDeviceSegmentPullPolling()
        {
            EndDeviceSegmentPullPolling();
            _deviceSegmentPullPolling = true;
            _nextDeviceSegmentPullPoll = 0;
            EditorApplication.update += PollDeviceSegmentPull;
        }

        private void EndDeviceSegmentPullPolling()
        {
            if (!_deviceSegmentPullPolling)
            {
                return;
            }
            EditorApplication.update -= PollDeviceSegmentPull;
            _deviceSegmentPullPolling = false;
        }

        private void PollDeviceSegmentPull()
        {
            if (!_deviceSegmentPullPolling)
            {
                return;
            }

            if (_deviceSegmentPullTask != null)
            {
                if (!_deviceSegmentPullTask.IsCompleted)
                {
                    return;
                }

                DeviceSegmentPullResult completed;
                try
                {
                    completed = _deviceSegmentPullTask.GetAwaiter().GetResult();
                }
                catch (System.Exception e)
                {
                    completed = new DeviceSegmentPullResult { error = e.Message };
                }
                _deviceSegmentPullTask = null;
                bool consumed = ConsumeDeviceSegmentPullResult(completed);
                _nextDeviceSegmentPullPoll = EditorApplication.timeSinceStartup +
                    (consumed && completed != null && completed.found
                        ? 0.1
                        : DEVICE_PULL_POLL_INTERVAL_SECONDS);
            }

            if (_state != State.Recording || string.IsNullOrEmpty(_deviceCaptureSession) ||
                string.IsNullOrEmpty(_adbSerial) || string.IsNullOrEmpty(_adbPackage) ||
                EditorApplication.timeSinceStartup < _nextDeviceSegmentPullPoll || _deviceSegmentPullTask != null)
            {
                return;
            }

            string localError;
            if (!EnsureDeviceSegmentLocalDir(out localError))
            {
                _deviceSegmentPullError = localError;
                _nextDeviceSegmentPullPoll = EditorApplication.timeSinceStartup + DEVICE_PULL_POLL_INTERVAL_SECONDS;
                return;
            }

            string adb = FindAdbPath();
            string serial = _adbSerial;
            string session = _deviceCaptureSession;
            string deviceDir = GetDeviceFilesRoot() + DeviceFrameRecorder.FRAME_DIR;
            string localDir = _deviceSegLocalDir;
            _deviceSegmentPullTask = System.Threading.Tasks.Task.Run(
                () => PullOneReadyDeviceSegment(adb, serial, session, deviceDir, localDir));
        }

        private bool FinishActiveDeviceSegmentPull(out string error)
        {
            error = null;
            if (_deviceSegmentPullTask == null)
            {
                return true;
            }

            try
            {
                if (!_deviceSegmentPullTask.Wait(DEVICE_PULL_TIMEOUT_MS + 5000))
                {
                    error = "等待正在传输的设备小段超时";
                    return false;
                }
                DeviceSegmentPullResult result = _deviceSegmentPullTask.GetAwaiter().GetResult();
                _deviceSegmentPullTask = null;
                if (!ConsumeDeviceSegmentPullResult(result))
                {
                    error = _deviceSegmentPullError;
                    return false;
                }
                return true;
            }
            catch (System.Exception e)
            {
                _deviceSegmentPullTask = null;
                error = "等待设备小段传输失败：" + e.Message;
                return false;
            }
        }

        private bool ConsumeDeviceSegmentPullResult(DeviceSegmentPullResult result)
        {
            if (result == null)
            {
                _deviceSegmentPullError = "设备小段传输返回空结果";
                return false;
            }
            if (!string.IsNullOrEmpty(result.error))
            {
                if (!string.Equals(_deviceSegmentPullError, result.error, System.StringComparison.Ordinal))
                {
                    Debug.LogWarning("[AIProfiler] 设备小段实时传输失败，将自动重试: " + result.error);
                }
                _deviceSegmentPullError = result.error;
                return false;
            }
            _deviceSegmentPullError = "";
            if (!result.found)
            {
                return true;
            }

            bool exists = false;
            foreach (string file in _deviceSegFiles)
            {
                if (string.Equals(file, result.localPath, System.StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                _deviceSegFiles.Add(result.localPath);
                _deviceSegFiles.Sort(CompareDeviceSegmentFiles);
            }
            PersistDeviceRecoverySession();
            Repaint();
            return true;
        }

        private static DeviceSegmentPullResult PullOneReadyDeviceSegment(string adb, string serial,
            string session, string deviceDir, string localDir)
        {
            var result = new DeviceSegmentPullResult();
            string stdout, stderr;
            int exit;
            string listCommand = "shell \"ls -1 '" + deviceDir + "'/seg_*.ready 2>/dev/null\"";
            bool listed = RunProcess(adb, BuildAdbArgs(serial, listCommand),
                out stdout, out stderr, out exit, 3000);
            if (!listed || exit != 0)
            {
                if (string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr))
                {
                    return result;
                }
                result.error = "枚举设备 ready 小段失败：" +
                    (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                return result;
            }

            string[] lines = stdout.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            var markers = new List<string>();
            foreach (string line in lines)
            {
                string marker = line.Trim();
                if (marker.EndsWith(DeviceFrameRecorder.READY_EXTENSION, System.StringComparison.OrdinalIgnoreCase))
                {
                    markers.Add(marker);
                }
            }
            if (markers.Count == 0)
            {
                return result;
            }
            markers.Sort(System.StringComparer.Ordinal);
            string readyPath = markers[0];
            result.found = true;

            if (!RunProcess(adb, BuildAdbArgs(serial, "shell cat '" + readyPath + "'"),
                    out stdout, out stderr, out exit, 3000) || exit != 0)
            {
                result.error = "读取设备小段 ready 信息失败：" +
                    (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                return result;
            }

            string[] metadata = stdout.Trim().Split(':');
            int segmentIndex;
            long expectedLength;
            if (metadata.Length != 3 ||
                (!string.IsNullOrEmpty(session) && !string.Equals(metadata[0], session, System.StringComparison.Ordinal)) ||
                !int.TryParse(metadata[1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out segmentIndex) ||
                !long.TryParse(metadata[2], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out expectedLength) || expectedLength <= 0)
            {
                result.error = "设备小段 ready 信息无效：" + stdout.Trim();
                return result;
            }

            result.segmentIndex = segmentIndex;
            string fileName = "seg_" + segmentIndex.ToString("D4") + ".raw";
            string remoteRaw = deviceDir + "/" + fileName;
            string localRaw = System.IO.Path.Combine(localDir, fileName);
            string localPart = localRaw + ".part";
            try
            {
                System.IO.Directory.CreateDirectory(localDir);
                if (System.IO.File.Exists(localRaw) && new System.IO.FileInfo(localRaw).Length != expectedLength)
                {
                    System.IO.File.Delete(localRaw);
                }
                if (!System.IO.File.Exists(localRaw))
                {
                    if (System.IO.File.Exists(localPart))
                    {
                        System.IO.File.Delete(localPart);
                    }
                    if (!RunProcess(adb, BuildAdbArgs(serial,
                            string.Format("pull \"{0}\" \"{1}\"", remoteRaw, localPart)),
                            out stdout, out stderr, out exit, DEVICE_PULL_TIMEOUT_MS) || exit != 0)
                    {
                        result.error = "adb pull 设备小段失败：" +
                            (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                        return result;
                    }
                    long actualLength = new System.IO.FileInfo(localPart).Length;
                    if (actualLength != expectedLength)
                    {
                        result.error = string.Format("设备小段长度校验失败：seg_{0:D4}，设备 {1}B / PC {2}B",
                            segmentIndex, expectedLength, actualLength);
                        return result;
                    }
                    System.IO.File.Move(localPart, localRaw);
                }
            }
            catch (System.Exception e)
            {
                result.error = "保存设备小段失败：" + e.Message;
                return result;
            }

            string removeCommand = "shell \"rm -f '" + remoteRaw + "' '" + readyPath + "'\"";
            if (!RunProcess(adb, BuildAdbArgs(serial, removeCommand),
                    out stdout, out stderr, out exit, 3000) || exit != 0)
            {
                result.error = "确认设备小段完成后清理源文件失败：" +
                    (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                return result;
            }

            result.localPath = localRaw;
            return result;
        }

        private bool DrainReadyDeviceSegments(out string error)
        {
            error = null;
            if (!EnsureDeviceSegmentLocalDir(out error))
            {
                return false;
            }

            string adb = FindAdbPath();
            string deviceDir = GetDeviceFilesRoot() + DeviceFrameRecorder.FRAME_DIR;
            while (true)
            {
                DeviceSegmentPullResult result = PullOneReadyDeviceSegment(adb, _adbSerial,
                    _deviceCaptureSession, deviceDir, _deviceSegLocalDir);
                if (!ConsumeDeviceSegmentPullResult(result))
                {
                    error = _deviceSegmentPullError;
                    return false;
                }
                if (!result.found)
                {
                    return true;
                }
            }
        }

        /// <summary>StopRecord 自动调用：拉完尚未实时传输的 ready 小段并收集有效 .raw。</summary>
        private bool PullDeviceFrameSegments(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(_adbSerial) || string.IsNullOrEmpty(_adbPackage))
            {
                error = "ADB 设备或应用未识别";
                return false;
            }

            string devicePath = GetDeviceFilesRoot() + DeviceFrameRecorder.FRAME_DIR;
            if (!DrainReadyDeviceSegments(out error))
            {
                return false;
            }

            string ingestError;
            if (!IngestSegmentsFromDir(_deviceSegLocalDir, out ingestError))
            {
                // 兼容升级前已启动、尚未生成 .ready 的旧真机包：仅在本地一个有效段都没有时整目录兜底。
                string legacyDir = System.IO.Path.Combine(_deviceSegLocalDir, "legacy");
                try
                {
                    if (System.IO.Directory.Exists(legacyDir))
                    {
                        System.IO.Directory.Delete(legacyDir, true);
                    }
                    System.IO.Directory.CreateDirectory(legacyDir);
                }
                catch (System.Exception e)
                {
                    error = "准备旧版设备段兼容目录失败：" + e.Message;
                    return false;
                }

                string legacyStdout, legacyStderr;
                int legacyExit;
                if (!RunProcess(FindAdbPath(), BuildAdbArgs(_adbSerial,
                        string.Format("pull \"{0}\" \"{1}\"", devicePath, legacyDir)),
                        out legacyStdout, out legacyStderr, out legacyExit, 10 * 60 * 1000) || legacyExit != 0 ||
                    !IngestSegmentsFromDir(_deviceSegLocalDir, out ingestError))
                {
                    error = string.IsNullOrEmpty(ingestError)
                        ? "旧版设备段兼容拉取失败：" +
                          (string.IsNullOrWhiteSpace(legacyStderr) ? legacyStdout : legacyStderr).Trim()
                        : ingestError;
                    return false;
                }
            }

            string stdout, stderr;
            int exit;
            // 所有 ready 段均已逐个校验；清掉可能残留的空目录或旧包兼容文件。
            if (!RunProcess(FindAdbPath(), BuildAdbArgs(_adbSerial, "shell rm -rf " + devicePath),
                    out stdout, out stderr, out exit, 3000) || exit != 0)
            {
                Debug.LogWarning("[AIProfiler] 已实时拉取设备小段，但清理手机源目录失败: " +
                                 (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim());
            }
            return true;
        }

        /// <summary>递归收集非空 seg_*.raw，按文件名排序后供 ExportForAI 逐段累加。</summary>
        private bool IngestSegmentsFromDir(string dir, out string error)
        {
            List<string> valid;
            if (!CollectValidDeviceSegments(dir, out valid, out error))
            {
                return false;
            }
            if (valid.Count == 0)
            {
                error = "设备自动分段未生成有效 .raw 文件";
                return false;
            }

            _deviceSegFiles.Clear();
            _deviceSegFiles.AddRange(valid);
            _deviceSegLocalDir = dir;
            SessionState.SetString(kDeviceRecoveryLocalDir, dir);
            SessionState.SetString(kDeviceRecoverySerial, _adbSerial ?? "");
            SessionState.SetString(kDeviceRecoveryPackage, _adbPackage ?? "");
            SessionState.EraseString(kDeviceRecoverySession);
            Debug.Log(string.Format("[AIProfiler] 已自动拉取设备帧小段 {0} 个，源目录: {1}", valid.Count, dir));
            return true;
        }

        private static bool CollectValidDeviceSegments(string dir, out List<string> valid, out string error)
        {
            valid = new List<string>();
            error = null;
            string[] raws;
            try
            {
                raws = System.IO.Directory.GetFiles(dir, "seg_*.raw", System.IO.SearchOption.AllDirectories);
            }
            catch (System.Exception e)
            {
                error = "扫描设备段文件失败：" + e.Message;
                return false;
            }

            if (raws != null)
            {
                foreach (string raw in raws)
                {
                    try
                    {
                        if (new System.IO.FileInfo(raw).Length > 0)
                        {
                            valid.Add(raw);
                        }
                    }
                    catch { }
                }
            }
            valid.Sort(CompareDeviceSegmentFiles);
            return true;
        }

        private void PersistDeviceRecoverySession()
        {
            SessionState.SetString(kDeviceRecoverySession, _deviceCaptureSession ?? "");
            SessionState.SetString(kDeviceRecoverySerial, _adbSerial ?? "");
            SessionState.SetString(kDeviceRecoveryPackage, _adbPackage ?? "");
            SessionState.SetString(kDeviceRecoveryLocalDir, _deviceSegLocalDir ?? "");
        }

        private void RestoreDeviceRecoveryState()
        {
            string localDir = SessionState.GetString(kDeviceRecoveryLocalDir, "");
            string serial = SessionState.GetString(kDeviceRecoverySerial, "");
            string packageName = SessionState.GetString(kDeviceRecoveryPackage, "");
            string session = SessionState.GetString(kDeviceRecoverySession, "");

            if (!string.IsNullOrEmpty(session) && !string.IsNullOrEmpty(serial) && !string.IsNullOrEmpty(packageName))
            {
                _mode = CaptureMode.RemoteDevice;
                _adbSerial = serial;
                _adbPackage = packageName;
                _deviceCaptureSession = session;
                _deviceSegLocalDir = localDir;
                if (!string.IsNullOrEmpty(localDir) && System.IO.Directory.Exists(localDir))
                {
                    List<string> pulled;
                    string collectError;
                    if (CollectValidDeviceSegments(localDir, out pulled, out collectError))
                    {
                        _deviceSegFiles.Clear();
                        _deviceSegFiles.AddRange(pulled);
                    }
                    else
                    {
                        Debug.LogWarning("[AIProfiler] 恢复已实时拉取的小段失败: " + collectError);
                    }
                }
                _state = State.Idle;
                _statusLine = "检测到未回收的设备小段会话，正在自动恢复…";
                EditorApplication.delayCall += RecoverPendingDeviceCapture;
                return;
            }

            if (!string.IsNullOrEmpty(localDir) && System.IO.Directory.Exists(localDir))
            {
                _adbSerial = serial;
                _adbPackage = packageName;
                string ingestError;
                if (IngestSegmentsFromDir(localDir, out ingestError))
                {
                    _mode = CaptureMode.RemoteDevice;
                    _hasCapture = true;
                    _state = State.Stopped;
                    _statusLine = string.Format("已恢复待导出的设备帧段 {0} 个。", _deviceSegFiles.Count);
                    return;
                }
                Debug.LogWarning("[AIProfiler] 恢复设备帧段失败: " + ingestError);
                SessionState.EraseString(kDeviceRecoveryLocalDir);
            }
        }

        private void RecoverPendingDeviceCapture()
        {
            if (this == null || string.IsNullOrEmpty(_deviceCaptureSession) || _state == State.Recording)
            {
                return;
            }

            string error;
            bool recovered;
            try
            {
                EditorUtility.DisplayProgressBar("AI Profiler", "正在恢复设备分段会话…", 0.5f);
                recovered = StopDeviceFrameCaptureAndPull(out error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (recovered)
            {
                _hasCapture = true;
                _state = State.Stopped;
                _statusLine = string.Format("已自动恢复设备帧段 {0} 个，可 ExportForAI。", _deviceSegFiles.Count);
            }
            else
            {
                _statusLine = "设备分段自动恢复失败：" + error + "。保持应用前台与 USB 连接后重新打开面板即可重试。";
            }
            Repaint();
        }

        private static void ClearDeviceRecoveryState()
        {
            SessionState.EraseString(kDeviceRecoverySession);
            SessionState.EraseString(kDeviceRecoverySerial);
            SessionState.EraseString(kDeviceRecoveryPackage);
            SessionState.EraseString(kDeviceRecoveryLocalDir);
        }

        private static int CompareDeviceSegmentFiles(string a, string b)
        {
            int ai = ParseDeviceSegmentIndex(a);
            int bi = ParseDeviceSegmentIndex(b);
            int byIndex = ai.CompareTo(bi);
            return byIndex != 0 ? byIndex : System.StringComparer.Ordinal.Compare(a, b);
        }

        private static int ParseDeviceSegmentIndex(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            int underscore = name.LastIndexOf('_');
            int index;
            return underscore >= 0 && int.TryParse(name.Substring(underscore + 1), out index)
                ? index
                : int.MaxValue;
        }

        private void ClearDeviceSegments()
        {
            EndDeviceSegmentPullPolling();
            if (_deviceSegmentPullTask != null)
            {
                string transferError;
                if (!FinishActiveDeviceSegmentPull(out transferError))
                {
                    Debug.LogWarning("[AIProfiler] 清理前等待设备小段传输失败: " + transferError);
                    return;
                }
            }
            _deviceSegFiles.Clear();
            try
            {
                // 仅删自动 adb pull 落地的本次 session 临时目录。
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai_profiler_device_frames");
                if (!string.IsNullOrEmpty(_deviceSegLocalDir) &&
                    _deviceSegLocalDir.StartsWith(tmp, System.StringComparison.OrdinalIgnoreCase) &&
                    System.IO.Directory.Exists(_deviceSegLocalDir))
                {
                    System.IO.Directory.Delete(_deviceSegLocalDir, true);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 清理设备段临时目录失败: " + e.Message);
            }
            _deviceSegLocalDir = null;
            ClearDeviceRecoveryState();
        }

        /// <summary>定位 adb：优先 Editor 配置的 Android SDK，其次环境变量，最后 PATH。</summary>
        private static string FindAdbPath()
        {
            string exe = Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb";
            // 1) Unity Editor 配置的 Android SDK
            string sdk = EditorPrefs.GetString("AndroidSdkRoot", "");
            if (!string.IsNullOrEmpty(sdk))
            {
                string p = System.IO.Path.Combine(sdk, "platform-tools", exe);
                if (System.IO.File.Exists(p)) return p;
            }
            // 2) 环境变量
            foreach (var ev in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
            {
                string root = System.Environment.GetEnvironmentVariable(ev);
                if (!string.IsNullOrEmpty(root))
                {
                    string p = System.IO.Path.Combine(root, "platform-tools", exe);
                    if (System.IO.File.Exists(p)) return p;
                }
            }
            // 3) PATH 上直接有 adb
            return exe;
        }

        /// <summary>同步跑一个进程，捕获 stdout/stderr/exit。失败（找不到 exe 等）返回 false。</summary>
        private static bool RunProcess(string fileName, string args, out string stdout, out string stderr,
            out int exitCode, int timeoutMs = 60000)
        {
            stdout = ""; stderr = ""; exitCode = -1;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(fileName, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return false;
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        stderr = "进程执行超时（" + timeoutMs + "ms）";
                        return false;
                    }
                    stdout = stdoutTask.GetAwaiter().GetResult();
                    stderr = stderrTask.GetAwaiter().GetResult();
                    exitCode = p.ExitCode;
                }
                return true;
            }
            catch (System.Exception e)
            {
                stderr = e.Message;
                return false;
            }
        }

        private static void DrawSwitch(string label, bool on)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var c = GUI.color;
                GUI.color = on ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.6f, 0.4f);
                EditorGUILayout.LabelField(on ? "● ON " : "○ OFF", GUILayout.Width(48));
                GUI.color = c;
                EditorGUILayout.LabelField(label);
            }
        }

        /// <summary>可点击版开关：点 ON/OFF 按钮返回 true（表示用户请求切换）。</summary>
        private static bool DrawSwitchButton(string label, bool on)
        {
            bool clicked;
            using (new EditorGUILayout.HorizontalScope())
            {
                var c = GUI.color;
                GUI.color = on ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.6f, 0.4f);
                clicked = GUILayout.Button(on ? "● ON " : "○ OFF", EditorStyles.miniButton, GUILayout.Width(48));
                GUI.color = c;
                EditorGUILayout.LabelField(label);
            }
            return clicked;
        }

        /// <summary>切换 Unity 原生 Deep Profile。改它会触发脚本重编译（域重载），只能非 Play 时切。</summary>
        private static void ToggleUnityDeepProfile()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("AI Profiler",
                    "Unity Deep Profile 只能在退出 Play 后切换（切换会触发脚本重编译）。", "OK");
                return;
            }
            // 翻转。关掉后会重编译→域重载→OnEnable 再跑 AutoEnableDeepSwitches，
            // 但本会话已配置过（kDeepAutoConfiguredKey=true），不会被强制改回，用户的 OFF 得以保留。
            ProfilerDriver.deepProfiling = !ProfilerDriver.deepProfiling;
        }
        #endregion
    }
}
#endif
