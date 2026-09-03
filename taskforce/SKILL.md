---
name: taskforce
version: 1.5.0
description: TaskForce 是一个把「需求」推进到「可提交的 P4 pending changelist」的任务落地引擎。主会话的 AI 只处理 定档、拆包、并行派工、仲裁、验收；写代码的是按复杂度分级的 subagent（Claude Sonnet / Opus / Fable + 多个并行 Codex CLI agent，codex 既承接开发派单也承接独立审核，主 agent 复核）。自动、独立 review 收敛，质量有保障。全程状态外置到任务板文件，扛得住高频上下文压缩。当用户提到 taskforce、落地需求、把这个功能做完、新需求实现、长程任务、大需求落地、项目经理模式、拆任务并行开发、多 agent 协作开发、组一个开发小队、让 codex 一起干、或要对已有改动做独立 review 收敛验证时触发。即使用户只说"帮我把这个需求做了"或"这个需求很大，帮我组织人手"，只要意图是把一个需求实施到可交付并闭环质量，都应触发此 skill。
---

# taskforce — 任务落地引擎

主 agent 是**项目经理（PM）**，不是程序员。价值在于定档、拆解、派工、仲裁、验收；
写代码的是 subagent。流程假设可能跑很久、上下文随时被压缩，所以**一切可持久信息
落文件，主上下文只保留当前波次的最小工作集**。

**规模不预设**：同一条链路吃从「改 3 个文件」到「跨模块多波并行」的全量程，
靠 §2 的档位自动挡伸缩——小需求不该为大需求的编排成本买单。

## 0. 入口模式

| 模式 | 触发 | 从哪进 |
|---|---|---|
| **完整**（默认） | 给了需求（文档路径 / 链接 / 一句话描述） | §2 定档 起全程 |
| **收敛验证**（`--verify`） | 改动**已做好**（手改、调试 skill 修完、别的工具产出） | 跳过 §2–§4，直接进 §5 review 收敛 + §6 收口 |
| **交接**（`args=<交接单路径>`） | 另一 skill 已完成分析 / 侦察并产出交接单（如 `profiler-analysis` Step 5 的 `perf-handoff.md`） | §2 定档以交接单「建议任务档」为起点（换挡触发器照常生效）；§4.1.0 钉需求 = 把交接单的验收标准 / 明确不做搬进任务板、向用户确认一次；§4.1.1 recon 复用交接单附件（`packets/`）不重探；交接单「收口约束」（如每包独立 CL）**高于** §6.2 默认 |

交接模式下调用方是 P4 团队 skill、本 skill 是 user 级：调用方必须自带降级链（无 taskforce 时走 `/dev` 或自身最小闭环），本 skill 不反向依赖调用方。

`--verify` 的审查目标 = 当前未提交改动（`p4 opened` / `git diff`）；无新功能验收标准时
收敛标准重定义为「客观闸门全绿（含原本失败的用例现在通过）+ 无回归 + 根因修复非打补丁」。
它对"谁改的"不可知，所以能接手任何来源的改动；调试 skill 修完要不要接力由用户定，
**两个 skill 不互相内联**（调试 skill 是团队受控 skill，硬调 user 级 skill 会让别人 sync 后引用到不存在的东西）。

---

## 1. PM 契约（一票否决）

- **PM 不写业务代码**。允许亲手写的只有：任务板/派工 prompt/汇总报告等编排文件，
  以及 ≤5 行的应急粘合修改（并在任务板登记）。其余一律派 subagent。
  PM 自己 Grep/Glob/读 diff/数文件**不算写代码**，是定档与验收的分内事——但
  **只为判断范围**（改了哪些文件、有无越界、规模是否触发换挡），**不为判断质量**
  （这段逻辑对不对）。质量判断一律归独立 reviewer。
- **PM 不亲自读大文件**。需要了解代码现状 → 派 `Explore` agent 查完只回结论。
  主上下文里堆源码 = 挤占长程任务的生存空间。
- **review 必须独立**：reviewer 一律**全新 subagent、只读、只喂 diff + 验收标准**，
  **不喂实现者的推理过程**——否则 review 染上实现偏误，等于自己批自己。
  改代码的和评代码的**永不同人**：reviewer 不改代码，dev agent 不自评。
- **PM 不当第三个审核者**。审核结论以 review 收敛结果为准，PM 只做**判定级验收**：
  看回报的闸门结果、看改动文件有无超出独占声明、看 reviewer 结论与实证分流裁定，
  然后拍板 done / 打回 / 换人重写。**不是审读级审核**（逐行读 diff 判断逻辑对错）。
  **PM 裁决过设计的包尤其如此**：PM 握着最完整的设计意图，是最容易脑补"它这么写
  应该也是这个意思"的人——自己验自己的设计 = 锚定偏误，还顺带把源码搬进主上下文。
- **根因优先，禁打补丁**：修引发症状的根因，不加 nil 守卫 / try-catch / 特例分支 /
  补偿调用掩盖。这条对 dev agent 与 fixer 同样是一票否决（遵循所在项目的 bug 修复规范）。
- **外援产出不免检**：codex 承接的开发包与 Claude agent 走同一套闸门 / 越界检查 /
  独立 review，**不因为它是 gpt 最强档就加权或跳检**（复核清单见 §4.7）。
  同样地，codex 的审核结论也不天然压过 Claude 侧结论——两侧都只有证据算数（§5.2）。
- **能并行的调用必须放在同一条消息里发出**。串行派工 = 白等一倍时间。
- **PM 只在本工作区内活动**：一切读写限于当前工作区根（含 `.taskforce/`）、`~/.claude/taskforce/` 与 scratchpad。
  **禁止**读写其他 CoW worktree / 原始 workspace / 其他项目目录下的 `.taskforce/`、memory、源码（实证见 evidence.md §3.2 第 7 条）。
  要用旧产物 → 用户显式给路径，或用户自己拷进本工作区。
- **PM 上下文有预算**：单次工具回显 ≤ 60 行（超了先 `| head` / `wc -l` / 落文件再读摘要）、不 `cat` 源码、
  任务板用 Edit 定点改而非整篇重写、派工 prompt 给路径不贴正文、`SendMessage` 一事一句。 实证见 evidence.md §3.2 第 2 条。
- **PM 的模型在开工时定死**（会话中 agent 无法自行切档，`/model` 是用户命令）：
  架构判断密集的任务 → 建议用户以 **fable** 开 PM 会话（PM 可亲自裁决设计方案）；
  架构已定、以铺量实施为主 → **opus** 足矣（罕见的设计裁决派 fable agent）。
  拿不准时在任务启动回合向用户提一句建议档位再开工。

---

## 2. 档位与自动换挡（自动挡）

档位分两级，**别只给任务定档**——大任务里的机械包按 S 审、核心包按 L 审，
才是省 token 的真正杠杆。

| 级别 | 挂在谁 | 决定什么 |
|---|---|---|
| **任务档** | 整个需求 | 要不要 recon、要不要分波、要不要 codex 外援、要不要终验 |
| **包档** | 每个工作包 | 该包的 review 强度、起草模型档 |

### 2.1 入口定档（PM 自己 Grep/Glob 估，不派 agent，成本约等于零）

| 信号 | 取值 |
|---|---|
| 预估触及文件数（主信号） | ≤3 → S ｜ 4–12 → M ｜ >12 → L |
| 跨介质 | 纯 Lua ｜ Lua+C# ｜ 再叠 prefab·配表·proto —— 每多一层 +1 档 |
| 契约变更 | 无 ｜ 改既有签名 → ≥M ｜ 新增 public API·事件·协议字段 → ≥M |
| 需求歧义 | 验收标准能写成可机判 ｜ 写不死 → ≥M |
| 高风险区 | 命中确定性玩法逻辑层（帧同步/录像回放类）、网络协议、存档、支付、打包链路 → **L** |
| 任务形状 | 可并行（包间无共享语义）｜ **顺序因果**（后步语义由前步决定）→ **不拆包**：一个 agent 端到端做完，档位只调 review 强度、不调并行度，文件数 >12 也不例外 |

任一「直接 ≥M/L」触发即取高档；否则按文件数主信号定。**拿不准取高一档**——
降档随时可做且免费，升档要返工。

