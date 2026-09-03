# taskforce 实证与依据（SKILL.md 的附录，只在追溯依据时读）

SKILL.md 主干只保留规则与机制；实测数字、论文出处、修订史与事故数据放在这里，避免每一轮都把它们重读一遍。

---

## 1. UCL 2026 七条失效模式 ↔ 本 skill 防线（原 SKILL.md §2.4 表）

真并行是本引擎的产能来源，也是它最容易自毁的地方。下表左列是 UCL 2026
（arXiv 2608.16801，1902 次评分运行 + 244 次密封复现，模型钉死 sonnet-4-6）实测到的
结构性失效，右列是本 skill 里对应的**机制**——不是提醒，是流程里必须踩到的那一步。
**每开一个新 agent（Claude 或 codex）前，把右列自查一遍。**

| 失效模式（实测） | 数字 | 本 skill 的防线 |
|---|---|---|
| **接缝无主则断** | 8 步链：2/4 人 9/10 成功，8 人 **0/10**；10 次运行**每次都讨论了**舍入约定、**每次都没解决** | 接缝登记表每条恰好一个 owner；M/L 档波次 1 派工前登记表为空 = 不许派工（§4.1.3） |
| **薄规格 / 闲置 agent 是通信噪声源** | 8 人分布式里 4 个没拿到规格的 agent 发了**全队 62%** 的消息（13.7 vs 8.4 条/人） | 宁少勿闲：规格写不满的包并入相邻包；每个 agent 必须持有完整包规格 + 它拥有/消费的接缝行（§4.4.5） |
| **"协调者"头衔不产生领导力** | 冲突拆分成功率：4 人协调者 8/10 vs 扁平 12/20（p=0.42）；8 人扁平持平或更好；密封复现同样为零效应 | PM 的作用力来自**拆解与登记表**，不来自头衔：矛盾靠 oracle 裁定后写进契约列（§4.1.1），不靠"我是 PM"喊话 |
| **文件通道的收益是有条件的** | 分布式任务强制文件：output token −42%、消息 134→26；**链式任务反而 +10~17%** | taskforce 本就是链式 + file-first，**因此不再叠加任何新的文件纪律**（§4.1.3）；只有当任务形状是"知识被切碎给多人"时才加文件通道 |
| **够得着的路径就会被够** | 密封复现：80% 打开诱饵测试文件、66% 翻别人的 prompt、61% 读清单——**prompt 里没让它们这么做** | 双引擎评审物理隔离：codex 编队一包一独立目录，review/design 产物默认落任务目录**之外**，全队收工后才 `-Collect`（§4.6 / §5.1） |
| **加人不等于加产能** | 16 人与 8 人消息量持平（47.0 vs 46.8，斜率 0.00）；链式 16 人平均度只有 0.28 | 每波在跑 agent ≤ 6（含 codex）；并行度由**任务形状**决定而非档位；顺序因果链一个 agent 端到端做完（§2.1 / §4.4.3） |
| **单次运行 = 样本量 1** | 开放式协调格 27 格里 13 格跨会话不可复现（指数 1.76 vs 2.44）；链式格几乎完美复现 | 不从 n=1/2 推政策：投机命中率"降半"而非"连续 2 次即停"（§4.3）；失败不原样重试，先诊断再缩规格或换人（§7） |

---

## 2. 修订史与实测签名（v1.1.0–v1.4.0，原 SKILL.md §参考）

- Destefanis & Aste, *When Agents Coordinate: Measuring Coordination in Multi-Agent AI Coding*,
  UCL, 2026 — https://arxiv.org/abs/2608.16801 （1902 次评分运行 + 244 次密封复现，模型钉死
  sonnet-4-6）。v1.1.0（2026-08-27）依据其四个结构性发现（接缝无主则断 / 闲置 agent 制造 62%
  通信 / 文件通道的条件收益 / 单次运行 = 样本量 1）与本地 16 次 taskforce 运行实证，修订
  §2.1 §3 §4.1 §4.3 §4.4 §4.5 §5.0 §5.1 §5.4 §6 §7。
  v1.2.0（2026-08-31）把这些发现固化成 **§2.4 七条防线**（每开一个 agent 前自查），并在
  真并行落地（§4.6 codex 编队 / §4.7 派单开发 + 复核 / §5.2 交叉收敛协议）时逐条对齐：
  越界翻看 → 编队一包一目录 + sealed 区；协调者头衔无效 → 归并规则机械化、PM 只在修法冲突时
  一次拍板；加人不等于加产能 → 并行上限与"规格写满了吗"前置自查。
