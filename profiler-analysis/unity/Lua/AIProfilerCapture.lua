--[[
AI Profiler — 纯 Lua 可选适配器（无框架依赖：不用 class / 自定义事件 / 定时器）。

作用：把 Lua 侧的 UI / 场景流程打点桥接到 C# 通用采集器 AIProfiler.AIProfilerCapture，
并周期上报 Lua VM 内存（collectgarbage("count")），让 AI-Profiler-v1 导出的
VIEW_STATS / SCENE_SWITCH / LUA_MEM_TREND 三个 section 有数据。没有 Lua 的工程不需要本文件。

接入（xLua / ToLua / sLua 都一样，只要能调到 C# 静态类）：
    local Capture = require("AIProfilerCapture")
    Capture.init({
        cs = CS.AIProfiler.AIProfilerCapture,      -- xLua；ToLua 写 AIProfiler.AIProfilerCapture（需先生成绑定）
        frameCount = function() return CS.UnityEngine.Time.frameCount end,   -- 可选
    })
    -- 每帧（任意 update 循环里）调用一次，用于周期上报 Lua VM 内存：
    Capture.update(deltaSeconds)
    -- 在 UI 框架里打点：
    Capture.markClick(buttonId)                        -- 按钮点击 / 输入分发
    Capture.markViewLoadStart(viewName)                -- 界面开始加载资源
    Capture.markViewResourceLoaded(viewName, ms)       -- 资源加载完成
    Capture.markViewShown(viewName, totalMs, isSub)    -- 显示完成
    Capture.beginViewFpsWindow(viewName)               -- 界面打开后启动 1s 帧率统计窗口
    Capture.scheduleViewNodeStats(viewName, rootTransform, isSub)
    Capture.beginSceneSwitch(fromName, toName) / Capture.endSceneSwitch(sceneName)
    -- 可选：pcall 守卫，上报被吞掉的 lua error（error unwind 会打断原生 Profiler Begin/End 配对，是采样流污染嫌疑）
    Capture.installErrWatch(function(msg) print("[LuaErrWatch] " .. msg) end)

绑定要求：C# 侧 AIProfiler.AIProfilerCapture 是纯静态类、无委托字段，可直接生成绑定；
ScheduleViewNodeStats 需要 UnityEngine.Transform 参数，项目若不绑该重载可改用 recordViewNodes(viewName, total, inactive)。
]]

local M = {}

local _cs = nil
local _frameCount = nil
local _memIntervalSec = 5.0
local _memElapsed = 0
local _capturing = false
local _maxMemSamples = 2000
local _memSampleCount = 0

--- 取 C# 采集器；未 init 时返回 nil（各打点函数据此静默跳过）
local function _api()
    return _cs
end

--- 初始化：opts.cs = C# AIProfiler.AIProfilerCapture 静态类；opts.frameCount 可选；opts.memIntervalSec 可选（默认 5s）
function M.init(opts)
    opts = opts or {}
    _cs = opts.cs
    _frameCount = opts.frameCount
    if type(opts.memIntervalSec) == "number" and opts.memIntervalSec > 0 then
        _memIntervalSec = opts.memIntervalSec
    end
    _memElapsed = 0
    _memSampleCount = 0
end

--- 是否在采集中（以 C# 侧为准）
function M.isCapturing()
    local cs = _api()
    return cs ~= nil and cs.IsCapturing == true
end

--- 每帧调用：采集期间周期上报 Lua VM 内存（MB）。起止各补一发，保证首末成对。
function M.update(deltaSeconds)
    local cs = _api()
    if not cs then return end
    local capturing = cs.IsCapturing == true
    if capturing ~= _capturing then
        _capturing = capturing
        _memElapsed = 0
        _memSampleCount = 0
        if capturing then
            M.recordLuaMemory()
        end
        return
    end
    if not capturing then return end
    _memElapsed = _memElapsed + (deltaSeconds or 0)
    if _memElapsed >= _memIntervalSec then
        _memElapsed = 0
        M.recordLuaMemory()
    end
end

--- 立即上报一发 Lua VM 内存
function M.recordLuaMemory()
    local cs = _api()
    if not cs then return end
    if _memSampleCount >= _maxMemSamples then return end
    _memSampleCount = _memSampleCount + 1
    cs.RecordScriptMemory(collectgarbage("count") / 1024)
end

-- ---------------- 界面打开 ----------------
function M.markClick(id)
    local cs = _api(); if cs then cs.MarkClick(tostring(id or "")) end
