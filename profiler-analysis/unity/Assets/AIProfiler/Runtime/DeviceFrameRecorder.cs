#if UNITY_EDITOR || AI_PROFILER_DEVICE
using System.IO;
using UnityEngine;
using UnityProfiler = UnityEngine.Profiling.Profiler;

namespace AIProfiler
{
    /// <summary>
    /// 真机无限帧采集器：设备本地分段写 Profiler binary log，Editor 通过 ADB 命令文件自动启停并 pull。
    /// 仅含 AI_PROFILER_DEVICE 宏的包会在真机启动轮询；正式包不包含本类型。Lua 采样开关经 LuaProfilerBackend 委托给具体 Lua profiler（可无）。
    /// </summary>
    public class DeviceFrameRecorder : MonoBehaviour
    {
        public const string FRAME_DIR = "/ai_profiler_frames";
        public const string CONTROL_DIR = "/ai_profiler_control";
        public const string COMMAND_FILE = "command.txt";
        public const string STATE_FILE = "state.txt";
        public const string STATE_RECORDING = "recording";
        public const string STATE_STOPPED = "stopped";
        public const string READY_EXTENSION = ".ready";

        // 单段必须显著低于 Editor LoadProfile 的约 2000 帧上限；Deep 场景再由 32MB 体积闸提前滚段。
        // 小段关闭后立即发布 .ready，Editor 可在下一段录制期间后台 pull，避免设备长期堆积大文件。
        private const int SEG_FRAMES = 600;
        private const long SEG_MAX_BYTES = 32L * 1024 * 1024;
        private const int PROFILER_MAX_USED_MEMORY = 128 * 1024 * 1024;
        private const float COMMAND_POLL_INTERVAL = 0.2f;
        private const int SIZE_CHECK_INTERVAL_FRAMES = 1;

        private static DeviceFrameRecorder _inst;
        private bool _recording;
        private string _dir;
        private string _controlDir;
        private string _commandPath;
        private string _statePath;
        private string _currentRawPath;
        private string _sessionId = "";
        private string _lastCommand = "";
        private int _segIndex;
        private int _currentSegIndex = -1;
        private int _segStartFrame;
        private int _nextSizeCheckFrame;
        private float _nextCommandPoll;

        private bool _prevProfilerEnabled;
        private bool _prevBinaryLog;
        private bool _prevAllocCallstacks;
        private int _prevMaxUsedMemory;
        private string _prevLogFile;
        private bool _profilerStateCaptured;

        public static bool IsRecording { get { return _inst != null && _inst._recording; } }
        public static int SegCount { get; private set; }
        public static string DeviceDir { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!Application.isEditor)
            {
                EnsureInstance();
            }
        }

        private static DeviceFrameRecorder EnsureInstance()
        {
            if (_inst != null)
            {
                return _inst;
            }

            var go = new GameObject("MikuDeviceFrameRecorder");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<DeviceFrameRecorder>();
            return _inst;
        }

        public static void StartRecord()
        {
            string ignored;
            EnsureInstance().BeginInternal("manual", true, out ignored);
        }

        public static void StopRecord()
        {
            if (_inst != null)
            {
                _inst.EndInternal(true);
            }
        }

        private void Awake()
        {
            if (_inst != null && _inst != this)
            {
                Destroy(gameObject);
                return;
            }
            _inst = this;
            _dir = Application.persistentDataPath + FRAME_DIR;
            _controlDir = Application.persistentDataPath + CONTROL_DIR;
            _commandPath = Path.Combine(_controlDir, COMMAND_FILE);
            _statePath = Path.Combine(_controlDir, STATE_FILE);
            _nextCommandPoll = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= _nextCommandPoll)
            {
                _nextCommandPoll = now + COMMAND_POLL_INTERVAL;
                PollAdbCommand();
            }

            if (!_recording)
            {
                return;
            }

            bool byFrames = Time.frameCount - _segStartFrame >= SEG_FRAMES;
            bool bySize = false;
            if (Time.frameCount >= _nextSizeCheckFrame)
            {
                _nextSizeCheckFrame = Time.frameCount + SIZE_CHECK_INTERVAL_FRAMES;
                try
                {
                    var fi = new FileInfo(_currentRawPath);
                    bySize = fi.Exists && fi.Length >= SEG_MAX_BYTES;
                }
                catch { }
            }