- v1.2.1（2026-09-01）：§0 增「交接」入口模式——接收其他 skill（首个是 P4 团队 skill `profiler-analysis` Step 5）
  产出的交接单，定档起点 / recon / 收口约束由交接单预填；方向恒为"团队 skill → user 级 taskforce 带降级链"，
  本 skill 不反向引用调用方。
- v1.3.0（2026-09-01）：复盘同日并行的两个 L 档任务（`task-d` 5h+ / `task-e` 3h+，
  用户评价"很简单的任务跑了很久"）。墙钟大头不在写代码（两边各约 1h）：单 Editor 被两任务串行争用
  （一方纯等 55 分钟）、运行时验证放最后且单 agent 串行 16 项（4 轮 Play、2h20m）、缺陷未钉根因连修 3 次 +
  假缺陷派修再撤回、两会话 47 个 agent 几乎全 opus 同时撞 session limit、跨会话误 `p4 revert`、同源文档
  全量重 recon、一句话需求自动展开成 L 档。修订：§2.1 范围先于档位（MVP）、§3 独占资源登记 + 任务板
  Editor/在跑行、§4.1.1 recon 用 Explore + delta、§4.2 额度预算 + 增量落盘、§4.4 在跑计数、§4.5 P4 归属
  守卫 + Editor 状态、§5.0 冒烟入闸门、§5.3 闭环复审降 sonnet、§5.4 终验矛盾直接 verifier、
  §5.5 运行时验证前移 / 分片 / 缺陷先钉根因、§7 等待与中断协议；`references/convergence.md` §7 缺陷记录格式。
- v1.4.0（2026-09-01）：适配 工作区池工具 / 池化 worktree **P4 多 client 工作区**（ReFS 卷上从预热池 CoW 克隆出的 worktree，
  同机同 stream 多个 P4 client 并存、无 `.git`），同时保持 P4 单 workspace / 仅 git 形态原有行为：§3 工作区形态探测 +
  任务板「工作区形态」行 + `_active.md` 迁到机器级按 server+stream 分文件；§4.1.2 隔离原语按形态分流（多 client →
  独立 CoW worktree；`isolation: "worktree"` 仅限有 git overlay 的单 workspace）；§4.5 早锁（多 client 必做）；
  §4.6 manifest 示例去硬编码路径 + codex stderr 噪声过滤；§5.0 越界检查并入 `p4 diff -f -sa`；§6 多 client 收口三步。
  实测签名（2026-09-01，`<池根>\workspaces\<项目>\<worktree>`）：client root 下 `.p4config` 逐目录路由正确、
  `池化 worktree verify` 通过、`codex exec --skip-git-repo-check -C <root>` 在未信任路径正常执行，不带该 flag 直接拒跑；
  `<池 CLI> worktree current/show --worktree active` 在 池化 worktree 目录返回 `selector_not_found`（派单须显式 `--repo id:`）。
- 实测签名（2026-08-31，codex-cli 0.150.1 / gpt-5.6-sol）：`exec` 下 `effort=ultra` 正常收敛
  （旧注释"ultra 在 exec 下不可用"已作废）；`-s read-only` **拒绝一切子进程**
  （`dir` 亦 `blocked by policy`）却仍 exit 0，只读评审必须走 `prompt-ro`（§4.6）。

---

## 2b. 其他实证签名（从 SKILL.md 正文移出，按日期）

- **2026-08-20 stale 闸门自报**：WP-03 自报"编译 PASS"是 stale 查询，被相邻包实测撞出 FAIL → SKILL.md §5.0「闸门结果不采信自报，要 log 路径 + mtime」。
- **2026-08-20 接缝错配对每个 agent 都不可见**：一个共享 prefab 被某包改动后炸穿了另一模块的相机，"全部前序轮次零感知"，到终验才发现 → §5.4 接缝扫描 + 终验。
- **2026-08-24 codex 抄对侧结论**：codex 把同目录已存在的 Claude 侧结论文件 逐字抄成自己的结论，显式禁读写了仍被违反 → §5.1 双引擎产物物理隔离（"够不着"）。
- **2026-09-01 误 revert 兄弟 CL**：共享 workspace 下误 `p4 revert` 兄弟 CL 的 `WorldMapModule.lua`，对方丢一行 Drequire → 闭环复审报"进大世界必崩" → 两边各返工一轮
  → §4.5 P4 归属守卫（`p4 revert` 只许 `-a -c <本任务 CL>`；`change N ≠ 本任务 CL` 一律不动）。
