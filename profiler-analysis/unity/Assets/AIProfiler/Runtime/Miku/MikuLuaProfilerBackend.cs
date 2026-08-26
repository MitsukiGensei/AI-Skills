// AI Profiler — MikuLuaProfiler 后端适配（可选）。
//
// 上游：https://github.com/leinlin/Miku-LuaProfiler （第三方，MIT；本仓库不附带其源码）。
// 启用：工程装好上游 Miku 后，在 Player Settings → Scripting Define Symbols 加 `AI_PROFILER_MIKU`。
//
// 只编译期依赖上游公开 API（LuaDeepProfilerSetting / LuaProfiler.RegisterOnReceiveSample / Sample.RegAction /
// NetWorkMgrClient）。真机无上限采样需要的几处扩展点（Hook 就绪查询、状态心跳、远程采样开关、
// 一次性启动标记）上游没有，这里用反射探测：打了 ../../../Miku-LuaProfiler/patches 里的补丁就生效，
// 没打则各自优雅降级（见每个成员的注释）。
#if AI_PROFILER_MIKU
using System;
using System.IO;
using System.Reflection;
using MikuLuaProfiler;
using UnityEngine;

namespace AIProfiler
{
    public sealed class MikuLuaProfilerBackend : ILuaProfilerBackend
    {
        private const float TICKS_PER_MS = 10000f; // Miku 计时单位 100ns ticks → ms
        private const string SERVER_FLAG_FILE = "/LUAPROFILER_SERVER"; // 与上游 LuaProfiler.SERVER_CONFIG_NAME 一致

        private const BindingFlags kStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags kInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type kSettingType = typeof(LuaDeepProfilerSetting);
        private static readonly Type kHookType = typeof(HookLuaSetup);
        private static readonly Type kProfilerType = typeof(MikuLuaProfiler.LuaProfiler);
        private static readonly Type kHeartBeatType = typeof(HeartBeatMsg);

        private Action<LuaSampleNode> _onNode;
        private Action<bool, bool> _onStatus;
        private Delegate _heartBeatHandler;

        private static LuaDeepProfilerSetting S { get { return LuaDeepProfilerSetting.Instance; } }

        public string Name { get { return "MikuLuaProfiler"; } }
        public bool IsAvailable { get { return true; } }

        // ---- 设置 ----

        public bool DeepLuaEnabled
        {
            get { return S.m_isDeepLuaProfiler; }
            set
            {
                // 补丁版有带 Save() 的属性 isDeepLuaProfiler；上游只有公开字段。
                if (!TrySetInstance(S, "isDeepLuaProfiler", value))
                {
                    S.m_isDeepLuaProfiler = value;
                    TryInvokeInstance(S, "Save");
                }
            }
        }

        public bool IsLocal
        {
            get { return S.isLocal; }
            set { S.isLocal = value; }
        }

        public bool RecordEnabled
        {
            set { S.isRecord = value; }
        }

        public bool IsSampling
        {
            get { return S.isStartRecord; }
            set { S.isStartRecord = value; }
        }

        public bool WindowOpen
        {
            set
            {
                // 上游：static bool ProfilerWinOpen；补丁版：实例属性（持久化）。两种都试。
                if (TrySetStatic(kSettingType, "ProfilerWinOpen", value))
                {
                    return;
                }
                TrySetInstance(S, "ProfilerWinOpen", value);
            }
        }

        public void SetRemoteEndpoint(string ip, int port)
        {
            S.ip = ip;
            S.port = port;
        }

        // ---- Hook 状态 ----

        public bool IsHookInitialized
        {
            get
            {
                object v = TryGetStatic(kHookType, "IsInitialized");
                if (v is bool)
                {
                    return (bool)v;
                }
                return MikuLuaProfiler.LuaProfiler.mainL != IntPtr.Zero; // 上游无该属性：以 Lua VM 指针就绪代替
            }
        }

        public bool IsHookReady
        {
            get
            {
                object v = TryGetStatic(kHookType, "IsDeepProfilerReady");
                if (v is bool)
                {
                    return (bool)v;
                }
                return MikuLuaProfiler.LuaProfiler.mainL != IntPtr.Zero;
            }
        }

        // ---- 接收 ----

        public void RegisterLocalReceiver(Action<LuaSampleNode> onNode)
        {
            UnregisterReceivers();
            _onNode = onNode;
            MikuLuaProfiler.LuaProfiler.RegisterOnReceiveSample(OnSample);
        }

        public void RegisterRemoteReceiver(Action<LuaSampleNode> onNode, Action<bool, bool> onStatus)
        {
            UnregisterReceivers();
            _onNode = onNode;
            _onStatus = onStatus;
            Sample.RegAction(OnSample);
            RegisterHeartBeat();
        }

        public void UnregisterReceivers()
        {
            MikuLuaProfiler.LuaProfiler.UnRegistReceive();
            Sample.UnRegAction();
            UnregisterHeartBeat();
            _onNode = null;
            _onStatus = null;
        }

        public bool RemoteStatusSupported
        {
            get { return kHeartBeatType.GetField("hookReady", kInstance) != null; }
        }

        // 注意：远程模式下本回调在 NetWorkMgrClient 接收线程触发（非主线程），调用方须自行加锁。
        private void OnSample(Sample sample)
        {
            var cb = _onNode;
            if (cb == null || sample == null)
            {
                return;
            }
            Walk(sample, cb);
        }