**范围先于档位（一句话需求 / 用户不在场时的一票否决）**：档位只回答"怎么审"，不回答"做多少"。
一句话需求（"把 X 挪过来""把这个文档做完"）先写出**最小可交付（MVP）**的验收标准——用户看到
就能用的最小闭环——并把所有超出 MVP 的项（三方合并 / 恢复历史删除项 / 顺手修的既有 bug /
文档全量逐条）列进任务板「待用户裁决 · 扩展项」。用户不在场时**按 MVP 推进**，扩展项默认不做；
「最保守解释」指**范围最小**的解释，不是"把所有可能相关的都做上"。
L 档只在两种情况成立：需求文档 / 交接单本身明示了全量范围，或用户对扩展项点了头。
实证见 evidence.md §2 v1.3.0（2026-09-01 两个一句话需求被自动展开成 L 档，3h+ / 5h+，用户评价"很简单的任务跑了很久"）。

**审计型需求恒两段式（一票否决）**：需求形如「拆解策划案 → 逐条验证 → 查漏补缺（→ checkout）」时，它是**两个任务**：
① 审计——只派 `Explore` recon + PM 合成 `packets/audit-matrix.md`（每条功能项 DONE / PARTIAL / MISSING / N-A + 证据 file:line
+ 归属层），**把矩阵交给用户后停下**；② 实施——用户从矩阵里勾选要做的项（或说"全做"），再按勾选范围定档拆包。
不许把 ① 的产物直接当 ② 的派工单一口气跑完：矩阵本身就是用户最想先看到的交付物，而全量实施是最贵的路径
（实证 evidence.md §3）。
用户在需求里明说"不用确认、直接做完"才跳过停顿，且仍先落矩阵。

**档位决定 review 强度与编排开销，不决定并行度。** 并行度由任务形状决定：能由一个
opus agent 一次会话完成的活不派第二个——每多一个 agent 付一次入职（读任务板 / recon /
接缝表），而闲置或规格薄的 agent 是团队里最吵的（evidence.md §1 第 2 条）。

### 2.2 档位链路

| 档 | 链路 |
|---|---|
| **S 轻量** | 钉验收标准 → 一个 dev agent 实现（不开 recon、不建 packets）→ 自跑预扫描+闸门 → **1 个 reviewer 单审** → 修 → 收口。**无波次、无交叉审核、无确认轮** |
| **M 常规** | recon 一次 → 分包并行 → 闸门 → 通用交叉审核 skill 跑 1 轮 → 争议实证分流 → 定性派修 → 收口 |
| **L 长程** | M 的全套 + 多波调度 + codex 外援 + 终验跨包集成审视 + 确认轮（单个 completeness-critic） |

### 2.3 换挡触发器（客观，不凭感觉）

**升档**（任一命中即升一档，同一包最多升 2 次）：
- dev agent 实际触及文件 > 入口估算的 2 倍
- 出现接缝契约变更（要在接缝登记表追加版本行）
- review 出**方向性** BLOCK（不是细节）
- 同一 blocker 连续 2 轮复发，或闸门连续 2 轮不过
- dev agent 回报"需要改所有权之外的文件"

**降档**（省钱侧，同样要客观）：
- recon 结论显示实际范围小于预估（"以为跨模块，实际只在一个 Provider 内部"）
- 该包已连续 1 轮全绿且改动面 ≤2 文件 → 复审降到单 reviewer
- 波次收尾只剩机械包（改名、文档同步、格式化）→ 整波按 S 审

**防抖动**：同一包**降档后不得再升回原档**，唯一例外是命中"方向性 BLOCK"这条硬触发。
每次换挡必须写一行进任务板「档位轨迹」区（`WP-03 M→L：review 出方向性 BLOCK`）——
不记就会在上下文压缩后失忆、来回抖。

**手动挡覆盖**：用户任何时点指定档位（"这个走 L"／"WP-05 按 S 审就行"）
**恒高于自动判据**，记进任务板备注；此后自动升档触发器仍生效，
**降档触发器失效**（不许把用户的指定悄悄降回去）。

### 2.4 并行度纪律（七条防线，每开一个新 agent 前自查）

依据 UCL 2026（arXiv 2608.16801，1902 次评分运行 + 244 次密封复现）七条实测失效模式，本 skill 的对应**机制**
（不是提醒，是流程里必须踩到的那一步；实测数字与出处见 [references/evidence.md](references/evidence.md) §1）：

1. **接缝无主则断** → 接缝登记表每条恰好一个 owner；M/L 档波次 1 派工前登记表为空 = 不许派工（§4.1.3）。
2. **薄规格 / 闲置 agent 是通信噪声源** → 宁少勿闲：规格写不满的包并入相邻包；每个 agent 持完整包规格 + 自己拥有 / 消费的接缝行（§4.4.5）。
3. **"协调者"头衔不产生领导力** → PM 的作用力来自拆解与登记表；矛盾靠 oracle 裁定后写进契约列（§4.1.1），不靠"我是 PM"喊话。
4. **文件通道的收益是有条件的** → taskforce 已是链式 + file-first，**不再叠加任何新的文件纪律**（§4.1.3）。
5. **够得着的路径就会被够** → 双引擎评审物理隔离：codex 一包一独立目录，review / design 产物落任务目录**之外**，收工后才 `-Collect`（§4.6 / §5.1）。
6. **加人不等于加产能** → 每波在跑 agent ≤ 6（含 codex），机器级 opus/fable ≤ 4（§4.2）；并行度由任务形状决定；顺序因果链一个 agent 端到端（§2.1 / §4.4.3）。
7. **单次运行 = 样本量 1** → 不从 n=1/2 推政策：投机命中率"降半"而非"连续 2 次即停"（§4.3）；失败不原样重试，先诊断再缩规格或换人（§7）。

---

## 3. 任务板（状态外置，压缩安全的根基）

落点：`<工作区根>/.taskforce/<task-slug>/`（不提交版本控制；P4 项目不 checkout）。
**唯一 SSOT 是 taskboard.md**，不另建 json / 进度文件。

```
taskboard.md    唯一权威状态，每次状态变化立即更新
resume.md       一段话：现在在哪、下一步干什么（波次边界更新）
packets/        每个工作包的派工 prompt 与验收记录（S 档可不建）
codex/          codex 外援的 prompt / 输出 / 日志
```

`taskboard.md` 模板：

```markdown
# <任务名>    任务档：S|M|L
目标：<一句话>
## 验收标准（可测 / 可机判优先）
- [ ] AC1 当<条件>，系统应<行为>
## 明确不做
## 基线
开工前 `p4 opened` / `git status` 快照：<用户既有未提交改动清单>（**永不覆盖**）
工作区形态：<CoW worktree（独立工程） | P4 单 workspace | 仅 git> / client=<P4CLIENT> / 根=<工作区根> / git overlay=<有|无> / Library=<有|无>（§3 工作区形态探测）
PM 锁：`.taskforce/<slug>/pm-lock.json` pid=<CLAUDE_PID>（§3 PM 单例锁）
同期活动任务：<机器级 `_active.md`（§3 同期任务探测）中与本任务独占文件有交集的 slug / client / CL / 文件；无则写"无">
Editor：<bound port=<8090–8100> dataPath 已验 | not-open（已请用户在本工作区开 Unity） | n/a（无 Play 需求）>    ← 本工作区自己的 Editor，不排队（§3 Editor 绑定）
## 波次计划
Wave 1: WP-01, WP-02（并行） → Wave 2: WP-03（依赖 01+02）…    ← S 档留空
在跑（每次派工前数一次）：本任务 <N>/6 含 codex，opus/fable <k>/3；机器级（`_active.md` 各行 agents 列求和）opus/fable <m>/4（§4.2 额度预算）
## 工作包
| ID | 范围 | 独占文件 | 满足AC | 执行者(agent名/codex) | 模型 | 档 | 状态 | review轮次 | 备注 |
|----|------|----------|--------|----------------------|------|----|------|-----------|------|
状态流转：pending → dev → gate → review → fixing → review → done
## 接缝登记表
| # | 接缝 | 两端 | owner（唯一） | 契约（签名 / 数据形状 / 语义约定） | 机判验证 | 验证时机 | 版本 |
|---|------|------|---------------|-----------------------------------|----------|----------|------|
<每条包间接口一行。M/L 档波次 1 派工前为空 = 不许派工；S 档写"无接缝"。
 契约列禁"暂名 / 以 recon 为准"。改契约 = 追加版本行，不改旧行>
## codex 编队
| fleetFile | 波次 | 包 id → 角色 / 写权限 / 状态 | 备注 |
|---|---|---|---|
<每次 launch 记一行；压缩后靠 fleetFile 跑 `-Status` 即可恢复全队状态>
## 档位轨迹
<WP-03 M→L：review 出方向性 BLOCK>
## 投机记录
<只对规格可机判的领域记降档草稿过审/被拒（细节 vs 方向性）；某领域被方向性拒绝 → 该领域投机比例降半>
## 已知风险 / 待用户裁决
## 收敛轨迹
| 包 | 轮 | 闸门 | blockers | 修了什么 |
```

