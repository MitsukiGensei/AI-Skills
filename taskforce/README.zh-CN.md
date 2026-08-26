# taskforce — 任务落地引擎（项目经理模式）

[English](README.md) | **中文**

## 作用

把一个需求从「一句话描述」推进到「可交付并闭环质量」的编排型 skill。主 agent 只当**项目经理（PM）**：定档、拆包、并行派发 subagent、用客观闸门与独立 review 收敛质量，最后把改动归入 P4 pending changelist 交用户拍板——自己尽量不写代码。

核心设计：

- **档位自动挡**：按触及文件数、跨介质层数、契约变更、风险区自动定 S / M / L 档。小需求降到轻量档（一个 dev agent + 一次单审），大需求才展开多波并行；换挡由客观触发器驱动，不凭感觉。
- **模型分级派工**：每次派 Agent 显式传 model（haiku 跑腿 / sonnet 机械 / opus 常规 / fable 架构与对抗终验），并支持「投机起草」——规格完整的包降一档起草，审核环节当验证器。
- **Codex 外援**：规格已写死的大工作包可丢给 Codex CLI（gpt-5.6-sol，max + fast 档）后台跑，消耗另一个额度池；默认弹可视终端窗口直播过程。
- **独立 review 收敛**：reviewer 一律全新 subagent、只喂 diff + 验收标准、不喂实现者推理；S 档单审四视角，M 档多模型交叉审核 1 轮，L 档完整多轮交叉审核 + completeness-critic 确认轮。争议 finding 走实证分流，不加辩论轮。
- **状态外置**：全程状态落 `<工作区根>/.taskforce/<task-slug>/taskboard.md`（唯一 SSOT），扛得住高频上下文压缩——压缩后重读任务板即断点恢复，不需要 resume 参数。
- **checkout ≠ submit**：收口只把改动聚合进 pending changelist 供复核，提交决定永远归用户（一票否决项）。

## 使用方式

### 安装

把本目录整体复制到 Claude Code 的技能目录（用户级或项目级二选一）：

```powershell
# 用户级（对所有项目生效）
Copy-Item -Recurse taskforce "$env:USERPROFILE\.claude\skills\taskforce"

# 项目级（仅对某个项目生效）
Copy-Item -Recurse taskforce "<项目根>\.claude\skills\taskforce"
```

### 触发

- 显式：`/taskforce <需求描述或需求文档路径>`
- 自然语言：提到「落地需求」「把这个功能做完」「组一个开发小队」「拆任务并行开发」「让 codex 一起干」等，只要意图是把一个需求实施到可交付并闭环质量，都会自动触发。

### 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--verify` | 关 | 收敛验证模式：改动已做好（手改 / 其他工具产出），跳过定档与拆包，直接对当前未提交改动跑 review 收敛 + 收口 |
| `--no-test` | 关 | 廉价闸门跳过自动化测试（仅静态闸门） |
| `--no-checkout` | 关 | 跳过 P4 收口 |

档位不设参数——自动判定，用户一句话（如「这个走 L」「WP-05 按 S 审」）即可手动覆盖。

### 依赖

- **必需**：Claude Code 的 Agent / SendMessage 工具（subagent 派工与复用）。
- **可选，有则用、无则降级**：多模型交叉审核 skill（M/L 档互审）、专用审查 agent、自动化测试与 lint 闸门、P4 checkout skill、Codex CLI（`codex login` 后外援可用；不可用时全部由 Claude agent 承担并明告用户）。
- Codex 外援脚本假定 Windows + PowerShell 5.1 环境。

## 文件结构

| 文件 | 说明 |
|---|---|
| `SKILL.md` | 技能主体：PM 契约、档位自动挡、任务板、拆分派工、质量闭环、交付收口、长程节奏 |
| `references/convergence.md` | 收敛回路细则（预扫描清单、review 视角清单、完成报告模板），只在跑到对应环节时读 |
| `scripts/codex-dev.ps1` | 以开发者身份跑一轮非交互 Codex CLI（默认只读，`-AllowWrite` 按包放开写权限） |
| `scripts/codex-window-runner.ps1` | 被 codex-dev.ps1 拉起，在可视终端窗口里直播 codex exec 过程（不手动调用） |
