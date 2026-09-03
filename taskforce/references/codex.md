# codex 编队与派单细则（SKILL.md §4.6 / §4.7 的附录，只在起编队或排障时读）

## 1. 恒定配置（`codex-dev.ps1` 默认值，实测 2026-08-31 / codex-cli 0.150.1）

模型 = `models_cache.json` priority 1（当时是 **gpt-5.6-sol**）、`model_reasoning_effort=ultra`、
thinking 流开（`model_reasoning_summary=detailed` + `show_raw_agent_reasoning=true`）、fast 档（`service_tier=priority`）。
配置对所有 codex 恒定，不逐包调。thinking 流不是给人看热闹的：**写权限包的事后审计只能靠它**（SKILL.md §4.7 复核第 5 条）。
`exec` 下 `effort=ultra` 正常收敛（旧注释"ultra 在 exec 下不可用"已作废）。

## 2. manifest

落 `<task-dir>/codex/fleet-wave1.json`；路径写正斜杠即可。`repoRoot` 填**当前**工作区根的绝对路径——CoW worktree 与原始
workspace 是同 stream 的不同目录 / 不同 client，照抄别处示例会把 codex 派到别的 client 里干活。

```json
{
  "repoRoot": "<工作区根>",
  "workDir":  "<工作区根>/.taskforce/<slug>/codex",
  "maxParallel": 3,
  "packets": [
    { "id": "wp-03", "role": "dev", "promptFile": ".../codex/wp-03.md",
      "allowWrite": true, "timeoutSec": 3600 },
    { "id": "wp-05", "role": "dev", "promptFile": ".../codex/wp-05.md" },
    { "id": "rv-w1", "role": "review", "promptFile": ".../codex/rv-w1.md",
      "schemaFile": "<skill>/schemas/review-findings.schema.json" }
  ]
}
```

逐包可选字段：`access`（`auto | sandbox-ro | prompt-ro | write`，默认 `auto`）、`allowWrite`、`schemaFile`、`timeoutSec`（默认 3600）、
`outDir`（默认按角色：dev 落 workDir/<id>，review/design 落 sealed 区）、`repoRoot`（默认取 manifest 的）。

## 3. 编队纪律细则与实测签名

- **一包一目录**：脚本拒绝两包共用 outDir。`role=review|design` 的产物默认落**任务目录之外**的 sealed 区
  （`$env:TEMP\taskforce-sealed\<fleetId>\<id>`），全队收工后 `-Collect` 才搬进来——"够不着"优于"禁止读"（evidence.md §1 第 5 条）。
- **访问档（`access`）**。⚠️ **codex ≥ 0.150 的 `-s read-only` 会拒绝一切子进程**（2026-08-31 实测 0.150.1：只读包连 `dir` 都
  `blocked by policy`，却仍然 exit 0 交出一份"很自信的空报告"）。所以：
  - 不写文件的包 → `prompt-ro`：绕过沙箱，但 wrapper **自动在 prompt 顶部注入只读契约**；
  - 要改文件的包 → `write`（`allowWrite: true`）：wrapper 注入写契约（只许动独占清单内文件、禁 submit/push/装依赖），仅限规格已写死、独占清单明确的包；
  - `auto` 在旧版 codex 上仍退回真沙箱 `sandbox-ro`；显式要 `sandbox-ro` 而版本已破 → **脚本报错拒跑**，不交出空壳评审。
- **契约文本可改，脚本不可塞中文**：只读 / 写契约正文在 `contracts/read-only.md` 与 `contracts/write.md`，改措辞改这两个文件。
  **不要把中文写回 `.ps1`**——Windows PowerShell 5.1 对无 BOM 的 .ps1 按 ANSI 解码，脚本内的中文字面量在解析期变成乱码
  （2026-08-31 实测：注入的契约到 codex 手里是 GBK-as-UTF8 乱码）。两个脚本因此保持纯 ASCII。
- **失败定性先过滤噪声**：`rmcp::transport … 127.0.0.1:8080/mcp` 连接失败是工作区池工具的 codex 配置挂了一个 Editor 侧 MCP、Unity 没开就刷，
  不影响执行；而 `Not inside a trusted directory and --skip-git-repo-check was not specified` 是真失败——wrapper 已带该 flag，
  出现即说明有人绕过 wrapper 手起 codex（池化 worktree / 池化克隆无 `.git` 且路径不在 codex 信任表里，2026-09-01 实测）。
  鉴权失效 → 提示用户 `codex login`。