**压缩纪律**：
- 每次派工、每次收工、每次 review 结论 → 先写 taskboard 再干别的。
  设计目标是：**上下文被压缩后，仅凭重读 taskboard.md + resume.md 就能完整恢复**
  （所以不需要 `--resume` 之类的参数，重读任务板天然就是断点恢复）。
- 轮次记录保持 terse：`counts + blocker id + ≤1 行 why + 修了什么`，不写散文复盘。
  被驳回的伪 blocker 记一行 `DISMISSED + 一句依据` 即可。
- subagent 的回报契约统一为：≤30 行摘要 + 改动文件清单 + 闸门结果，禁止贴大段代码回主上下文。
- 波次边界主动向用户提示"现在是 /compact 的安全点"；被压缩后的第一件事 = 重读任务板。

**能力探测**（开跑前一次，结果记进任务板）：`p4 info` / `git status` 定版本控制；
是否有自动化测试 skill、lint、专用审查 agent。**有则用、无则降级**——
降级的是"用哪套工具/规则"，**独立 review 永不降级**。

**工作区形态探测**（能力探测的一部分；三种形态都是合法输入，结论写任务板「基线 · 工作区形态」行，
后面 §3 同期任务探测 / §4.1.2 隔离原语 / §4.5 早锁 / §6 收口都按它分流）：

| 判据（按序） | 形态 | 含义 |
|---|---|---|
| `p4 info` 通 **且** client root 下有工作区池工具的标记文件（各家不同：池工具专属的标记文件，或它注入的 worktree-id 环境变量非空） | **CoW worktree（独立工程）** | 本目录是工作区池工具从预热池写时复制克隆出的 worktree：**自己的 P4 client、自己的 have 表与 opened 列表、自己的 `.taskforce/`、自己的 `Library/` 与 Unity Editor**。与同机其他 worktree（golden、其他任务、原始 workspace）只共享 depot stream。P4 路由靠 client root 下的 `.p4config` 逐目录生效，cwd 在 root 内的任何 `p4` 命令都落到本 client，subagent / codex 不需额外设置；**通常没有 `.git`** |
| `p4 info` 通，无池工具标记文件 | **P4 单 workspace** | 传统工作区；`git rev-parse --show-toplevel` 成功则再记「git overlay=有」 |
| `p4 info` 不通、`git status` 通 | **仅 git** | 非 P4 项目 |

顺带记：codex 可用性——`AGENTS.md` 存在即可；`.codex` junction 在 P4 克隆里不会跟过来，只影响 codex 侧 hooks，不阻断派单
（wrapper 自带 `--skip-git-repo-check -C <root>`，未信任路径也能跑）。

**CoW worktree = 独立工程（一票否决）**：把它当成另一台机器上的另一个项目来编排——

- **边界**：PM 与全部 subagent / codex 的读写只限本工作区根（含 `.taskforce/`）、`~/.claude/taskforce/`、scratchpad。
  不读其他 worktree / 原始 workspace 的 `.taskforce/`、源码、memory；不把别处的 recon / taskboard 搬进来当 prior
  （要用旧产物 → 用户显式给路径）。派工 prompt、codex manifest `repoRoot`、fleet outDir 全部写**本**工作区绝对路径。
- **Editor 绑定（每工作区一个，不排队）**（下文 `Editor bridge` 指常驻 Editor 的桥接进程、
  `editor-cli` 指驱动它的命令行；没有这套工具的工程整条按 `n/a` 跳过）：每个 worktree 开自己的 Unity（首开要等完整导入 `Library/`）。Editor bridge 端口
  自动扫 8090–8100 并按项目路径登记到 Editor bridge 的 registry.json；RuntimeTestServer 自动扫 18091–18100 并把端口写进
  本工程 `Library/runtime_test_server.port`；Editor bridge 与 `editor-cli` 都按 **cwd 所属项目** 匹配实例。所以：
  ① 任何要碰 Editor 的 agent，cwd 必须在本工作区内；② 进 Play / 改资产前先 `editor-cli verify-project pattern=<本 worktree 目录名>`
  （或读 registry 核对 `path`），dataPath 不匹配一律停手；③ 本工作区 Editor 未开 → 一句话请用户「在本工作区开 Unity」，
  记 `UNVERIFIED-EDITOR` 继续做纯文本可做的部分，**不借别的 worktree 的 Editor、不排队、不轮询**。
  「同机只能一个 Editor / 8090・18091 端口固定」是旧规则，已失效（2026-09-02 实测三个 worktree 三个 Editor 同时在线：8090 / 8091 / 8092）。
  ④ **归属不是会话常量**：Editor 卡死 → 心跳停 → 被兄弟 Editor 从 registry 清掉 → 端口解析漂移。每一轮 Play 前、每次 Editor 超时/无输出后重跑 `editor-cli health` 核 `editorInstanceId`；看到 `EDITOR_NOT_BOUND` / `WRONG_PROJECT` / `RUNTIME_NOT_BOUND` 停手，禁止换端口继续（实证 evidence.md §3.2 第 8 条）。
- **跨 worktree 只剩一种耦合：同一 depot stream**。两个 client 各改同一文件要到 submit 才撞车，所以派工前跑一次
  `p4 opened -a <独占清单>`：命中别的 client 的 CL → 该文件不进本任务独占清单（找本任务独占的替代落点，或登记「待用户裁决」），
  并在任务板与完成报告点名对方 client / CL。**不发起跨会话文件所有权谈判**（实证 evidence.md §3.2 第 1 条）。`SendMessage` 只在两种情况发、一事一句、无冲突不回复：
  ① 必须改对方 CL 里已开的文件；② 对方明确请求。

**PM 单例锁与 resume 守卫（一票否决）**：工作区池工具 / Claude Code 可能在原进程仍存活时用同一 transcript 再起一个进程
（实证 2026-09-02 11:20 / 11:27：三个 PM 各被复制成 2–3 个进程，副本给同一批 subagent id 重发续跑、文件被交替覆写，详见 evidence.md §3.2 第 1 条）。机制：

```bash
# 开工第一件事（写锁；CLAUDE_PID 是本 Claude Code 进程 PID，harness 注入）
L=.taskforce/<slug>/pm-lock.json; mkdir -p "$(dirname "$L")"
printf '{"claudePid":%s,"sessionId":"%s","terminal":"%s","root":"%s","at":"%s"}\n' \
  "$CLAUDE_PID" "$CLAUDE_CODE_SESSION_ID" "$TERMINAL_HANDLE" "$PWD" "$(date -Iseconds)" > "$L"
# 任何 resume 标记之后的第一条命令（守卫；Git Bash 下 tasklist 的 /FI 要写成 //FI 防路径转换）
P=$(python -c "import json;print(json.load(open('$L'))['claudePid'])")
if [ "$P" != "$CLAUDE_PID" ] && tasklist //FI "PID eq $P" //NH 2>/dev/null | grep -q " $P "; then echo "DUPLICATE pid=$P alive"; else echo "TAKEOVER"; fi
```

- **resume 标记**（任一出现即触发守卫）：`No completion record was found for … from the previous session`、
  `Continue from where you left off`、`usage limit has reset`、SessionStart:resume。
- `DUPLICATE` → 本会话是副本：**不派工、不 SendMessage 给 subagent、不写任务目录 / CL**，向用户回一行
  「同任务原 PM 进程 <pid> 仍在运行，本会话为副本，已停止；如需接管请先结束该进程」，然后停下。不去和原会话协商。
- `TAKEOVER` → 原进程已死：把锁改成自己的 PID，再按 §7 中断协议读增量落盘、只重派未完成单元。
- 锁只在本任务目录，不进版本控制；§6 收口时删除。

**同期任务探测**（与能力探测同时做，结果写任务板"基线"区）：开工前跑两条——`ls -lt .taskforce/*/taskboard.md`
（本工作区今天活动的兄弟任务）+ `p4 opened -a`（别的 client / CL 开着哪些文件，**多 client 下这是唯一跨 worktree 可见的信号**）。
然后在**机器级** `_active.md` 登记一行
`<slug> | <client> | <工作区根> | <CL> | <独占文件集> | agents=<opus/fable 数>/<总数>`，每次派工 / 收工更新 agents 列，收口时删除。
派工前与它求交集：独占文件重叠 → 按上文「同一 depot stream」处理；agents 列求和用于 §4.2 机器级预算。

