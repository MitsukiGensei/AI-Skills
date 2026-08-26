// AI Profiler — Lua 采样后端抽象。
//
// AI Profiler 面板/导出器本身不依赖任何具体的 Lua profiler。Lua 维度（Lua CPU / Lua VM GC）通过本接口接入：
//   · 没有 Lua 的工程：什么都不用做，LuaProfilerBackend.Current 是 Null 实现，导出的 LUA_HOTSPOTS 为 NO DATA。
//   · 用 MikuLuaProfiler 的工程：在 Player Settings 加 Scripting Define `AI_PROFILER_MIKU`，
//     Miku/MikuLuaProfilerBackend.cs 会自动成为 Current（基于上游 Miku 公开 API，扩展点用反射探测，缺失时优雅降级）。
//   · 其他 Lua profiler：实现 ILuaProfilerBackend，并在启动时 LuaProfilerBackend.Register(实例)。
using System;

namespace AIProfiler
{
    /// <summary>Lua 采样节点（后端无关的扁平 DTO）。后端把自己的采样树逐节点回调出来，导出器按 name 聚合。</summary>
    public struct LuaSampleNode
    {
        public string name;       // 后端的函数标识；Miku 格式为 "[lua]: <file>&<func>:<line>"（导出器据此解析 location）
        public double selfMs;     // 自身耗时（不含子级）
        public double totalMs;    // 含子级耗时
        public long calls;
        public long luaGcBytes;   // Lua VM 分配（自身）
        public long monoGcBytes;  // Mono 分配（自身）
    }

    public interface ILuaProfilerBackend
    {
        string Name { get; }
        bool IsAvailable { get; }

        // ---- 采样开关 / 路由（Editor 面板按模式设置）----
        bool DeepLuaEnabled { get; set; }   // 是否对 Lua 函数做深度插桩
        bool IsLocal { get; set; }          // true=Editor 进程内回调；false=远程 TCP 回传
        bool RecordEnabled { set; }         // 允许录制
        bool IsSampling { get; set; }       // 采样中（StartRecord/StopRecord 切换）
        bool WindowOpen { set; }            // AI Profiler 面板是否打开（部分后端以此决定进 Play 时是否装 Hook）
        void SetRemoteEndpoint(string ip, int port);

        // ---- Hook 状态（Editor 本地模式 StartRecord 前置校验）----
        bool IsHookInitialized { get; }
        bool IsHookReady { get; }

        // ---- 采样接收 ----
        void RegisterLocalReceiver(Action<LuaSampleNode> onNode);
        /// <param name="onStatus">(hookReady, captureActive) 设备状态心跳；后端不支持状态心跳时不会回调，见 RemoteStatusSupported。</param>
        void RegisterRemoteReceiver(Action<LuaSampleNode> onNode, Action<bool, bool> onStatus);
        void UnregisterReceivers();
        bool RemoteStatusSupported { get; }

        // ---- 远程连接（Editor 侧）----
        bool RemoteConnect(string ip, int port);
        void RemoteDisconnect();
        bool IsRemoteConnected { get; }

        // ---- 设备侧 ----
        /// <summary>真机：由 DeviceFrameRecorder 随 StartRecord/StopRecord 打开/关闭 Lua 采样（Hook 平时休眠）。</summary>
        void SetRemoteCaptureActive(bool active);
        /// <summary>真机：写入"下次完整启动时安装 Lua Hook 并开 TCP server"的一次性标记（接到 GM 菜单）。</summary>
        bool RequestRemoteHookOnNextLaunch();
    }

    /// <summary>无 Lua 后端：所有操作为 no-op，Lua 维度 NO DATA。</summary>
    public sealed class NullLuaProfilerBackend : ILuaProfilerBackend
    {
        public string Name { get { return "None"; } }
        public bool IsAvailable { get { return false; } }
        public bool DeepLuaEnabled { get { return false; } set { } }
        public bool IsLocal { get { return true; } set { } }
        public bool RecordEnabled { set { } }
        public bool IsSampling { get { return false; } set { } }
        public bool WindowOpen { set { } }
        public void SetRemoteEndpoint(string ip, int port) { }
        public bool IsHookInitialized { get { return false; } }
        public bool IsHookReady { get { return false; } }
        public void RegisterLocalReceiver(Action<LuaSampleNode> onNode) { }
        public void RegisterRemoteReceiver(Action<LuaSampleNode> onNode, Action<bool, bool> onStatus) { }
        public void UnregisterReceivers() { }
        public bool RemoteStatusSupported { get { return false; } }
        public bool RemoteConnect(string ip, int port) { return false; }
        public void RemoteDisconnect() { }
        public bool IsRemoteConnected { get { return false; } }
        public void SetRemoteCaptureActive(bool active) { }
        public bool RequestRemoteHookOnNextLaunch() { return false; }
    }

    public static class LuaProfilerBackend
    {
        private static ILuaProfilerBackend _current;

        public static ILuaProfilerBackend Current
        {
            get
            {
                if (_current == null)
                {
#if AI_PROFILER_MIKU
                    _current = new MikuLuaProfilerBackend();
#else
                    _current = new NullLuaProfilerBackend();
#endif
                }
                return _current;
            }
        }

        /// <summary>注册自定义后端（在任何面板/录制逻辑跑起来之前调用，例如 [InitializeOnLoadMethod]）。传 null 恢复默认。</summary>
        public static void Register(ILuaProfilerBackend backend)
        {
            _current = backend;
        }
    }

    /// <summary>
    /// 设备侧控制门面：真机 GM 菜单 / 调试面板调用。绑定到脚本层（如 Lua）时只绑这个纯静态类即可。
    /// </summary>
    public static class AIProfilerDeviceControl
    {
        /// <summary>请求下次完整启动时安装 Lua Hook（真机 Lua 采样前置步骤）。返回 false = 当前包无可用 Lua 后端。</summary>
        public static bool OpenLuaProfiler()
        {
            return LuaProfilerBackend.Current.RequestRemoteHookOnNextLaunch();
        }

        public static string LuaBackendName
        {
            get { return LuaProfilerBackend.Current.Name; }
        }
    }
}