            if (byFrames || bySize)
            {
                _segIndex++;
                OpenSegment();
                WriteState(STATE_RECORDING);
            }
        }

        private void PollAdbCommand()
        {
            if (string.IsNullOrEmpty(_commandPath) || !File.Exists(_commandPath))
            {
                return;
            }

            string command;
            try
            {
                command = File.ReadAllText(_commandPath).Trim();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 读取 ADB 帧采集命令失败: " + e.Message);
                return;
            }
            try { File.Delete(_commandPath); } catch { }
            if (string.IsNullOrEmpty(command) || command == _lastCommand)
            {
                return;
            }
            _lastCommand = command;

            string[] commandParts = command.Split(':');
            string action = commandParts.Length > 0 ? commandParts[0] : command;
            string session = commandParts.Length > 1 ? commandParts[1] : "manual";
            bool captureLua = commandParts.Length <= 2 || commandParts[2] != "lua=0";
            if (action == "start")
            {
                string error;
                if (!BeginInternal(session, captureLua, out error))
                {
                    WriteRawState("error:" + session + ":" + SanitizeStateText(error));
                }
            }
            else if (action == "stop")
            {
                if (!_recording)
                {
                    // App 崩溃/重启后也确认 stopped，让 Editor 可以回收已 flush 的旧段。
                    _sessionId = session;
                    WriteState(STATE_STOPPED);
                }
                else if (session == "manual" || session == _sessionId)
                {
                    EndInternal(true);
                }
            }
        }

        private bool BeginInternal(string session, bool captureLua, out string error)
        {
            error = null;
            if (_recording)
            {
                if (session == _sessionId)
                {
                    WriteState(STATE_RECORDING);
                    return true;
                }
                EndInternal(false);
            }

            _sessionId = string.IsNullOrEmpty(session) ? "manual" : session;
            try
            {
                _prevProfilerEnabled = UnityProfiler.enabled;
                _prevBinaryLog = UnityProfiler.enableBinaryLog;
                _prevAllocCallstacks = UnityProfiler.enableAllocationCallstacks;
                _prevMaxUsedMemory = UnityProfiler.maxUsedMemory;
                _prevLogFile = UnityProfiler.logFile;
                _profilerStateCaptured = true;

                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
                Directory.CreateDirectory(_dir);
                Directory.CreateDirectory(_controlDir);

                UnityProfiler.maxUsedMemory = PROFILER_MAX_USED_MEMORY;
                UnityProfiler.enableAllocationCallstacks = false;
                _segIndex = 0;
                _currentSegIndex = -1;
                SegCount = 0;
                DeviceDir = _dir;
                _recording = true;
                OpenSegment();
                LuaProfilerBackend.Current.SetRemoteCaptureActive(captureLua);
                WriteState(STATE_RECORDING);
                Debug.Log("<color=#00ff00>[AIProfiler] 设备帧自动采集开始</color> session=" + _sessionId + " " + _dir);
                return true;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                _recording = false;
                LuaProfilerBackend.Current.SetRemoteCaptureActive(false);
                RestoreProfilerState();
                Debug.LogWarning("[AIProfiler] 启动设备帧采集失败: " + e.Message);
                return false;
            }
        }

        private void OpenSegment()
        {
            if (_currentSegIndex >= 0)
            {
                CloseActiveSegment(true);
            }

            string baseName = Path.Combine(_dir, "seg_" + _segIndex.ToString("D4"));
            _currentRawPath = baseName + ".raw";
            _currentSegIndex = _segIndex;
            UnityProfiler.logFile = baseName;
            UnityProfiler.enableBinaryLog = true;
            UnityProfiler.enabled = true;
            _segStartFrame = Time.frameCount;
            _nextSizeCheckFrame = Time.frameCount + SIZE_CHECK_INTERVAL_FRAMES;
            SegCount = _segIndex + 1;
        }

        private void CloseActiveSegment(bool publishReady)
        {
            UnityProfiler.enabled = false;
            UnityProfiler.enableBinaryLog = false;
            UnityProfiler.logFile = "";
            if (publishReady)
            {
                PublishReadySegment();
            }
            _currentRawPath = null;
            _currentSegIndex = -1;
        }

        private void PublishReadySegment()
        {
            if (string.IsNullOrEmpty(_currentRawPath) || _currentSegIndex < 0 || !File.Exists(_currentRawPath))
            {
                return;
            }

            long length = new FileInfo(_currentRawPath).Length;
            if (length <= 0)
            {
                try { File.Delete(_currentRawPath); } catch { }
                return;
            }

            string readyPath = Path.ChangeExtension(_currentRawPath, READY_EXTENSION);
            string tempPath = readyPath + ".tmp";
            File.WriteAllText(tempPath, _sessionId + ":" + _currentSegIndex + ":" + length);
            if (File.Exists(readyPath))
            {
                File.Delete(readyPath);
            }
            File.Move(tempPath, readyPath);
        }

        private void EndInternal(bool writeState)
        {
            LuaProfilerBackend.Current.SetRemoteCaptureActive(false);
            if (!_recording)
            {
                if (writeState)
                {
                    WriteState(STATE_STOPPED);
                }
                return;
            }

            try
            {
                CloseActiveSegment(true);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 停止设备帧采集异常: " + e.Message);
            }
            finally
            {
                _recording = false;
                RestoreProfilerState();
            }

            if (writeState)
            {
                WriteState(STATE_STOPPED);
            }
            Debug.Log("<color=#00ff00>[AIProfiler] 设备帧自动采集停止</color> session=" + _sessionId +
                      " 段数=" + SegCount + " 目录=" + _dir);
        }

        private void RestoreProfilerState()
        {
            if (!_profilerStateCaptured)
            {
                return;
            }
            try
            {
                UnityProfiler.maxUsedMemory = _prevMaxUsedMemory;
                UnityProfiler.enableAllocationCallstacks = _prevAllocCallstacks;
                UnityProfiler.logFile = _prevLogFile ?? "";
                UnityProfiler.enableBinaryLog = _prevBinaryLog;
                UnityProfiler.enabled = _prevProfilerEnabled;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 还原设备 Profiler 状态失败: " + e.Message);
            }
            finally
            {
                _profilerStateCaptured = false;
            }
        }

        private void WriteState(string state)
        {
            WriteRawState(state + ":" + _sessionId + ":" + SegCount);
        }

        private void WriteRawState(string state)
        {
            try
            {
                Directory.CreateDirectory(_controlDir);
                string tempPath = _statePath + ".tmp";
                File.WriteAllText(tempPath, state);
                if (File.Exists(_statePath))
                {
                    File.Delete(_statePath);
                }
                File.Move(tempPath, _statePath);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[AIProfiler] 写入设备帧采集状态失败: " + e.Message);
            }
        }

        private static string SanitizeStateText(string text)
        {
            return string.IsNullOrEmpty(text) ? "unknown" : text.Replace('\r', ' ').Replace('\n', ' ').Replace(':', '_');
        }

        private void OnApplicationQuit()
        {
            EndInternal(false);
        }

        private void OnDestroy()
        {
            if (_inst == this)
            {
                EndInternal(false);
                _inst = null;
            }
        }
    }
}
#endif