        private static void Walk(Sample s, Action<LuaSampleNode> cb)
        {
            if (!string.IsNullOrEmpty(s.name))
            {
                cb(new LuaSampleNode
                {
                    name = s.name,
                    selfMs = s.selfCostTime / TICKS_PER_MS,
                    totalMs = s.costTime / TICKS_PER_MS,
                    calls = s.calls,
                    luaGcBytes = s.selfLuaGC,
                    monoGcBytes = s.selfMonoGC,
                });
            }
            var childs = s.childs;
            if (childs != null)
            {
                for (int i = 0, imax = childs.Count; i < imax; i++)
                {
                    Walk(childs[i], cb);
                }
            }
        }

        // 状态心跳（补丁扩展点：HeartBeatMsg.RegAction(Action<HeartBeatMsg>) + hookReady/captureActive 字段）
        private void RegisterHeartBeat()
        {
            var reg = kHeartBeatType.GetMethod("RegAction", kStatic);
            var fReady = kHeartBeatType.GetField("hookReady", kInstance);
            var fActive = kHeartBeatType.GetField("captureActive", kInstance);
            if (reg == null || fReady == null || fActive == null)
            {
                return; // 上游无状态心跳：RemoteStatusSupported=false，由调用方按 TCP 连接状态兜底
            }
            Action<HeartBeatMsg> handler = msg =>
            {
                var cb = _onStatus;
                if (cb == null || msg == null)
                {
                    return;
                }
                cb((bool)fReady.GetValue(msg), (bool)fActive.GetValue(msg));
            };
            _heartBeatHandler = handler;
            reg.Invoke(null, new object[] { handler });
        }

        private void UnregisterHeartBeat()
        {
            if (_heartBeatHandler == null)
            {
                return;
            }
            _heartBeatHandler = null;
            var unreg = kHeartBeatType.GetMethod("UnRegAction", kStatic);
            if (unreg != null)
            {
                unreg.Invoke(null, null);
            }
        }

        // ---- 远程连接（NetWorkMgrClient 在 MikuLuaProfiler.Editor 程序集，运行时程序集不能直接引用 → 反射）----

        public bool RemoteConnect(string ip, int port)
        {
            return TryInvokeNetWorkClient("Connect", ip, port) != null || IsRemoteConnected;
        }

        public void RemoteDisconnect()
        {
            TryInvokeNetWorkClient("Disconnect");
        }

        public bool IsRemoteConnected
        {
            get
            {
                var r = TryInvokeNetWorkClient("GetIsConnect");
                return r is bool && (bool)r;
            }
        }

        private static Type _netWorkClientType;

        private static object TryInvokeNetWorkClient(string method, params object[] args)
        {
#if UNITY_EDITOR
            try
            {
                if (_netWorkClientType == null)
                {
                    _netWorkClientType = FindType("MikuLuaProfiler.NetWorkMgrClient");
                }
                if (_netWorkClientType == null)
                {
                    Debug.LogWarning("[AIProfiler] 未找到 MikuLuaProfiler.NetWorkMgrClient（Miku Editor 程序集未加载？）");
                    return null;
                }
                var mi = _netWorkClientType.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (mi == null)
                {
                    Debug.LogWarning("[AIProfiler] NetWorkMgrClient 无静态方法 " + method);
                    return null;
                }
                object r = mi.Invoke(null, (args == null || args.Length == 0) ? null : args);
                return r ?? (object)true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AIProfiler] NetWorkMgrClient." + method + " 调用失败: " + e.Message);
                return null;
            }
#else
            return null;
#endif
        }

        // ---- 设备侧 ----

        public void SetRemoteCaptureActive(bool active)
        {
            // 补丁扩展点：LuaProfiler.SetRemoteCaptureActive(bool)。上游 Hook 一装上就持续采样，无休眠开关，故 no-op。
            var mi = kProfilerType.GetMethod("SetRemoteCaptureActive", kStatic);
            if (mi != null)
            {
                mi.Invoke(null, new object[] { active });
            }
        }

        public bool RequestRemoteHookOnNextLaunch()
        {
            // 补丁版：HookLuaSetup.OpenRemoteProfiler()（含已初始化时的即时切换）；否则直接写上游约定的启动标记文件。
            var mi = kHookType.GetMethod("OpenRemoteProfiler", kStatic);
            if (mi != null)
            {
                object r = mi.Invoke(null, null);
                return !(r is bool) || (bool)r;
            }
            try
            {
                string path = Application.persistentDataPath + SERVER_FLAG_FILE;
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, "1");
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AIProfiler] 写入 Lua Profiler 启动标记失败: " + e.Message);
                return false;
            }
        }

        // ---- 反射小工具 ----

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null)
                {
                    return t;
                }
            }
            return null;
        }

        private static object TryGetStatic(Type t, string member)
        {
            var p = t.GetProperty(member, kStatic);
            if (p != null)
            {
                return p.GetValue(null, null);
            }
            var f = t.GetField(member, kStatic);
            return f != null ? f.GetValue(null) : null;
        }

        private static bool TrySetStatic(Type t, string member, object value)
        {
            var p = t.GetProperty(member, kStatic);
            if (p != null && p.CanWrite)
            {
                p.SetValue(null, value, null);
                return true;
            }
            var f = t.GetField(member, kStatic);
            if (f != null)
            {
                f.SetValue(null, value);
                return true;
            }
            return false;
        }

        private static bool TrySetInstance(object target, string member, object value)
        {
            if (target == null)
            {
                return false;
            }
            var p = target.GetType().GetProperty(member, kInstance);
            if (p != null && p.CanWrite)
            {
                p.SetValue(target, value, null);
                return true;
            }
            return false;
        }

        private static void TryInvokeInstance(object target, string method)
        {
            if (target == null)
            {
                return;
            }
            var mi = target.GetType().GetMethod(method, kInstance, null, Type.EmptyTypes, null);
            if (mi != null)
            {
                mi.Invoke(target, null);
            }
        }
    }
}
#endif