**`_active.md` 落在机器级、按 P4 server + stream 分文件，不在工作区里**：
`~/.claude/taskforce/active/<P4PORT 去端口>__<stream 去 // 且 / 换 _>.md`
（例：`~/.claude/taskforce/active/<p4 主机名>__streams_main.md`；仅 git 项目用 `git__<远端 repo 名>.md`；
目录不存在就建）。它只做两件事：跨 worktree 的独占文件可见性、机器级 agent 计数。**不再登记 Editor 占用**（每工作区自己的 Editor），
旧行里的 `editor=` 列读到就忽略。`ListAgents` 列的是本机全部会话（跨 worktree），`SendMessage` 照常可达，但按上文只在两种情况用。

---

## 4. 拆分与派工

### 4.1 拆分原则

0. **先钉需求再拆包**：把目标、**可测验收标准**（借鉴 EARS：`当<条件>，系统应<行为>`）、
   **明确不做的范围**写成任务板骨架（含首版波次计划与接口契约），**向用户确认一次
   再开工**——这是全流程最便宜的一次验证，需求理解偏一度，误差会乘到每个包上。
   验收标准后续既是 review 需求覆盖视角的依据，也是自动化测试断言的一手来源。
   用户不在场时把理解与假设写进"待用户裁决"区，按最保守解释推进。
   **只有"会逼你猜"的歧义/矛盾/关键缺信息才弹 AskUserQuestion**（先断轮、不接在长正文后）；
   能查证的自己查，记入报告"待关注"。
1. **侦察一次，全员复用**（M/L 档）：首波开工前派 `Explore`（sonnet）把代码现状
   （模块结构、关键 API、既有实现、坑）侦察成 `packets/recon.md`（`Explore` 无 Write 工具，
   由 PM 代为落盘）；之后**所有**派工 prompt 附它的路径。不落盘的侦察 = 每个新 agent
   都从零重探同一片代码，这是长程任务最大的隐性浪费之一。侦察结论只进文件，不堆主上下文。
   S 档不开 recon——dev agent 自己读那 3 个文件比多一次派工便宜。
   **recon 一律 `Explore`（sonnet），禁止 general-purpose（不论 sonnet 还是 opus）**：general-purpose 会用 `cat` 整文件读源码、
   一个 recon 跑 100–229 轮（实证 evidence.md §2 v1.3.0、§3.2 第 3 条）。Explore 的回报契约：≤ 80 行、只给 file:line + 一句结论，PM 原样落进
   `packets/recon-<x>.md`。一个需求 recon 拆片 ≤ 4，每片给明确的目录 / 问题清单，不给"把 X 模块摸一遍"。
   **已有同源侦察则做 delta，不全量重探**：**本工作区** `.taskforce/` 下若已有同一需求文档 / 同一模块的
   audit-matrix / recon（先 `grep -l <文档 token 或模块名> .taskforce/*/packets/*.md`；别的 worktree 的不算，要用得用户给路径），本轮只核
   上轮 PARTIAL / MISSING / BLOCKED 项 + 上轮之后的相关 CL（`p4 changes -m 20 <路径>`），
   DONE 项抽样 ≤3 条复核即可。
   **recon.md 固定含「矛盾清单」一节**：需求文档内部矛盾、文档 vs 代码现状矛盾、文档 vs
   资产 / 配置数据矛盾。PM 对照验收标准逐条裁定、写进接缝登记表契约列后才派工；
   裁不了的进"待用户裁决"，相关包不派——两个 agent 各持半条矛盾规则时，通信量和 PM 头衔
   都救不了，只有对着 oracle 裁才行（evidence.md §1 第 3 条）。
2. **独占文件所有权**：每个工作包声明自己独占的文件集合，包间不重叠 →
   并行不冲突，不需要 worktree。确实避不开共享文件时，**首选把该文件单独划成一个
   串行包**；确需并行隔离时按 §3 探到的工作区形态选隔离原语，不许凭习惯用 git worktree：
   - **CoW worktree（独立工程）** → 派到**另一个独立 CoW worktree**（再领一个预热成员，各自 client，
     秒级、不传字节）：`<池 CLI> worktree create --repo <repo id> --name <包名>
     --agent claude|codex --prompt "<派工 prompt>"`（子命令形态按你的池工具）。`--repo` **必须显式给 id**（用池 CLI 的仓库列表命令查；
     工作区池 CLI 目前不能从池化 worktree 目录推断 parent，`--worktree active` 会 `selector_not_found`）。
     该 worktree 的包仍登记在本 PM 的任务板（执行者列写 `worktree:<名>`），用池 CLI 的终端读写
     或 `ListAgents`/`SendMessage` 收报；它的改动在**它自己的 client / CL** 里，§6 收口时单独列 CL 号，
     不能 reopen 到主 CL。
   - **P4 单 workspace 且 git overlay=有** → 允许 `isolation: "worktree"`（Claude Code git worktree）。
     注意它不是 P4 client：worktree 里的 `p4` 命令仍走父目录 `.p4config` 的 client，而文件不在其 view 内，
     只适合纯读 / 产出物落回主工作区的包。
   - **P4 单 workspace 且无 git**、或任何池化克隆（无 `.git`）→ `isolation: "worktree"` 直接报错，**只能串行**。
3. **接缝所有权（文件所有权之外的第二层，一票否决）**：分解必然制造包与包之间的
   接口，**每条接缝恰好一个 owner**——文件都有主、接缝无主，团队就在接缝上断
   （依据 evidence.md §1 第 1 条）。规则：
   - 拆包时把接缝逐条写进任务板**接缝登记表**（§3 模板）：两端 / owner / 契约 /
     机判验证 / 验证时机 / 版本。**M/L 档波次 1 派工前登记表为空 = 不许派工**。
   - 契约列**禁止「暂名」「以 recon 为准」「签名待定」**。签名定不下来 → owner 包先派、
     先把签名（哪怕是桩）落进登记表，再派消费方——这是依赖排序，不是等待。
   - 派工 prompt 只附该包**拥有**与**消费**的接缝行，不附整表。
   - 接缝类型逐类过一遍（本地反复漏的）：跨语言绑定层生成的 wrapper（如 Lua↔C#）；共享 prefab /
     资产的其他消费方（`grep guid`）；跨 CL 的提交序依赖；配表列的客户端 / 服务端可见性；协议字段跨端的
     命名与大小写约定；**非相邻依赖**（跨包共享的常量、阈值、单位、舍入、
     排序约定——owner 明确、值写进登记表，消费方引用不复制）。
   - **改契约 = 在登记表追加版本行**（谁改、改成什么、受影响消费方），无版本行的契约
     改动按越界处理。`SendMessage` 只发一行指针：「接缝 S3 更新至 v2，闸门前重读登记表，
     不需回复」——文件是一对多通道，消息是一对一。**但不要再叠加任何新的文件纪律**
     （每包状态文件、每波同步文件之类）：taskforce 是链式且已 file-first，链式任务再加
     文件纪律只多花 10–17%（UCL 2026 §6）。
4. **包的粒度** ≈ 一个 subagent 一次会话能干完并自测的量；干不完就再拆。
   **但边界不许切在未定语义上**：若两包之间需要造桩、「暂名」、或串行改同一文件，
   说明边界穿过了一条语义缝——**合包**，不是加契约。顺序因果链（后步语义由前步决定）
   交给一个 agent 端到端做，它在脑内就能和解两半约定。
5. **按依赖分波，但波次只是规划视图，不是执行屏障**：一个包的全部前置 done
   就立即派它，不等整波收尾。**关键路径优先**：依赖链最长的包最先派、review
   优先排——总墙钟由它决定，旁支包排队无所谓。
6. **复杂度由真实需求支付**：小需求却要新增多分支/状态标记/跨模块调度，
   先停下核对这些分支是否来自需求本身，否则保持直接、局部实现。

### 4.2 模型分级（每次派 Agent 显式传 `model`，不许全员继承主模型）

**默认档是 `sonnet`，向上升档要有理由**（记在任务板备注列）。主会话若是 Fable，
不传 model 的 subagent 全部按最贵档计费——这是长程任务最大的隐性成本源。

| 复杂度 | model | 典型任务 |
|---|---|---|
| 琐碎 | `haiku` | 跑命令收日志、文件清单/状态核对、格式化、机械替换 |
| 机械（默认） | `sonnet` | 样板代码、批量改名、文档同步、Explore 侦察、按明确规格实现 |
| 常规 | `opus` | 有设计判断的功能开发、常规 bug 修复、单模块重构 |
| 高阶（例外） | `fable` | 架构设计、跨模块方案、复杂算法、疑难调试、对抗性终验 |
| 外援（另一个额度池） | `codex`（gpt-5.6-sol / ultra） | **对标 opus 档**：规格已写死、能独立长跑的开发包；第二引擎独立审核；并行独立起草方案。慢（分钟级起步）、无会话续接、不带 Claude 侧工具链（§4.6 / §4.7） |

