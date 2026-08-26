# AI-Skills

[English](README.md) | **中文**

个人设计的 Claude Code skills 与 harness 组件仓库。每个技能独立成目录，目录内自带 `SKILL.md`（技能本体）与 `README.md` / `README.zh-CN.md`（作用与使用方式说明，英文默认、中文对照）；使用时把对应目录复制到 `~/.claude/skills/`（用户级）或 `<项目根>/.claude/skills/`（项目级）即可。

## 技能索引

| 技能 | 说明 |
|---|---|
| [taskforce](taskforce/README.zh-CN.md) | 任务落地引擎（项目经理模式）：主 agent 只当 PM，定档、拆包、并行派发多级模型 subagent（+ Codex 外援），用客观闸门与独立 review 收敛质量，改动归入 P4 pending changelist 交用户拍板；状态外置任务板，扛高频上下文压缩 |
| [profiler-analysis](profiler-analysis/README.zh-CN.md) | Unity 性能采样分析：脚本预处理 AI Profiler 导出（C#/Lua CPU、Mono/Lua VM GC、高频调用、GPU 计数器、内存趋势、帧尖刺、界面打开、场景切换），主 agent triage 后每问题派只读 subagent 深挖根因，再派独立 skeptic 对抗验证，产出带 `文件:行` 改法的 P0/P1/P2 报告；随附通用的 Unity 侧配套（AI Profiler 面板/导出器/运行时采集器/无人值守菜单/Lua 后端抽象 + Miku 适配/纯 Lua 打点适配器，无 Lua 工程也能用，见 [unity/](profiler-analysis/unity/README.zh-CN.md)） |

## 贡献者

- [MitsukiGensei](https://github.com/MitsukiGensei) — 作者与维护者
- [Claude Code](https://github.com/anthropics/claude-code) — 共同作者（技能设计、实现、审核）
- [Codex](https://github.com/openai/codex) — 共同作者（外援开发与交叉审核）

由 AI 协助完成的提交会带 `Co-authored-by` trailer，以便在 GitHub 上归属贡献。
