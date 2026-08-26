using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnitySkills;

namespace AIProfiler.Editor.Integrations
{
    /// <summary>
    /// AI Profiler（Window/Analysis/AI Profiler）远程控制：ADB connect / start / stop / export。
    /// 依赖 unity-skills（MCP）框架的 [UnitySkill] 特性；不用 unity-skills 的工程不要合入本文件。
    /// 本文件可放在 unity-skills 的扩展程序集里，故对 AIProfilerWindow 用类型名查找而非直接引用。
    /// AIProfilerWindow 的 StartRecord / StopRecord / ExportForAI 均为私有实例方法（面板按钮回调），
    /// 此处经反射调用——面板升级若改名，返回 error 中会带出可用方法名便于修复。
    /// 用途：AI Agent 全自动跑性能 A/B 采样（此前 Start/Stop/Export 需人工点面板按钮）。
    /// </summary>
    public static class AIProfilerSkills
    {
        private const string ProfilerLogsDir = "Assets/ProfilerLogs";
        private const string WindowTypeName = "AIProfiler.AIProfilerWindow";

        private static Type FindWindowType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(WindowTypeName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static object Invoke(string methodName)
        {
            var winType = FindWindowType();
            if (winType == null)
                return new { error = "找不到 " + WindowTypeName + "（AI Profiler 面板脚本未合入或未编译）" };
            // 未打开则自动打开（首次打开会自动启用 Unity Deep Profile + Lua 深度采样，
            // 可能触发一次脚本重编译——调用方需在编译静默期使用）
            var win = EditorWindow.GetWindow(winType, false, "AI Profiler", false);
            if (win == null)
                return new { error = "AIProfilerWindow 打开失败" };

            var mi = winType.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi == null)
            {
                var candidates = winType
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m => m.GetParameters().Length == 0 &&
                                (m.Name.Contains("Record") || m.Name.Contains("Export")))
                    .Select(m => m.Name).ToArray();
                return new
                {
                    error = $"方法 {methodName} 不存在（面板可能已升级改名）",
                    candidates
                };
            }

            mi.Invoke(win, null);
            return null; // null = 无错误
        }

        [UnitySkill("aiprofiler_start",
            "Start AI Profiler recording (Window/Analysis/AI Profiler > StartRecord). " +
            "Opens the panel if not open (first open this session auto-enables Unity Deep Profile + " +
            "Lua deep sampling and may trigger a recompile). Use during compile-quiet period.")]
        public static object AIProfilerStart()
        {
            var err = Invoke("StartRecord");
            if (err != null) return err;
            return new { success = true, message = "AI Profiler recording started" };
        }

        [UnitySkill("aiprofiler_connect_adb",
            "Connect AI Profiler to one USB-attached Android Development Player through adb. " +
            "Auto-detects the running Unity package, forwards Unity tcp:34999 and the Lua profiler tcp:2333, then selects Unity's device:// ADB target.")]
        public static object AIProfilerConnectAdb()
        {
            var err = Invoke("ConnectAdb");
            if (err != null) return err;
            return new { success = true, message = "AI Profiler ADB connection requested" };
        }

        [UnitySkill("aiprofiler_status",
            "Get AI Profiler ADB device, Unity Profiler target, Lua profiler connection, and current panel status.")]
        public static object AIProfilerStatus()
        {
            var winType = FindWindowType();
            if (winType == null)
                return new { error = "找不到 " + WindowTypeName + "（AI Profiler 面板脚本未合入或未编译）" };
            var win = EditorWindow.GetWindow(winType, false, "AI Profiler", false);
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
            var connectionMethod = winType.GetMethod("GetCurrentConnectionName", staticFlags);
            var luaMethod = winType.GetMethod("IsLuaRemoteConnected", staticFlags);
            return new
            {
                success = true,
                adbSerial = winType.GetField("_adbSerial", instanceFlags)?.GetValue(win) as string,
                adbPackage = winType.GetField("_adbPackage", instanceFlags)?.GetValue(win) as string,
                unityTarget = connectionMethod?.Invoke(null, null) as string,
                luaRemoteConnected = luaMethod != null && Convert.ToBoolean(luaMethod.Invoke(null, null)),
                deviceSegmentsPulled = (winType.GetField("_deviceSegFiles", instanceFlags)?.GetValue(win) as System.Collections.ICollection)?.Count ?? 0,
                deviceSegmentPullError = winType.GetField("_deviceSegmentPullError", instanceFlags)?.GetValue(win) as string,
                status = winType.GetField("_statusLine", instanceFlags)?.GetValue(win) as string,
            };
        }

        [UnitySkill("aiprofiler_stop",
            "Stop AI Profiler recording (StopRecord button). Device captures stream completed small segments to PC while recording; stop flushes and pulls only the remaining final segments before returning. Call before aiprofiler_export.")]
        public static object AIProfilerStop()
        {
            var err = Invoke("StopRecord");
            if (err != null) return err;
            return new { success = true, message = "AI Profiler recording stopped" };
        }

        [UnitySkill("aiprofiler_export",
            "Export the stopped AI Profiler capture for AI analysis (ExportForAI button). " +
            "Returns the exported file path under Assets/ProfilerLogs (newest .txt after export). " +
            "May take a while on large captures (walks all frames).")]
        public static object AIProfilerExport()
        {
            var before = LatestLog();
            var err = Invoke("ExportForAI");
            if (err != null) return err;
            var after = LatestLog();
            if (after == null || after == before)
                return new
                {
                    success = false,
                    error = "导出后未发现新文件——可能没有已停止的采样数据（先 aiprofiler_start/stop）"
                };
            return new { success = true, path = after.Replace('\\', '/') };
        }

        private static string LatestLog()
        {
            if (!Directory.Exists(ProfilerLogsDir)) return null;
            return Directory.GetFiles(ProfilerLogsDir, "*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
    }
}