end

function M.markViewLoadStart(viewName)
    local cs = _api(); if cs then cs.MarkViewLoadStart(viewName) end
end

function M.markViewResourceLoaded(viewName, resourceLoadMs)
    local cs = _api(); if cs then cs.MarkViewResourceLoaded(viewName, resourceLoadMs or 0) end
end

function M.markViewShown(viewName, totalLoadMs, isSubView)
    local cs = _api(); if cs then cs.MarkViewShown(viewName, totalLoadMs or 0, isSubView == true) end
end

--- 工程已自行算好各段耗时时直接记一条；clickResponseMs 传 nil 表示无点击配对信息
function M.recordViewOpen(viewName, resourceLoadMs, totalLoadMs, isSubView, clickResponseMs)
    local cs = _api()
    if not cs then return end
    if clickResponseMs ~= nil then
        cs.RecordViewOpen(viewName, resourceLoadMs or 0, totalLoadMs or 0, isSubView == true, clickResponseMs)
    else
        cs.RecordViewOpen(viewName, resourceLoadMs or 0, totalLoadMs or 0, isSubView == true)
    end
end

-- ---------------- 开屏帧率 ----------------
function M.beginViewFpsWindow(viewName)
    local cs = _api(); if cs then cs.BeginViewFpsWindow(viewName) end
end

-- ---------------- 节点使用率 ----------------
function M.scheduleViewNodeStats(viewName, rootTransform, isSubView, delaySeconds)
    local cs = _api()
    if not cs or rootTransform == nil then return end
    cs.ScheduleViewNodeStats(viewName, rootTransform, isSubView == true, delaySeconds or 1.0)
end

function M.recordViewNodes(viewName, totalCount, inactiveCount, isSubView)
    local cs = _api(); if cs then cs.RecordViewNodes(viewName, totalCount or 0, inactiveCount or 0, isSubView == true) end
end

-- ---------------- 场景切换 ----------------
function M.beginSceneSwitch(fromName, toName)
    local cs = _api(); if cs then cs.BeginSceneSwitch(fromName, toName) end
end

function M.endSceneSwitch(sceneName)
    local cs = _api(); if cs then cs.EndSceneSwitch(sceneName) end
end

function M.recordSceneSwitch(fromName, toName, costMs)
    local cs = _api(); if cs then cs.RecordSceneSwitch(fromName, toName, costMs or 0) end
end

-- ---------------- 通用行 ----------------
--- 自定义统计：type 如 "ViewOpen"/自定义，label 如 "界面"，subject 为界面名/路线名
function M.recordLine(type_, label, subject, message, exceeded)
    local cs = _api(); if cs then cs.RecordLine(type_, label, subject, message, exceeded == true) end
end

-- ---------------- pcall 守卫（LuaErrWatch） ----------------
-- 被 pcall 吞掉的 lua error 在日志里完全不可见，但错误 unwind 会跳过途中已发出的原生 BeginSample
-- （Lua 侧插桩区间 / 绑定层的 LuaCallCS 采样），是采样流污染（录制段损坏）的重点嫌疑。
-- 只包 pcall 不包 xpcall（xpcall 必有 handler，项目通常已自行上报）；不改变 pcall 的语义与返回值。
local _errWatchInstalled = false
local _warnQuota = 50

local function _reportSwallowed(warn, err)
    if _warnQuota <= 0 then return end
    _warnQuota = _warnQuota - 1
    local msg = tostring(err)
    if #msg > 300 then msg = msg:sub(1, 300) .. "…" end
    warn("被 pcall 吞掉的 lua error（unwind 会打断原生采样配对）: " .. msg
        .. (_warnQuota == 0 and "（告警配额用尽，后续静默）" or ""))
end

--- 安装 pcall 守卫。warn: function(msg) 输出告警的函数；quota: 最多告警次数（默认 50）。幂等，防热重载叠包。
function M.installErrWatch(warn, quota)
    if _errWatchInstalled or rawget(_G, "__aiProfilerErrWatchInstalled") then return end
    _errWatchInstalled = true
    rawset(_G, "__aiProfilerErrWatchInstalled", true)
    if type(quota) == "number" and quota > 0 then _warnQuota = math.floor(quota) end
    warn = warn or print
    local rawpcall = pcall
    local function check(ok, ...)
        if not ok then
            _reportSwallowed(warn, (...))
        end
        return ok, ...
    end
    pcall = function(f, ...)
        return check(rawpcall(f, ...))
    end
end

return M