- **未信任路径**：`codex exec --skip-git-repo-check -C <root>` 在 CoW worktree（无 `.git`）正常执行，不带该 flag 直接拒跑（2026-09-01 实测）。
- **无会话续接**：wrapper 每次都是新 turn。细节修复 = 重投一个带 finding 全文 + 上一轮改动清单的修复包；方向性错误 = 换 Claude agent 重写。

## 4. 派单开发包 prompt 必带项

与 Claude dev agent 同一套（SKILL.md §4.5；骨架见 convergence.md §6）：包范围 + 独占文件清单 + 接缝登记表中它拥有 / 消费的行 +
验收标准 + `packets/recon.md` 路径 + 预算行 + 工作区根绝对路径 + "遵守项目 CLAUDE.md 与代码规范" + 完工前自跑 §5.0 预扫描与廉价闸门 +
回报契约（≤30 行摘要 + 改动文件清单 + 闸门结果及 log 路径 / mtime）+ 数据缺失不假设 / 移植类不自创绕过。

---

## 附：SKILL.md v1.4.0 原文（§4.6 / §4.7，供追溯）

### 4.6 codex 编队（多 codex 并行）

单个 codex 比 Claude subagent 慢（分钟级起步），但推理深、吃的是**另一个额度池**。
过去的用法是一次只起一个、等它回来再起下一个——那等于把最慢的资源串行化。
现在用**编队**：一次 launch，K 个 codex 同时开工，每个持有自己的包规格与独立输出目录。

**配置对所有 codex 恒定，不逐包调**（`codex-dev.ps1` 的默认值，实测于 2026-08-31 /
codex-cli 0.150.1）：模型 = `models_cache.json` priority 1（今天是 **gpt-5.6-sol**）、
`model_reasoning_effort=ultra`、thinking 流开（`model_reasoning_summary=detailed` +
`show_raw_agent_reasoning=true`）、fast 档（`service_tier=priority`）。
thinking 流不是给人看热闹的：**写权限包的事后审计只能靠它**（§4.7 复核第 5 条）。

**三步用法**：① 每包写一个 prompt 文件 → ② 写 manifest → ③ 后台 launch + 轮询。

manifest（落 `<task-dir>/codex/fleet-wave1.json`；路径写正斜杠即可；`repoRoot` 填**当前**工作区根的绝对路径——
池化 worktree 与原始 workspace 是同 stream 的不同目录 / 不同 client，照抄别处示例会把 codex 派到别的 client 里干活）：

```json
{
  "repoRoot": "<工作区根>",
  "workDir":  "<工作区根>/.taskforce/<slug>/codex",
  "maxParallel": 3,
  "packets": [
    { "id": "wp-03", "role": "dev", "promptFile": ".../codex/wp-03.md",
      "allowWrite": true, "timeoutSec": 3600 },
    { "id": "wp-05", "role": "dev", "promptFile": ".../codex/wp-05.md" },
    { "id": "rv-w1", "role": "review", "promptFile": ".../codex/rv-w1.md",
      "schemaFile": "<skill>/schemas/review-findings.schema.json" }
  ]
}
```

```powershell
# ① launch —— 必须 Bash run_in_background:true（脚本自身阻塞到全队收工）
& "$env:USERPROFILE\.claude\skills\taskforce\scripts\codex-fleet.ps1" -Manifest <manifest>
# ② 轮询 —— 主线随时可跑，只读，不干扰在跑的 agent
& "...\codex-fleet.ps1" -Status  -FleetFile <task-dir>\codex\fleet-<id>.json
# ③ 收口 —— 全队 done 后一次性把产物搬进任务目录
& "...\codex-fleet.ps1" -Collect -FleetFile <...> -Into <task-dir>\xreview
```

**编队纪律**：

- **一包一目录**，脚本拒绝两包共用 outDir。`role=review|design` 的产物默认落**任务目录
  之外**的 sealed 区，全队收工后 `-Collect` 才搬进来——"够不着"优于"禁止读"（§2.4 末两行）。
- **并行上限 3（脚本硬顶 6）**，且与 Claude agent **共用** §4.4 的每波 ≤6 预算。
  加人之前先回答 §2.4 那两行：这个包的规格写满了吗？它拥有哪条接缝？
- **访问档逐包定**（`access`，默认 `auto`）。⚠️ **codex ≥ 0.150 的 `-s read-only`
  会拒绝一切子进程**（2026-08-31 实测 0.150.1：只读包连 `dir` 都 `blocked by policy`，
  却仍然 exit 0 交出一份"很自信的空报告"）。所以：
  - 不写文件的包 → `prompt-ro`：绕过沙箱，但 wrapper **自动在 prompt 顶部注入只读契约**；
  - 要改文件的包 → `write`（`allowWrite: true`）：wrapper 注入写契约（只许动独占清单内文件、
    禁 submit/push/装依赖），仅限规格已写死、独占清单明确的包；
  - `auto` 在旧版 codex 上仍退回真沙箱 `sandbox-ro`；显式要 `sandbox-ro` 而版本已破 → **脚本报错拒跑**，
    不交出空壳评审。