- **2026-09-01 终验与复审矛盾开辩论**：fable 终验判 `CardStatusCtrl` 控制器不存在 vs r2 复审判存在 → WP-C 反驳 → PM 再亲自查，三段往返本可一次 runtime probe 定案 → §5.4 直接派 verifier。
- **2026-09-01 运行时验证形态**（原 SKILL.md §5.5「问题形态」）：

  **问题形态**（实证 2026-09-01 `task-d`）：全部代码收敛后才派一个 agent 在一次 Play 里
  串行跑 16 项；每发现一个缺陷 = 派修 + 重进 Play 复测一整轮（10–30 分钟）；共 4 轮 Play、2h20m，
  占任务总墙钟 45%。其中 emoji 面板一个缺陷**连改 3 次都不对**（显式 AddSubView → 换 Find 根节点 →
  真根因是 handler 未包 `checkModuleStep`，每次错修烧一轮复测）；驻防面板"首开崩"是验证 agent 自己的
  调用约定错（`func(H, params)` vs `func(params)`）造出的**假缺陷**，派修 → 复测 → 撤回白烧一轮。

---

## 3. 事故数据：2026-09-02 三个 CoW worktree 并行任务

三个 worktree（`<池根>\workspaces\<项目>\taskforce-…-{A,B,C}`）各起一个 `/taskforce` 会话，
需求同形：「拆解策划案 → 逐条验证 → 查漏补缺（→ checkout）」。10:35 开工，12:36 三会话同时撞 session limit，15:11 恢复后又撞。
三个任务全部定 L 档、零收口。数据来自 `~/.claude/projects/<ws>/<session>.jsonl` 与 `subagents/*.jsonl` 的 usage 字段。

### 3.1 用量（PM 全部是 claude-fable-5-1；subagent 为 opus-5 / sonnet-5）

| 会话 | PM 轮次 | PM cache-read | PM cache-write | PM output | PM 上下文峰值 | compact 次数 | subagent 数 | subagent cache-read | subagent output |
|---|---|---|---|---|---|---|---|---|---|
| wt-A `task-a` | 339 | 112.9M | 4.2M | 0.94M | 520K | 0 | 12 | 481M | 1.26M |
| wt-B `task-b` | 314 | 96.2M | 3.0M | 0.78M | 463K | 0 | 23 | 446M | 1.30M |
| wt-C `task-c` | 253 | 82.8M | 1.2M | 0.45M | 517K | 0 | 11 | 433M | 0.83M |
| 合计 | 906 | 292M | 8.4M | 2.17M | — | 0 | 46 | 1.36B | 3.39M |

### 3.2 根因链

1. **PM 进程被复制**（11:20 与 11:27 两次，工作区池工具用同一 transcript 重起 `claude`，原进程未退出）：wt-A 三份、wt-B 两份、wt-C 两份。
   副本看到 harness 的「No completion record was found for N background agents from the previous session」，按 §7 中断协议给
   **同一批 subagent id** 发「续跑」——WP-DATA / WP-EDIT / WP-BAR 各被驱动 2–3 次；wt-B 的 WP-A / WP-B / WP-C 各被派两份，
   多出 5 个重复 recon 与一个「对比两套 WP-B 候选」的 opus reviewer；wt-A 两个新文件被两个 WP-BAR 实例交替覆写。
   11:20–12:00 窗口 PM 轮次翻 2–3 倍（wt-A 150 轮 / 40 分钟）。随后 11:37–11:55 三个会话共发 65 条 `SendMessage` 排查「谁是副本」。
2. **PM 上下文无上限**：起步 107K（72KB SKILL.md + lark-doc skill + CLAUDE.md/rules + skill_listing），单调涨到 460–520K，0 次 compact。
   增量来源：Bash 回显 234–253K 字符 / 会话、任务板整篇 heredoc 重写 25–42 次（49–68K 字符）、Agent prompt 27–59K 字符、
   SendMessage 8–31K 字符、wt-C PM 直接 `Read` 94K 字符源码。