**额度预算（与 session limit 的关系）**：Claude subagent 与主会话共用同一账户额度；并行的 taskforce
会话叠加时更是同一个池。实证：2026-09-01 两会话 47 个 agent 同时撞 limit；2026-09-02 三会话 2 小时烧 ≈ 1.65B cache-read 集体撞 limit（evidence.md §3）。因此：
- **每波同时在跑的 opus/fable agent ≤ 3**（dev + review 合计，记在任务板「在跑」行），其余用
  sonnet / haiku / codex（另一个池）；
- **机器级 opus/fable ≤ 4、总 agent ≤ 8**：派工前把 `_active.md` 各行 `agents=` 求和（§3），超了就等；
  `_active.md` 已有 ≥ 2 个活动任务时，新任务开工回合必须告知用户"账户额度已被 N 个任务分摊，建议串行或降档"；
- **PM 模型**：审计 / 移植 / 按文档铺量的任务 PM 用 `opus`；只有设计裁决密集的任务才用 fable 开 PM
  （evidence.md §3.1）；
- **每个 agent 带预算行**（写进派工 prompt）：dev ≤ 120 轮 / 40 分钟，reviewer ≤ 60 轮，recon(Explore) ≤ 60 轮；
  到预算即落盘 + 回报"已完成 / 未完成清单"，不追求做完（evidence.md §3.2 第 4 条）；
- 闭环复审（逐条核对 finding 是否修掉）是机械活 → `sonnet`；接缝扫描 → `haiku`；recon → `Explore`(sonnet)；
- **长跑 agent 必须增量落盘**：运行时验证 / 大包开发的派工 prompt 写明"每完成一个可独立单元
  （一条 R 项、一个文件）立即追加到 `packets/<wp>-report.md`"，被杀后 PM 只重派未落盘部分，
  不从头重跑（§7 中断协议）。

### 4.3 投机起草（推测解码式：小模型写草稿，审核环节当验证器）

- **规格完整则降一档起草**：接口已钉死、独占文件明确、验收标准可机判的包，按 §4.2 定档后**再降一档**派
  （opus 档的活让 sonnet 先写），过审白赚差价，被拒按 §5.2「拒绝即升档」处理。
- **模糊 / 探索性的包不投机**：规格写不死的直接按原档派——返工 + 多一轮 review 比差价贵。
- **方案 / 架构设计恒不投机**（一票否决）：设计产出就是规格本身，无 oracle、审低档草稿有锚定效应、
  fable 审核照样全量读上下文，准确 / 成本 / 速度三头全输。设计包一律 `fable` 直写；**关键架构要更准**用
  「并行独立起草 + 裁决」：fable 与 codex（或两个不同切入角的 fable）**互不可见**各出一版
  （codex 侧 `role=design` 落 sealed 区，Claude 侧 prompt 不附对侧路径）。裁决归属：PM 是 fable → PM 亲自裁、
  **只裁不写**（产出 = 选谁 + 嫁接哪些点 + 理由，合成发回胜方 agent）；PM 低于 fable → 派 fable agent 裁
  （裁决档不低于起草档），PM 复核拍板。
- **命中率记账（样本量 1 纪律）**：任务板「投机记录」只对规格可机判的领域记草稿过审情况；某领域因方向性被拒 →
  该领域投机比例降半，不做"连续 2 次即停"（依据 evidence.md §1 第 7 条）。
- 推论：**PM 把规格写死的功夫 = 投机命中率**。想省钱先花在接口契约和验收标准上，不是给起草模型升档。

### 4.4 花钱纪律（每次派工前过一遍）

1. **先问要不要派**：一次 Grep/Read/Glob 能回答的不派 agent。
2. **再问能不能并**：同模块的活优先 `SendMessage` 给已有 agent（上下文已缓存，
   增量最便宜），而不是新开一个从零读代码。
3. **并行有上限**：每波同时在跑的 agent ≤ 6 个（含 codex），其中 opus/fable ≤ 3（§4.2）。
   **派工前在任务板「在跑」行数一次，超了不派**——dev 还没收工就叠 review、再叠下一波 dev，
   是最容易静默突破上限的形态（实证 2026-09-01：6 dev + 3 review + codex 同时在跑，随后撞 limit）。
   多开不等于快——排队的包留到下一波，别为了"显得热闹"预先全撒出去。
4. **prompt 收紧**：派工 prompt 只带该包必需的规格与文件清单（给路径，不贴正文），不整段粘贴无关
   上下文；general-purpose 只在确实需要全工具时用，纯侦察一律 `Explore`。给 dev / reviewer 的 prompt 固定带一行
   「读文件用 `sed -n a,bp` / `grep -n` 取区间，不 `cat` 超过 200 行的文件；工具回显超 100 行先落文件再读摘要」。
5. **宁少勿闲**：每个派出的 agent 必须持有**完整包规格 + 它在接缝登记表中拥有 / 消费的
   接缝行**。规格写不满的薄包并入相邻包，不为并行度好看拆薄——什么都不持有的 agent
   必须向所有人要一切（evidence.md §1 第 2 条），每多一个
   agent 还要付一次入职（读任务板 / recon / 登记表）。§4.2 备注列记"为什么这个包值得
   单独一个 agent"。

### 4.5 subagent 复用与派工 prompt

- 每次 `Agent` 派工后，把返回的 **agent 名字/ID 记进任务板执行者列**。
- 同一模块的后续工作（修 review 问题、增量需求、追问）一律 `SendMessage`
  发回**原 agent**——它的上下文还在，不必重读代码。
  只有新领域 / 原 agent 已污染跑偏时才开新 agent。
- 派工 prompt 固定包含：包范围、独占文件清单、接缝登记表中该包拥有 / 消费的行、
  **验收标准（可机判优先）**、`packets/recon.md` 路径（M/L 档）、**预算行**（§4.2）、**工作区根绝对路径 + "只在此目录内读写"**、
  "遵守项目 CLAUDE.md 与代码规范"、
  **完工前自跑 §5.0 预扫描 + 廉价闸门 + 自有接缝验证**、回报契约（≤30 行摘要 + 改动文件清单 +
  闸门结果及其 log 路径 / mtime + 接缝验证结果）、禁止碰所有权之外的文件。
- **早锁（CoW worktree 形态必做；单 workspace 可选）**：派工时 PM 先建本任务 WIP CL（`p4 change -i`，
  描述 `taskforce <slug> WIP`），把该包**独占清单里已存在的文件** `p4 edit -c <CL>` 锁进去（新增文件由 dev
  完成后 `p4 add -c <CL>`）。目的：多 client 下别的 worktree 未 checkout 的改动互不可见，`p4 opened -a` 是
  唯一跨 client 的信号——不早锁，两个 worktree 各改同一文件要到 submit 才撞车。早锁零改动的文件在 §6 由
  `p4 revert -a -c <CL>` 自动撤回；§6 的 checkout skill 会把已开文件 `reopen` 进新 CL。`p4 edit` 时 P4 提示
  `also opened by <别的 client>` → 那是兄弟任务，按 §3 同期任务协调后再定，不硬改。WIP CL 号写进任务板
  「基线」行，dev agent 的 P4 归属守卫（下条）以它为「本任务 CL」。
- 四条给 dev agent 的硬约束（写进 prompt）：
  - **数据缺失不假设**：发现数据来源有误 / 字段不存在 → **空实现 + TODO 标记**并回报，
    **不自行编造来源**；
  - **移植类需求不自创绕过**：遇缺失依赖继续从源头移植，禁止 nil 守护 / 空逻辑绕过；
  - **P4 归属守卫**：`p4 revert` 只允许 `p4 revert -a -c <本任务 CL>`（撤本 CL 内零改动文件）；对单个
    文件 revert / `p4 edit -c` 前先 `p4 opened <file>`，`change N ≠ 本任务 CL` 一律不动（实证 2026-09-01 误 revert 兄弟 CL 致对方丢 Drequire，见 evidence.md §2b）。需要往共享文件里加注册 /
    登记时优先找本任务独占的替代落点（如 `Binary/Game.json` 而非 `WorldMapModule.lua`）；
  - **需 Editor 落盘的改动先验绑定再查状态**：先 `editor-cli verify-project pattern=<本 worktree 目录名>`（§3 Editor 绑定），
    再看 `isPlaying`——`true` 时 Editor bridge 写入不落盘；本工作区 Editor 未开或在 Play → 记 `UNVERIFIED-EDITOR`，
    转做纯文本可做的部分并回报，不轮询、不借别的 worktree 的 Editor。

### 4.6 codex 编队（多 codex 并行）

