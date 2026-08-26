// AI Profiler 面板的无人值守驱动入口：把面板上 StartRecord / StopRecord / ExportForAI / CleanRecord
// 四个 GUI 按钮暴露成参数为空的 MenuItem，供远程菜单执行类工具（如 unity-skills 的 `editor_execute_menu`）调用，
// 用于「跑一整套压测矩阵、每档各导一份采样」这类需要脚本编排的批量采集。
//
// 为什么用反射而不是改 AIProfilerWindow 开公有 API：
//   面板本身是人工操作的工具，四个方法的私有性是它的设计意图（含前置校验与 Dialog 提示）。
//   本文件只是**外部驱动器**，不改被驱动方的契约；面板改签名时这里 MethodInfo 取不到 → 显式报错，
//   不会静默走错分支。
//
// 无 try/catch（项目 C# 纪律）：反射查找失败一律走 null 守卫显式 LogError 返回，
// 不用异常捕获做流程控制。

using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace AIProfiler.Editor
{
    public static class AIProfilerAutomation
    {
        private const string kMenuRoot = "Tools/AI Profiler Auto/";

        private const BindingFlags kInstanceAny =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>取已打开的面板实例；未打开时不隐式创建（打开会触发 AutoEnableDeepSwitches 改深度开关）。</summary>
        private static EditorWindow FindPanel(bool openIfMissing)
        {
            System.Type t = typeof(AIProfilerWindow);

            Object[] found = Resources.FindObjectsOfTypeAll(t);
            if (found != null && found.Length > 0)
            {
                return found[0] as EditorWindow;
            }

            if (!openIfMissing)
            {
                Debug.LogError("[AIProfilerAuto] AI Profiler 面板未打开。先执行菜单 " + kMenuRoot + "Open Panel");
                return null;
            }
            return EditorWindow.GetWindow(t, false, "AI Profiler", true);
        }

        /// <summary>反射调面板上的私有无参方法；找不到即报错返回 false，不吞。</summary>
        private static bool InvokePanel(string methodName, bool openIfMissing)
        {
            EditorWindow win = FindPanel(openIfMissing);
            if (win == null)
            {
                return false;
            }
            MethodInfo mi = win.GetType().GetMethod(methodName, kInstanceAny, null, System.Type.EmptyTypes, null);
            if (mi == null)
            {
                Debug.LogError("[AIProfilerAuto] 面板上找不到无参方法 " + methodName + "（面板签名已变）");
                return false;
            }
            mi.Invoke(win, null);
            Debug.Log("[AIProfilerAuto] " + methodName + " 已执行｜" + ReadStatus(win));
            return true;
        }

        private static string ReadField(EditorWindow win, string field)
        {
            FieldInfo fi = win.GetType().GetField(field, kInstanceAny);
            if (fi == null)
            {
                return field + "=<缺失>";
            }
            object v = fi.GetValue(win);
            return field + "=" + (v == null ? "null" : v.ToString());
        }

        private static string ReadStatus(EditorWindow win)
        {
            return string.Join(" ", new string[]
            {
                ReadField(win, "_state"),
                ReadField(win, "_hasCapture"),
                ReadField(win, "_mode"),
                "deep=" + ProfilerDriver.deepProfiling,
                ReadField(win, "_statusLine"),
            });
        }

        [MenuItem(kMenuRoot + "Open Panel", priority = 1)]
        public static void OpenPanel()
        {
            EditorWindow win = FindPanel(true);
            if (win == null)
            {
                return;
            }
            Debug.Log("[AIProfilerAuto] 面板就位｜" + ReadStatus(win));
        }

        [MenuItem(kMenuRoot + "Status", priority = 2)]
        public static void Status()
        {
            EditorWindow win = FindPanel(false);
            if (win == null)
            {
                return;
            }
            Debug.Log("[AIProfilerAuto] STATUS｜" + ReadStatus(win) + "｜isPlaying=" + EditorApplication.isPlaying);
        }

        // Deep Profile 开关必须在**非 Play**时设（改它会触发脚本重编译）。
        // 压测矩阵默认关 Deep：Deep 下单帧 ~35MB 段落盘、导出逐段 LoadProfile 数分钟，
        // 且把帧耗时放大到失真——容量曲线要的是可比负载，不是被插桩淹没的绝对值。
        [MenuItem(kMenuRoot + "Deep Profile ON", priority = 20)]
        public static void DeepOn()
        {
            SetDeep(true);
        }

        [MenuItem(kMenuRoot + "Deep Profile OFF", priority = 21)]
        public static void DeepOff()
        {
            SetDeep(false);
        }

        private static void SetDeep(bool on)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[AIProfilerAuto] Play 中不可改 Deep Profile（会触发重编译）。先退 Play。");
                return;
            }
            ProfilerDriver.deepProfiling = on;
            Debug.Log("[AIProfilerAuto] deepProfiling = " + ProfilerDriver.deepProfiling);
        }

        // Lua hook 的安装通常发生在进 Play 时，**改这个开关必须在非 Play 期**，下次 Play 才生效。
        // 关掉它 = skill 文档里的「原生安全模式」：C# 榜不再被 Lua 后端自身运行时占据，
        // Lua section 空是预期而非数据损坏。无 Lua 后端时报错返回。
        [MenuItem(kMenuRoot + "Lua Deep ON", priority = 30)]
        public static void LuaDeepOn()
        {
            SetLuaDeep(true);
        }

        [MenuItem(kMenuRoot + "Lua Deep OFF", priority = 31)]
        public static void LuaDeepOff()
        {
            SetLuaDeep(false);
        }

        private static void SetLuaDeep(bool on)
        {
            var lua = LuaProfilerBackend.Current;
            if (!lua.IsAvailable)
            {
                Debug.LogError("[AIProfilerAuto] 未接入 Lua 后端（Miku 工程需加 AI_PROFILER_MIKU 宏）");
                return;
            }
            lua.DeepLuaEnabled = on;
            Debug.Log("[AIProfilerAuto] " + lua.Name + " DeepLuaEnabled = " + lua.DeepLuaEnabled
                + "（下次进 Play 生效；Play 中改无效）");
        }

        [MenuItem(kMenuRoot + "Start Record", priority = 40)]
        public static void StartRecord()
        {
            InvokePanel("StartRecord", false);
        }

        [MenuItem(kMenuRoot + "Stop Record", priority = 41)]
        public static void StopRecord()
        {
            InvokePanel("StopRecord", false);
        }

        [MenuItem(kMenuRoot + "Export For AI", priority = 42)]
        public static void ExportForAI()
        {
            InvokePanel("ExportForAI", false);
        }

        [MenuItem(kMenuRoot + "Clean Record", priority = 43)]
        public static void CleanRecord()
        {
            InvokePanel("CleanRecord", false);
        }
    }
}