3. **recon 用 general-purpose sonnet**（11 个，每个 100–229 轮，`cat` 整文件），≈ 370M cache-read，占 subagent 总量 27%。
4. **dev agent 无预算**：WP-BAR 646 轮（含副本驱动）/ 183M cache-read；WP-A 副本 278 轮 / 69M；WP-02a 284 轮 / 65M。
5. **review 叠加**：wt-A 3 个 dev 包 → 6 个 Claude reviewer + 4 个 codex review 包；「还原两个 Lua 文件」被审出 15 条 BLOCK。
6. **单 Editor 排队假设过期**：三个 worktree 各有 `Library/`，Editor bridge 的 registry.json 同时登记三个 Editor（8090 / 8091 / 8092），
   但任务板仍写「Editor need-play，排队于 task-a 之后 / 候选 worktree-unity 或 <另一个工作区路径>」。
7. **跨工作区泄漏**：wt-A PM 引用另一个工作区的路径 123 处（搬上一轮 recon 当 prior、合并旧 `_active.md`）；wt-C PM 读别的项目目录的 memory。
8. **Editor 归属漂移**（wt-C，20:50–21:40）：本工程 Editor 8092 在 `asset_refresh` 后主线程卡死 → 心跳停 → 兄弟 Editor 每 5 次心跳的 stale 清理（registry 的 last_active>120s）把 8092 从 Editor bridge 的 registry.json 删掉 → `editor-cli` 的端口解析在路径匹配落空后**硬回退 8090**、runtime 端口在本工程未进 Play 时**硬回退 18091** → `entry_flow`/`lua`/`poll`/`verify-project` 全部跑在 wt-B 工程上，直到 `verify-project` 报 `WRONG_PROJECT: …wt-B…` 才发现；agent 把会话开头一次 `verify-project` 的结论当成了常量。修法：`editor-cli` 去掉两处回退、每进程首次 editor 调用按 `/health.instanceId` 校验归属；规则落到项目的 Unity Editor 规范（多 Editor 实例归属一节） + 自动化测试 / Editor 自动化 skill + 本 skill §3 Editor 绑定 ④。

### 3.3 对应修订

见 SKILL.md §参考 v1.5.0。

---

## 4. SKILL.md v1.4.0 原文备份（§5.1 / §5.2，v1.5.0 精简前）

### 5.1 review 分级路由（全流程最贵的环节，按**包档**分流）

先生成本轮 diff 工件（`p4 diff -du` / `git diff`）落一个 `.diff` 文件，
**所有 reviewer 的输入 = 这份 diff + 验收标准 + 规则路径**，不让每个 reviewer 各自 Read 整模块。

**双引擎独立性隔离（一票否决）**：两侧 reviewer 的**输入与产物目录必须隔离**——外援（codex）
的输入单独复制一份（如 `codex/input.diff`）；两侧产物**先写到任务目录外**（scratchpad 各自
独立子目录），双方都完成后再搬进 `xreview/`——"禁读"改为"够不着"：先落盘的一侧结论对后跑
的一侧是污染源，且显式禁读在那次事故里已经写了仍被违反（实证 2026-08-24：codex 把同目录
已存在的 Claude 侧结论文件 逐字抄成自己的结论；UCL 2026 sealed 复现：无 prompt 要求下 80% 的运行去翻
评分诱饵、66% 翻其他 agent 的私有 prompt——路径够得着就会去够）。Claude subagent 可保留
prompt 级禁读作第二道，codex 一律物理隔离。
外援 wrapper 一律写明**反造假契约**：produce or fail——外援失败必须如实报失败，严禁 wrapper
代写审查内容。PM 收外援结论先验真三项：输出文件 mtime 晚于启动时刻、有 stdout/stderr 日志链、
内容与对侧首行不同；任一不过即作废重跑。
**第四项验真（codex ≥ 0.150 专属）**：确认该包不是 `sandbox-ro`——只读沙箱下 codex 跑不了任何
子进程，会交出一份 exit 0 的空报告（§4.6）。编队默认 `prompt-ro` 已避开，手工起的单包要自查。

| 包档 | review 强度 |
|---|---|
| **S**（含琐碎改动：配置、改名、几行胶水） | **1 个** `code-reviewer` 或 `sonnet` reviewer 单审，**四个视角写进同一份 prompt**（正确性 / 根因-补丁 / 项目规则 / 需求覆盖），不各开一个 agent |
| **M** | **双引擎并行独立审**：Claude reviewer subagent（`opus`）+ codex 编队 `role=review` 包各出一份**结构化 findings**（`schemas/review-findings.schema.json`），再走 §5.2 的一轮质证收敛；相邻小包攒到同波结束**合并成一次**，不逐包各跑一轮 |
| **L**（核心/高风险） | M 的双引擎，每侧再加一个**不同切入角**（一侧正确性 / 根因，另一侧需求覆盖 / 项目规则），仍是各自独立；**确认轮加 1 个 completeness-critic 单镜头**（"前几轮漏了什么"），不重开全套 |