codex 慢（分钟级起步）但推理深、吃**另一个额度池**；用**编队**一次 launch K 个并行，不要串行等。
配置对全队恒定（gpt-5.6-sol / effort ultra / thinking 流开 / fast 档，见 [references/codex.md](references/codex.md) §1）。
**三步**：① 每包一个 prompt 文件 → ② 写 manifest（字段见 codex.md §2；`repoRoot` 必须是**本**工作区根绝对路径）→ ③ 后台 launch + 轮询：

```powershell
# ① launch —— 必须 Bash run_in_background:true（脚本自身阻塞到全队收工）
& "$env:USERPROFILE\.claude\skills\taskforce\scripts\codex-fleet.ps1" -Manifest <manifest>
# ② 轮询 —— 只读，不干扰在跑的 agent
& "...\codex-fleet.ps1" -Status  -FleetFile <task-dir>\codex\fleet-<id>.json
# ③ 收口 —— 全队 done 后一次性把产物搬进任务目录
& "...\codex-fleet.ps1" -Collect -FleetFile <...> -Into <task-dir>\xreview
```

**编队纪律**（细节与实测签名见 codex.md §3）：
- **一包一目录**；`role=review|design` 产物落任务目录之外的 sealed 区，收工后 `-Collect` 才搬进来。
- **并行上限 3（脚本硬顶 6）**，与 Claude agent 共用 §4.4 每波 ≤ 6 预算；加人前先过 §2.4 第 1 / 2 条。
- **访问档**：不写文件的包 `prompt-ro`（wrapper 注入只读契约），要改文件的包 `write`（注入写契约，仅限规格已写死的包）。
  ⚠️ codex ≥ 0.150 的 `sandbox-ro` 拒绝一切子进程却 exit 0 交空报告——脚本对显式 `sandbox-ro` 报错拒跑。
- **契约文本在 `contracts/*.md`，两个 `.ps1` 保持纯 ASCII**（PowerShell 5.1 对无 BOM 脚本按 ANSI 解码，中文会乱码）。
- **fleetFile 路径写进任务板**「codex 编队」区，压缩后靠 `-Status` 恢复全队状态。
- **失败不原样重投**：读 `stderr_log` 定性（噪声与真失败判别见 codex.md §3）→ 缩包规格或改派 Claude agent；
  整队起不来 → 明告用户"外援降级"，不静默吞掉。

### 4.7 codex 派单开发（对标 opus 档）与主 agent 复核

能派给 `opus` 的包就能派给 codex。**三条准入全中才派**：① 规格已写死（接口 / 独占文件 / 验收标准可机判）；
② 能独立长跑、不需要快速多轮往返；③ 不依赖 Claude 侧工具链（Editor bridge / `editor-cli` / 自动化测试 skill）。
边探索边改、需频繁追问、依赖 Editor 交互的包留给 Claude agent。

**派单 prompt** 与 Claude dev agent 同一套（§4.5；骨架见 convergence.md §6）。写权限包的边界由 wrapper 契约兜底，
**但 prompt 里仍要把独占清单列全**——契约只说边界在哪，清单才说边界是什么。

**主 agent 复核（一票否决，五条全过才收）**，codex 产出不因模型强而免检：

| # | 复核项 | 怎么做 | 不过怎么办 |
|---|---|---|---|
| 1 | **越界** | `p4 opened` / `git diff --name-only` 与独占清单求差 | 非空即整包打回 |
| 2 | **闸门** | 不采信自报：要 log 路径 + mtime 晚于该包 launch 时刻 | 缺证据派 `haiku` 复跑 |
| 3 | **独立 review** | 走 §5.1；**永不由 codex 自评**，也不由同队另一个 codex 包审自家 diff | 按 §5.2 处置 |
| 4 | **根因优先** | 抽查 nil 守卫 / try-catch / 特例分支绕过——外援看不到项目历史，最容易在这里打补丁 | 按方向性错误处理 |
| 5 | **thinking 日志** | 写权限包读一遍 `stdout.log` / `stderr.log` 推理流，确认没顺手动清单外的东西 | 越界即打回 + 降级为 Claude 重写 |

**codex 无会话续接**：细节修复 = 重投一个带 finding 全文 + 上一轮改动清单的修复包；方向性错误 = 换 Claude agent 重写。

---

## 5. 质量闭环（每个工作包必经）

```
dev 完成 → 预扫描 → 廉价闸门 → review（档位定强度）→ 细节问题 → 派回原 agent 修 → 复审 → done
              ↘ 不过就打回原 agent            ↘ 方向性错误 → 升档重写（不进修复循环）
```

### 5.0 预扫描 + 廉价闸门（dev agent 完工前自跑，最便宜的两道）

**预扫描**（grep/Glob 自查，比一个 subagent 读千行文件省两个数量级 token）：
改一处同步全引用 / 无残留旧模式 / 多个产出点一致 / 依赖真实存在（配表行、proto 字段、
资源要 probe 验，不是读码猜）。清单细则见 [references/convergence.md](references/convergence.md)。
它**不替代**独立 review（独立性查的是实现者自己的盲区），但把「机械漏改」类 blocker
在昂贵 subagent 之前消化掉。

**廉价闸门**：lint / 编译 / 相关测试 / 自动化测试 skill（有 Unity 时）/ **自有接缝验证**
（接缝登记表里该包为 owner 的每一行，跑其"机判验证"列：grep 全调用方 / 编译 / wrapper
重生成编译 / guid 反查）/ **本包运行时冒烟**（触及 UI / 场景 / 资产 / 运行时注册的包，§5.5 规则 1）。
- **闸门结果不采信自报**：回报必须附 log 文件路径 + mtime（晚于本轮 dev 开始时刻），
  否则视为未过闸——由 `haiku` runner 独立复跑后才算数（实证 2026-08-20 stale 编译自报，见 evidence.md §2b）。
- **越界检查脚本化**：`p4 opened -c <CL>` **∪** `p4 diff -f -sa`（allwrite 下没 `p4 edit` 就改的文件不会
  出现在 `opened` 里）/ `git diff --name-only` 与独占声明求差，非空即打回，
  不靠 PM 肉眼；契约改动无登记表版本行同样按越界处理。
- **不过闸的直接打回原 agent，不进 review**——免费的精确验证先滤掉注定被拒的草稿。
- **测试失败是回路里的一种 blocker，不是终点**：自动化测试 FAIL 记成 `source=autotest`
  的 blocker 流进 §5.2 修复，下一轮重跑。Unity 不可用 → 降级记 `UNVERIFIED`，不算 fail。

### 5.1 review 分级路由（全流程最贵的环节，按**包档**分流）

先生成本轮 diff 工件（`p4 diff -du` / `git diff`）落一个 `.diff` 文件，
**所有 reviewer 的输入 = 这份 diff + 验收标准 + 规则路径**，不让每个 reviewer 各自 Read 整模块。

**双引擎独立性隔离（一票否决）**：两侧 reviewer 的输入与产物目录物理隔离——codex 输入单独复制一份（`codex/input.diff`），
两侧产物先写到任务目录外、双方都完成再搬进 `xreview/`（"禁读"改为"够不着"，依据 evidence.md §1 第 5 条、§2b 2026-08-24）。
外援 wrapper 写明**反造假契约**（produce or fail）。PM 收外援结论先验真四项：输出 mtime 晚于启动、有 stdout/stderr 日志链、
内容与对侧首行不同、该包不是 `sandbox-ro`（§4.6）；任一不过即作废重跑。

| 包档 | review 强度 |
|---|---|
| **S**（含琐碎改动：配置、改名、几行胶水） | **1 个** `code-reviewer` 或 `sonnet` reviewer 单审，**四个视角写进同一份 prompt**（正确性 / 根因-补丁 / 项目规则 / 需求覆盖），不各开一个 agent |
| **M** | **双引擎并行独立审**：Claude reviewer subagent（`opus`）+ codex 编队 `role=review` 包各出一份**结构化 findings**（`schemas/review-findings.schema.json`），再走 §5.2 的一轮质证收敛；相邻小包攒到同波结束**合并成一次**，不逐包各跑一轮 |
| **L**（核心/高风险） | M 的双引擎，每侧再加一个**不同切入角**（一侧正确性 / 根因，另一侧需求覆盖 / 项目规则），仍是各自独立；**确认轮加 1 个 completeness-critic 单镜头**（"前几轮漏了什么"），不重开全套 |

**包档按内容定，不继承任务档**：从 depot 历史 / 参考工程（w3、sp）**搬运还原**的文件、配表 / 图标搬运、改名、文档同步
= 机械包 → 恒按 **S 审**，哪怕任务是 L 档；只有本任务新写的业务逻辑才吃 M/L 双引擎。
**每波 reviewer 数 ≤ 该波 dev agent 数**，同波相邻小包合并成一次 review（实证 evidence.md §3.2 第 5 条）。