- **契约文本可改，脚本不可塞中文**：只读 / 写契约的正文在 `contracts/read-only.md` 与
  `contracts/write.md`，改措辞改这两个文件。**不要把中文写回 `.ps1`**——Windows
  PowerShell 5.1 对无 BOM 的 .ps1 按 ANSI 解码，脚本内的中文字面量会在解析期变成乱码
  （2026-08-31 实测：注入的契约到 codex 手里是 GBK-as-UTF8 乱码）。两个脚本因此保持纯 ASCII。
- **fleetFile 路径写进任务板**「codex 编队」区：上下文被压缩后，靠它 `-Status` 一条命令恢复全队状态。
- **失败不原样重投**：某包 `state=failed` → 读它的 `stderr_log` 定性（鉴权失效 → 提示用户
  `codex login`），然后**缩包规格或改派 Claude agent**，记任务板。整队起不来 → 明告用户
  "外援降级，全部由 Claude agent 承担"，不要静默吞掉。定性前先过滤噪声：
  `rmcp::transport … 127.0.0.1:8080/mcp` 连接失败是工作区池工具的 codex 配置挂了一个 Editor 侧 MCP、Unity 没开就刷，
  不影响执行；而 `Not inside a trusted directory and --skip-git-repo-check was not specified` 是真失败——
  wrapper 已带该 flag，出现即说明有人绕过 wrapper 手起 codex（池化克隆无 `.git` 且路径不在 codex 信任表里，
  2026-09-01 实测）。

### 4.7 codex 派单开发（对标 opus 档）与主 agent 复核

codex 不只当审核外援，**也承接开发包**：能派给 `opus` 的包就能派给 codex。

**派给 codex 的三条准入（全中才派）**：① 规格已写死（接口 / 独占文件 / 验收标准可机判）；
② 能独立长跑、不需要快速多轮往返；③ 不依赖 Claude 侧工具链（Editor bridge / `editor-cli` /
自动化测试 skill 只有 Claude agent 有）。**反例**：边探索边改的模糊包、需要频繁追问的包、
依赖 Editor 交互的包——这些留给 Claude agent。

**派单 prompt 必带**（与 Claude dev agent 同一套，不因为是外援就放松，见 §4.5）：
包范围 + 独占文件清单 + 接缝登记表中它拥有 / 消费的行 + 验收标准 + `packets/recon.md` 路径 +
"遵守项目 CLAUDE.md 与代码规范" + 完工前自跑 §5.0 预扫描与廉价闸门 +
回报契约（≤30 行摘要 + 改动文件清单 + 闸门结果及 log 路径 / mtime）+
数据缺失不假设 / 移植类不自创绕过。写权限包的"只许改清单内文件"由 wrapper 契约兜底，
**但 prompt 里仍要把清单列全**——契约只说边界在哪，清单才说边界是什么。

**主 agent 复核（一票否决，五条全过才收）**。codex 的产出**不因为模型强而免检**：

| # | 复核项 | 怎么做 | 不过怎么办 |
|---|---|---|---|
| 1 | **越界** | `p4 opened` / `git diff --name-only` 与独占清单求差 | 非空即整包打回 |
| 2 | **闸门** | 不采信自报：要 log 路径 + mtime 晚于该包 launch 时刻 | 缺证据派 `haiku` 复跑 |
| 3 | **独立 review** | 走 §5.1；**永不由 codex 自评**，也不由同队另一个 codex 包审自家 diff | 按 §5.2 处置 |
| 4 | **根因优先** | 抽查有没有 nil 守卫 / try-catch / 特例分支绕过——外援看不到项目历史，最容易在这里打补丁 | 按方向性错误处理 |
| 5 | **thinking 日志** | 写权限包读一遍 `stdout.log` / `stderr.log` 的推理流，确认它没顺手动清单外的东西 | 越界即打回 + 该包降级为 Claude 重写 |

**codex 无会话续接**（wrapper 每次都是新 turn），所以 §5.2 的"细节问题 `SendMessage`
派回原 agent"对它不适用：细节修复 = **重投一个带 finding 全文的修复包**（prompt 里附上
上一轮的改动清单与 blocker）；方向性错误 = 直接换 Claude agent（`opus` / `fable`）重写。