**包档按内容定，不继承任务档**：从 depot 历史 / 参考工程（w3、sp）**搬运还原**的文件、配表 / 图标搬运、改名、文档同步
= 机械包 → 恒按 **S 审**（1 个 sonnet reviewer 四视角），哪怕任务是 L 档；只有本任务新写的业务逻辑才吃 M/L 双引擎。
**每波 reviewer 数 ≤ 该波 dev agent 数**，同波相邻小包合并成一次 review。实证 2026-09-02 wt-A：3 个 dev 包对应
6 个 Claude reviewer + 4 个 codex review 包，"还原两个 Lua 文件"的包被四路审出 15 条 BLOCK，再进多轮派修。

**为什么不再默认外包给通用的交叉审核 skill**：它产出的是自然语言意见，不带「处理建议 + 处理方式 +
根因归属层」，也不带机器可归并的表态字段，收敛只能靠 PM 手工读两份散文对齐——正是"各说各话"
的来源。用本 skill 自己的两份 schema，收敛规则才能机械执行（§5.2）。
用户显式要求时仍可改用外部交叉审核 skill，但要知道 §5.2 的归并规则对它不成立。

**设计意图保真**（弱 subagent 执行强设计时的专项缺口）：独立 reviewer 只拿到
diff + 验收标准，**不知道设计理由**——"能跑、符合字面标准、但违背设计初衷"会静默通过。
两级解法，优先第一级：

1. **把设计意图编码进验收标准与接口契约**。凡能写成"当 X，系统应 Y"的一律写进去，
   「需求覆盖」视角天然就在验它，零额外成本，且顺带提高投机命中率（§4.3）。
2. 编不动的（架构约束、分层意图、"为什么不能那样写"）→ 加一个 **design-conformance
   reviewer**：`fable` 档、**独立 subagent**、输入 = 设计文档 + diff（**不喂实现者推理**），
   单一职责是"实现有没有偏离设计意图"。**只在 L 档核心包开**；它是独立的第 N 个
   reviewer，**不是 PM 本人**——PM 自己去验自己裁决的设计就是 §1 禁的锚定偏误。

**reviewer 硬约束**（写进 prompt）：
- 只读 ① 本轮 diff ② diff 命中行的直接上下文与直接调用方（`grep` 出来精读命中行）
  ③ 验收标准与规则路径。**禁止**为求"全面"通读未改模块。
- **区分 pre-existing 瑕疵与改动漏点**：与本次改动无关的既有瑕疵至多 INFO，不报 BLOCK；
  但「本次改动本应覆盖却漏掉的点」（被改签名的调用方没跟改、新字段在另一产出点没透传、
  旧语义还有残留引用）**是 BLOCK**——它不是无关既有问题，是改动本身不完整。
- **terse 输出契约**：只输出 `[级别] file:line — 一句话问题 — ≤1 行证据`，
  无则单行 `NO BLOCKERS`，末行 `BLOCK: N`。不复述 diff、不列逐条验收标准通过表、不写过程叙述。

各视角的 prompt 要点见 [references/convergence.md](references/convergence.md)。

**通用交叉审核 skill 跑 1 轮 的产出是证据分层，不是收敛判定**：互判一轮下提出方听不到反驳，
"双方一致否决"这个出口不存在——accepted（双发命中 / AGREE）可信，被单侧 REJECT 的和
交叉轮新增的 finding 都停在争议态，走下面的实证分流。

### 5.2 交叉收敛协议（快速收敛，严禁各说各话）

双引擎的价值在于**两种偏误不重叠**，不在于开辩论会。所以收敛是**机械归并 + 至多一轮质证 +
一次实证裁决**，全程有硬时限，任何一步都不允许退化成"我觉得 / 它觉得"。