**不默认外包给通用的交叉审核 skill**：它产出自然语言意见，无「处理建议 + 处理方式 + 根因归属层」与机器可归并的表态字段，
收敛只能靠 PM 手工对齐两份散文；其 codex 侧走 `max` 而非 `ultra`，§5.2 归并规则对它不成立。用户显式要求时才用。

**设计意图保真**：reviewer 只拿 diff + 验收标准，不知设计理由。优先把设计意图**编码进验收标准与接口契约**（需求覆盖视角天然验它）；
编不动的架构约束 / 分层意图 → **只在 L 档核心包**加一个 `fable` 档 **design-conformance reviewer**（输入 = 设计文档 + diff，不喂实现者推理），
它是独立的第 N 个 reviewer，**不是 PM 本人**（§1 锚定偏误）。

**reviewer 硬约束三条写进每份 prompt**（细则与各视角要点见 [references/convergence.md](references/convergence.md) §2）：
① 只读本轮 diff + 命中行直接上下文与直接调用方 + 验收标准 / 规则路径，禁止通读未改模块；
② pre-existing 瑕疵至多 INFO，但「因本次改动才需跟改却漏掉」的点是 BLOCK；
③ terse 输出：`[级别] file:line — 一句话 — ≤1 行证据`，无则 `NO BLOCKERS`，末行 `BLOCK: N`。

### 5.2 交叉收敛协议（快速收敛，严禁各说各话）

双引擎的价值在于**两种偏误不重叠**，不在于开辩论会：收敛 = **机械归并 + 至多一轮质证 + 一次实证裁决**，
任何一步不许退化成"我觉得 / 它觉得"。prompt 骨架与归并工作表见 convergence.md §5。

- **Round 0 独立出结论**（两侧互不可见）：按 `schemas/review-findings.schema.json` 输出；**没有 `fix` 的 finding 不算 finding**，
  `fix.layer` 必须给根因归属层（code / config-table / asset / proto / third-party / spec，见所在项目的修复归属层级规范）。
- **Round 1 唯一一轮质证**：按 `schemas/review-rebuttal.schema.json` 逐条 `AGREE / PARTIAL / REJECT`。三条硬约束：
  **未表态视同 AGREE**；**不开新战场**（漏掉的 BLOCK 走 `missed_by_other`，至多 3 条）；**无证据的 REJECT 作废按 AGREE 计**。

**归并规则（机械执行，PM 不参与辩论）**：

| 两侧状态 | 结论 | 动作 |
|---|---|---|
| 双方都提出 / 一方提出另一方 AGREE | **accepted** | 进修复队列 |
| 一方 REJECT 且带代码级证据，另一方无反证 | **dropped** | 任务板记一行 `DISMISSED + 依据` |
| 双方各持证据对立，或任一方 `needs-probe` | **争议** | 派 verifier 实证，见下 |
| PARTIAL（严重度或根因不一致） | 按**较低严重度 + 较深根因**收编 | 进修复队列，根因以能被证据支撑的那个为准 |
| 仅 WARN / INFO 级争议 | **不进 probe** | 记任务板"已知风险"，不阻塞收敛 |

- **实证裁决是终局**：BLOCK 级争议派一个 `opus` verifier 对着代码取证（调用链 / 最小复现 / 测试 / runtime probe），
  产出 `accepted | dropped` + 一行证据，两侧不再表态。**每包 probe 队列 ≤ 3 条**（severity × confidence 排序），溢出登记"已知风险"。
- **修法冲突（`fix_agreement=different`）PM 一次拍板**：① 根因归属层正确（跨层补偿直接淘汰）→ ② 改动面最小 → ③ 合既有惯例；
  写任务板一行，不再征求两侧意见。
- **时限与降级**：codex 侧超时或 `state=failed` → 用单侧结论继续，任务板记 `XREVIEW-DEGRADED: codex <原因>`，不空等也不当"没问题"。
- **定性派修（拒绝即升档）**：**细节问题**（边界漏判、命名、局部逻辑）→ `SendMessage` 派回**原开发 agent**（附 finding 原文与 `fix.how`；
  原开发者是 codex 则重投修复包）；**方向性 / 架构性错误** → **不进修复循环**，按原档或升一档**换 agent 重写该包**，finding 附给
  新 agent 当反面参考；拿不准按方向性处理。投机降档包被方向性拒绝 → 记「投机记录」并触发包档升档（§2.3）。
- **修复根因优先**：禁 nil 守卫 / try-catch 吞错 / 特例分支掩盖。测试类 blocker 按结构化调试方法论；临时埋点写文件不写 console
  （按项目的埋点规范落文件日志，`[DEBUG-BEGIN]/[DEBUG-END]` 标记），修复确认后 用项目的埋点清理工具 + grep 验无残留。

### 5.3 收敛判定与升级守卫

**复审降档**：细节修复后**不重跑全量交叉审核**——派一个 `sonnet` reviewer 逐条核对
finding 是否闭环 + 修复 diff 有无引入新问题即可（机械核对不需要 opus；它发现新的结构性问题时
再升 opus 单看那一条）；只有修复本身涉及结构性改动时才重跑交叉审核。

**收敛标准**：无未处置的 BLOCK（仍在争议中的也算未处置，必须先过实证分流）、
WARN 已修或已登记"已知风险"或用户明示接受、性能类 finding 已落实、客观闸门全绿。
**一轮干净即收敛**；仅 **L 档**要求再过一个 completeness-critic 确认轮。

**升级守卫**（命中即停，把卡点写进任务板"待用户裁决"并呈报用户，**不许无限空转**）：

| 守卫 | 条件 | 结论 |
|---|---|---|
| 硬上限 | 细节修复循环 > 3 轮（升档重写后重新计数，且至多重写一次） | `CAPPED` |
| 无进展 | 本轮 blocker 数 ≥ 上轮，或同一 blocker（同 file+line+主旨）连续 2 轮复发 | `STUCK` |
| 假设崩 | 修复中发现需求/计划的核心假设不成立 | `HALT`（回 §4.1 重钉需求，或升级用户） |

升级 = 诚实停下 + 输出"卡在哪、试过什么、剩哪些 blocker"，**不是失败**。

多个包可以流水线化：A 包在 review 时，B 包继续 dev，不设全局屏障。

### 5.4 接缝扫描（M/L 档）与终验（仅 L 档）

**接缝验证不等终验**——每包闸门全绿 ≠ 集成通过：错配对每个 agent 都不可见，成品也能跑
（实证 2026-08-20 共享 prefab 炸穿另一模块的相机，见 evidence.md §2b）。
- **owner 闸门时**：该包自跑自有接缝验证（§5.0）。
- **每个波次边界**：派 `haiku` 跑接缝登记表**全表**扫描（grep / 编译 / wrapper 重生成 / 资源引用反查，
  成本 ≈ 一批 grep），结果写任务板；M 档且包数 ≥2 且登记表非空即必跑。
- **L 档终验**：全部包 done 后派一个 `fable` agent 做跨包集成审视——输入 = **接缝登记表全绿
  证据 + 全量 diff**，而不是从头找缝；并派 agent 跑项目既有闸门（lint / 测试 / 构建）。
  全绿才进 §6。
- **终验结论与前序闭环复审矛盾时不开辩论、不让 dev 反驳**：直接按 §5.2 派 verifier 实证
  （实证 2026-09-01 `CardStatusCtrl` 三段往返，见 evidence.md §2b）。

终验若仍新发现接缝 → 说明 §4.1.3 的接缝类型枚举漏了一类，补进去。

### 5.5 运行时验证（Editor Play）——前移、分片、缺陷先钉根因

**问题形态**见 evidence.md §2b（2026-09-01 `task-d`：4 轮 Play 2h20m 占墙钟 45%、一个缺陷连改 3 次、一个假缺陷派修再撤回）。

三条规则：

1. **验证前移到包闸门**：触及 UI / 场景 / 资产 / 运行时注册（Drequire、ViewDefine、AA 地址）的包，
   完工闸门必含**本包冒烟**（1–3 条本包核心断言，自动化测试 skill 或 `editor-cli` probe），由 dev agent 在**本工作区自己的**
   Editor 上跑（§3 Editor 绑定：先 `verify-project`）。本工作区 Editor 未开时记 `UNVERIFIED-EDITOR` 先进 review，
   但**不进 §6 收口**——请用户在本工作区开 Unity，不借别的 worktree。
