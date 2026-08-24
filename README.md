# AI-Skills

个人设计的 Claude Code skills 与 harness 组件仓库。每个技能独立成目录，目录内自带 `SKILL.md`（技能本体）与 `README.md`（作用与使用方式说明）；使用时把对应目录复制到 `~/.claude/skills/`（用户级）或 `<项目根>/.claude/skills/`（项目级）即可。

## 技能索引

| 技能 | 说明 |
|---|---|
| [taskforce](taskforce/README.md) | 任务落地引擎（项目经理模式）：主 agent 只当 PM，定档、拆包、并行派发多级模型 subagent（+ Codex 外援），用客观闸门与独立 review 收敛质量，改动归入 P4 pending changelist 交用户拍板；状态外置任务板，扛高频上下文压缩 |