**Round 0 — 独立出结论**（两侧互不可见，产物物理隔离，§5.1）。
两侧都必须按 `schemas/review-findings.schema.json` 输出，每条 finding 强制带：
`severity / file:line / claim / evidence（≤1 行代码级证据）/ root_cause / pre_existing /
confidence / fix{layer, files, how, risk}`。
**没有 `fix` 的 finding 不算 finding**——审核的产物是"处理建议 + 处理方式"，不是"这里我不喜欢"。
`fix.layer` 必须给出**根因归属层**（code / config-table / asset / proto / third-party / spec），
归属层不是"谁改起来快"（遵循所在项目的修复归属层级规范）。

**Round 1 — 唯一一轮质证**（互喂对方的 findings，按 `schemas/review-rebuttal.schema.json` 回）。
逐条表态 `AGREE / PARTIAL / REJECT` + `reason` + `evidence` + `disposition` +
`fix_agreement`（same / different / n/a）+ `counter_fix`。三条硬约束：
- **必须逐条表态**：对方的每条都要有一行。**未表态视同 AGREE**——沉默不是立场，是拖延。
- **不许开新战场**：本轮只准就对方的 findings 表态；漏掉的 BLOCK 走 `missed_by_other` 字段，
  **至多 3 条**，其余留到下一包/终验。
- **不许无证据的 REJECT**：`REJECT` 而 `evidence` 为空 → 该表态直接作废（按 AGREE 计），
  哪一侧都一样。

**归并规则（机械执行，PM 不参与辩论）**：

| 两侧状态 | 结论 | 动作 |
|---|---|---|
| 双方都提出 / 一方提出另一方 AGREE | **accepted** | 进修复队列 |
| 一方 REJECT 且带代码级证据，另一方无反证 | **dropped** | 任务板记一行 `DISMISSED + 依据` |
| 双方各持证据对立，或任一方 `needs-probe` | **争议** | 派 verifier 实证，见下 |
| PARTIAL（严重度或根因不一致） | 按**较低严重度 + 较深根因**收编 | 进修复队列，根因以能被证据支撑的那个为准 |
| 仅 WARN / INFO 级争议 | **不进 probe** | 记任务板"已知风险"，不阻塞收敛 |

**实证裁决（终局，不再往返）**：BLOCK 级争议派一个 `opus` verifier 对着代码取证——读调用链 /
写最小复现 / 跑测试 / runtime probe，产出 `accepted | dropped` + 一行证据。
**verifier 的结论是终局**，两侧都不再表态。**每包 probe 队列上限 3 条**（按 severity ×
confidence 排序），超出的降级登记"已知风险"——probe 是最贵的一步，不许排长队。

**修法冲突（`fix_agreement=different`）由 PM 一次拍板**，按序判据：
① 根因归属层正确（跨层补偿的方案直接淘汰）→ ② 改动面最小 → ③ 与既有代码惯例一致。
裁定写进任务板一行（选了谁 / 依据），**不再征求两侧意见**。

**时限与降级**（防止等外援等成空转）：codex 侧超时或 `state=failed` → 用单侧结论继续，
任务板记 `XREVIEW-DEGRADED: codex <原因>`，不空等、也不悄悄当成"没问题"。

**定性派修（拒绝即升档）**。PM 把 accepted 的 finding 分两类：
- **细节问题**（边界漏判、命名、局部逻辑、性能微调）→ `SendMessage` 派回**原开发 agent** 修
  （带上 finding 原文与 `fix.how`），不换人——它最懂自己的代码。
  原开发者是 codex 时改为**重投带 finding 的修复包**（§4.7 末段）。
- **方向性 / 架构性错误**（方案本身不对、契约理解错、结构要推倒）→ **不进修复循环**，
  按分级表原档（或升一档）**换 agent 重写该包**，旧草稿作废但把 finding 附给新 agent 当反面参考。
  让小模型对着方向错误修 3 轮是最贵的空转方式。定性拿不准时按方向性处理——
  升档重写的上限成本可控，空转循环的不可控。投机降档的包被方向性拒绝 → 记入"投机记录"，
  并**同时触发包档升档**（§2.3）。

修复必须**根因优先**：每个 blocker 追到根因再改，禁 nil 守卫 / try-catch 吞错 / 特例分支掩盖。
测试类 blocker 按结构化调试方法论（假设→埋点→复现→分析→修）；**临时埋点写文件不写 console**
（按项目的埋点规范落文件日志，带 `[DEBUG-BEGIN]/[DEBUG-END]` 块标记），
**修复确认后清理**（用项目的埋点清理工具 + grep 验无残留）。

prompt 模板与逐条判据见 [references/convergence.md](references/convergence.md) §5。