2. **集成验证分片 + 只复测失败项**：波次收尾的跨包运行时验证条目 ≤ 8（只保留跨包链路，包内断言已在
   冒烟里跑过）；条目 > 8 或预计 > 30 分钟 → 拆成多片，每片一个 agent、一次 Play 会话；每条结果
   **即时追加落盘**（§4.2 额度预算）。缺陷修复后**只复测失败项 + 直接相关项**，不从 R1 重跑。
3. **缺陷先钉根因再派修（一票否决）**：验证 agent 报缺陷前必须在 Play 现场完成三件事——
   ① 用**真实调用路径**（真按钮 / 真 handler 注册表 / 真事件）复现 ≥ 2 次，排除 probe 自身的调用约定
   差异；② probe 出根因证据（nil 的是哪个变量、哪一层没走到、资产 meta 哪个字段、prefab 是否模块常驻）；
   ③ 按 [references/convergence.md](references/convergence.md) §7 的缺陷记录格式落盘。
   PM 派修时把该记录整段附给 dev agent，dev 的修复方案**必须引用其中的证据**，不许凭读码猜；
   **同一缺陷第 2 次修复复测仍失败 → 停止派修**，按 §5.2 派 verifier 实证根因，不许第 3 次盲修。

---

## 6. 交付收口（checkout ≠ submit）

**触发**：收敛且未传 `--no-checkout`。升级类结论（`CAPPED` / `STUCK` / `HALT`）**默认不收口**——
代码未收敛，不把半成品塞进 CL；报告里提示"如需暂存，手动 checkout"。

1. **文档同步**：改动涉及模块公开接口且工程有文档同步 skill
   → 跑一次对应模块的文档同步。代码是唯一真理。
2. **归入一个新的 P4 pending changelist**：

   | 能力 | 动作 |
   |---|---|
   | 有专用 checkout skill | 调用它——自动处理 edit/add/delete + `.meta` 配对 + 建新 CL |
   | 仅 P4、无 skill | `p4 reconcile` 收集改动 → 建新 CL，description 填需求摘要 + 收敛轮数 |
   | 仅 git | 跳过（各包的检查点 commit 已是收口），报告里注明 |

   新增/删除 C# 脚本**连同 Unity 同名 `.meta`** 一并处理。CL 号写进任务板。
   **CoW worktree 形态补三步**（单 workspace 若用了早锁只做 ①）：① 先 `p4 revert -a -c <WIP CL>` 撤早锁中
   零改动的文件，checkout skill 会把其余已开文件 `reopen` 到新 CL，WIP CL 变空后 `p4 change -d`；② 派到独立
   CoW worktree 的包在**它自己的 client** 里各走一遍本步（不同 client 的文件不能 reopen 进同一个 CL），
   报告按 client 分列 CL 号；③ 从机器级 `_active.md` 删本任务行、删 `pm-lock.json`（§3）。worktree 本体不由 taskforce 删除——
   是否 `<池 CLI> 的 worktree 删除命令` 归用户。
3. **绝不 submit**：收口只把改动聚合进 **pending** changelist 供复核，
   提交决定永远归用户。这是一票否决项。
4. **输出完成报告**（模板见 [references/convergence.md](references/convergence.md)）：结论 +
   验收标准达成 + 产出文件 + 收敛轨迹 + **接缝统计**（登记 N 条 / owner 全覆盖 / 波次扫描 K 次 /
   终验新发现接缝 0）+ **待关注**（空实现 TODO / UNVERIFIED 与 UNVERIFIED-EDITOR 断言 /
   视觉副作用 / 已知风险 / 升级类未解 blocker）+ CL 号 + 下一步。
5. 可选：高频 blocker / 规范缺口 / 新陷阱 → 建议沉淀进项目知识体系。

---

## 7. 长程节奏（M/L 档）

- **波次边界必做**：更新 taskboard + resume → `haiku` 接缝扫描（§5.4）→ 向用户播报进度
  （一张状态表）→ 提示可 /compact → 发起下一波。**别等 150k 才压**：任务板齐全的前提下，
  越早压越省；整个任务收尾后提示用户 /clear 再开新任务，不带着尸体上下文续命。
  PM 无法自己 /compact，用户不在场时没人会压——所以真正的杠杆是 §1「PM 上下文有预算」：少进来比多压掉可靠。
- **等待期不空转**：codex 编队或慢包在后台跑时，继续推进其他包 / 侦察下一波。
  查进度用 `codex-fleet.ps1 -Status -FleetFile <...>`（只读、不打扰在跑的 agent），
  别去 tail 它的日志猜进度。绝不写"我预计它会返回…"——没收到任务通知就是还在跑。
- **等待 Editor 不算推进**：本任务只剩 Play 验证而本工作区 Editor 未开 → 向用户播报一次
  「请在本工作区开 Unity，其余已就绪」并进入 /compact 安全点；不轮询、不借别的 worktree 的 Editor。
- **被 session limit / 进程重启打断**：恢复后**第一条命令是 §3 的 PM 锁守卫**——`DUPLICATE` 就停，
  `TAKEOVER` 才读各长跑 agent 的增量落盘（§4.2）、只重派未完成的单元；
  把「中断时刻 + 被杀 agent + 已落盘到哪」记进任务板「档位轨迹」区，别从头重跑。
  「No completion record was found … from the previous session」**不等于** agent 死了——原进程可能还在跑它。
- **卡死/空产出不原样重试**：agent 卡死、返回为空或明显跑偏 → 先弄清它卡在
  哪（读它的最后回报/日志），然后**缩小包规格或升档换人**重派，并记入任务板。
  不带诊断的原样重试是在花钱抽样——同配置的两次运行本可以差很远（UCL 2026 §8），
  但没有诊断的重试没有信息增益。
- **用户不在场**（自主运行）时：可逆的按计划推进；不可逆动作（提交、删除、
  对外发布）与真分歧才停下等用户。
- **AskUserQuestion 只在三种情况合法**：① 不可逆动作；② 穷尽 grep / read / runtime probe
  仍无法定序的真冲突；③ 需求层真主观偏好。其余一律自己干、写报告、末尾汇总待关注。
- 项目级规范（编码规约、提交流程、目录禁区）以所在项目的 CLAUDE.md 为准，
  本 skill 只管编排，不覆盖项目规则。

---

## 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--verify` | 关 | 收敛验证模式：跳过 §2–§4，对当前已有改动直接跑 §5 收敛 + §6 收口 |
| `--no-test` | 关 | 廉价闸门跳过自动化测试（仅静态闸门） |
| `--no-checkout` | 关 | 跳过 §6 的 P4 收口 |
| `--codex=N` | 3 | codex 编队本波并行上限（脚本硬顶 6，且与 Claude agent 共用每波 ≤6 预算）|
| `--no-codex` | 关 | 不用外援：开发全由 Claude agent 承担，review 退化为单引擎（任务板记 `XREVIEW-DEGRADED`）|

档位不设参数——由 §2 自动判定，用户一句话即可手动覆盖。

---

## 参考

- Destefanis & Aste, *When Agents Coordinate: Measuring Coordination in Multi-Agent AI Coding*, UCL, 2026 —
  https://arxiv.org/abs/2608.16801 。七条防线（§2.4）的实测数字、v1.1.0–v1.4.0 修订史与各次实测签名见
  [references/evidence.md](references/evidence.md)。
- v1.5.0（2026-09-02）：复盘同日并行的三个 CoW worktree 任务（`task-a` / `task-b` / `task-c`，
  10:35–12:36 撞 session limit，零收口；数据见 evidence.md §3）。根因：① 工作区池工具在 11:20 / 11:27 用同一 transcript 重起进程而
  原进程未退出，三个 PM 各被复制成 2–3 份，副本按中断协议重派同一批 subagent；② 三个 fable PM 0 次 compact、上下文 460–520K；
  ③ recon 全用 general-purpose 而非 Explore；④「单 Editor 排队」假设已过期（三个 worktree 三个 Editor 同时在线）。
  修订：§3 重写为「CoW worktree = 独立工程」（边界 / 每工作区 Editor 绑定 / 只剩 depot 耦合）+ PM 单例锁与 resume 守卫；
  §1 PM 只在本工作区活动 + 上下文预算；§2.1 审计型需求两段式；§2.4 表格移 evidence.md；§4.1.1 recon 一律 Explore + delta 限本工作区；
  §4.2 机器级预算 + PM 模型 + agent 预算行；§4.4 / §4.5 prompt 读文件纪律 + Editor 绑定；§5.1 机械包恒 S 审 + reviewer ≤ dev；
  §5.5 / §6 / §7 去掉 Editor 排队、中断协议先过锁；`references/convergence.md` §7.1 前置改为 Editor 绑定验证。
