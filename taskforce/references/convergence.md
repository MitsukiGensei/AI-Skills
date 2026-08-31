# 收敛回路细则

SKILL.md §5 的展开。**只在跑到对应环节时读**，主干常驻不带它。

---

## 1. 预扫描清单（dev agent 完工前自跑，SKILL.md §5.0）

grep/Glob 就能查、且**最常漏**的四类。能当场补的当场补（算同轮改动，不单开轮）：

| 检查项 | 怎么查 | 漏了会怎样 |
|---|---|---|
| **改一处同步全引用** | 被改的签名 / 字段 / 枚举，`grep <符号>` 全工程列出所有调用点，逐个确认已更新 | 某个调用方按旧签名传参 → 运行时崩或静默错 |
| **无残留旧模式** | 被替换的旧写法 / 旧字段 / 废弃常量，全工程 grep 应零命中 | 新旧两套语义并存，下一个人不知道该信哪个 |
| **产出点一致** | 同一数据结构的**多个构造点**（`grep` 结构名 / 关键字段），确认都补了新字段 | 漏一个产出点 = 静默 bug，最难查 |
| **依赖真实存在** | 依赖的配表行 / proto 字段 / 资源路径 / AA 地址，**probe 验**（grep 配表、读 proto、Glob 资源），不是读码猜 | 编译过、跑起来才发现取不到 |

> 预扫描是**数量级最便宜的闸**：grep 一遍比一个 subagent 读千行文件省两个数量级 token。
> 它**不替代**独立 review——独立性查的是实现者自己的偏误盲区，预扫描查的是机械漏改。
> 但把机械漏改在昂贵 subagent 之前消化掉，能直接省掉为它们多跑的一整轮 review。

---

## 2. review 视角清单（SKILL.md §5.1）

**S 档：四个视角写进同一份 prompt 给一个 reviewer**，不各开一个 agent。
M/L 档由双引擎交叉审核提供多 agent 视角，这份清单作为其审查要点补充。

| 视角 | prompt 要点（对抗式，不给实现推理） |
|---|---|
| **正确性** | "你没写这段代码，假设它一定有 bug——nil / 空值、边界、生命周期、控制流、时序竞态。" |
| **根因-补丁** | "这是根因修复还是打补丁？检测 nil 守卫、try-catch 吞错、特例分支、补偿调用、强制刷新。" |
| **项目规则** | 按 GC / 生命周期 / MVC 分层 / 命名审**改动行**（有 `code-reviewer` agent 就用它，无则通用 subagent + 工程内 `.claude/rules/*` 当规则源）。 |
| **需求覆盖** | "逐条核对改动是否满足每条验收标准，未满足记 BLOCK。" |
| **design-conformance**（仅 L 档核心包，且是弱 subagent 执行强设计时） | 输入 = **设计文档 + diff**（同样不喂实现者推理）。"实现有没有偏离设计意图——架构约束破了没、分层意图守住没、被设计显式排除的做法有没有偷偷回来？"只判偏离，不重复正确性检查。`fable` 档。 |
| **completeness-critic**（仅 L 档确认轮） | "前几轮漏了什么——未验的验收标准 / 未跑的路径 / 未读的关联文件 / 未 probe 的依赖。" 它空 + 正确性空，该轮才算干净。 |

**根因-补丁视角的开关**：`--verify` 模式、bug 修复类、容错逻辑改动 → **必开**；
纯增量 feature 可不开。

### 视角之外的三条硬约束（每份 reviewer prompt 都要带）

1. **读取上限**：只读 ① 本轮 diff ② diff 命中行的直接上下文与直接调用方
   （要核对某符号的调用方就 `grep` 出来精读命中行，**不是 Read 整文件**）
   ③ 验收标准与规则路径。**禁止**为求"全面"通读未改模块——
   千行文件 × N 个 reviewer × M 轮是本引擎的头号开销。
2. **pre-existing vs 改动漏点**（前者杜绝误报，后者必须抓）：
   - 未改动代码里、**与本次改动无关**的既有瑕疵（缺 nil 守卫、命名不规范、旧风格）
     → 至多 INFO「pre-existing，本次未触及」，**不报 BLOCK**。
   - 但「本次改动**本应覆盖却漏掉**的点」仍是 BLOCK——它不是无关既有问题，
     是**改动本身不完整**：被改签名的某个调用方没跟改、新字段在另一个产出点没透传、
     被收口的旧语义还有残留引用。
   - **判据**：「这个未改的点，是不是**因为本次改动**才变得不对 / 才需要跟着改？」
     是 → BLOCK；否（它本来就这样、与本改动正交）→ 至多 INFO。
3. **terse 输出契约**（放 prompt 末尾，强制）：
   > 只输出 BLOCK/WARN 列表，每条 `[级别] file:line — 一句话问题 — ≤1 行证据`；
   > 无则单行 `NO BLOCKERS`。**不复述 diff、不列逐条验收标准通过表、不写过程叙述**。
   > 末行 `BLOCK: N`。

---

## 3. blocker 记账（写进任务板「收敛轨迹」区）

合并「闸门失败 + 各 reviewer 的 blocker」为单一列表，按 `file+line+主旨` 去重。
每条只记：

```
B1  source=autotest|lint|compile|correctness|rules|requirements|rootcause
    file:line  severity=BLOCK|WARN  why=<≤1 行>
```

修复后追加一行 `B1 fixed: root_cause=<一句> change=<一句>`。
被驳回的伪 blocker 记一行 `B2 DISMISSED: <一句依据>` 即可。

**保持 terse**：任务板是给"压缩后重读"用的机器档，不是报告，不写散文复盘。

---

## 4. 完成报告模板（SKILL.md §6.4）

```markdown
## taskforce 完成报告 — <任务名>

**结论**: CONVERGED / CAPPED / STUCK / HALT    **任务档**: S|M|L
**工作包**: N 个（done N / 未收敛 M）    **agent 用量**: <各档次数>

### 验收标准
- [x] AC1 ...
- [ ] AC3 ...（未达成 → 原因）

### 产出文件
新增：...    修改：...    删除：...

### 收敛轨迹
| 包 | 档 | 轮 | 闸门 | blockers | 修了什么 |
|----|----|----|------|----------|---------|
| WP-01 | S | 1 | all pass | 0 | — |
| WP-02 | M→L | 2 | test:fail→pass | correctness×2 | 根因 X |

### 接缝统计
登记 N 条 / owner 全覆盖 ✓ / 波次扫描 K 次 / 终验新发现接缝 0（非 0 → 接缝类型枚举漏了一类，补进 SKILL.md §4.1.3）

### ⚠️ 待关注（请事后复核）
- 空实现 + TODO 项 / UNVERIFIED 断言 / 视觉副作用
- 已知风险区登记的 WARN
- 升级类结论的未解 blocker + 试过哪些策略

### 交付
- Changelist: {CL号}（**pending，未 submit**）
- 归入文件: 新增 N / 修改 M / 删除 K（含 .meta）
- 下一步: 复核 diff 后由你 submit
```

升级类结论（CAPPED / STUCK / HALT）时，「交付」段换成：
「未收口——代码未收敛，如需暂存未完成改动请手动 checkout」，
并把**卡在哪、试过什么、剩哪些 blocker**写全。

---

## 5. 交叉收敛协议实操（SKILL.md §5.2）

两侧的产物都是 **JSON**，不是散文——归并才能机械执行。schema：
`schemas/review-findings.schema.json`（Round 0）、`schemas/review-rebuttal.schema.json`（Round 1）。
codex 侧由 `codex-fleet.ps1` 的 packet `schemaFile` 强制；Claude 侧把 schema 贴进 prompt 要求同形输出。

### 5.1 Round 0 — 独立审查 prompt 骨架（两侧同一份，只改视角）

```
你在审一份 diff。你没写这段代码，假设它一定有问题。

输入（只准读这三样）：
1. 本轮 diff：<path>.diff
2. diff 命中行的直接上下文与直接调用方（grep 出来精读命中行，不 Read 整文件）
3. 验收标准：<路径或内联>    项目规则：<.claude/rules/... 路径>

视角：<正确性 / 根因-补丁 / 项目规则 / 需求覆盖 —— 按 SKILL.md §5.1 分配>

每条 finding 必须给出「处理建议 + 处理方式」，缺 fix 的条目不算 finding：
- root_cause：引发症状的原因，不是症状出现的位置
- fix.layer：根因归属层 code / config-table / asset / proto / third-party / spec
  （配表错 → 转配表负责人改源表重导；资产错 → 转资产负责人；禁止在下游层补偿上游层的错误）
- fix.how：具体到「改哪个文件的哪里、改成什么」。禁止 nil 守卫 / try-catch 吞错 /
  特例分支 / 补偿调用这类掩盖症状的写法
- fix.risk：这个改法可能破坏什么、改完要复验什么
- pre_existing：与本次改动无关的既有瑕疵置 true（至多 INFO）；
  「因本次改动才变得不对」的置 false（是 BLOCK）

只输出 schema 规定的 JSON。不复述 diff、不写过程叙述、不列验收标准通过表。
```

### 5.2 Round 1 — 质证 prompt 骨架（唯一一轮）

```
这是另一个审查引擎对同一份 diff 的 findings：<对方 JSON>
你的上一轮结论：<你自己的 JSON>

逐条表态，一条都不许略过（未表态按 AGREE 计）：
- AGREE / PARTIAL / REJECT + reason + evidence（≤1 行代码级证据）
- REJECT 必须有 evidence。无证据的 REJECT 直接作废，按 AGREE 计
- disposition：accept / downgrade / drop / needs-probe
  （needs-probe = 读代码定不了，必须在 counter_fix 里写出能一锤定音的那条命令 / grep / 断点）
- fix_agreement：same / different / n/a；different 时在 counter_fix 写你的改法与「为什么它才是根因修复」

不许开新战场：本轮只就对方的 findings 表态。对方漏掉的 BLOCK 写进 summary.missed_by_other，
至多 3 条，其余留到下一包或终验。

只输出 schema 规定的 JSON。
```

### 5.3 归并工作表（PM 填，写进任务板"收敛轨迹"）

```
F1  accepted  (双发命中)                    -> 修复队列 / 派回 <agent>
F2  dropped   (codex REJECT + 证据; cc 无反证) -> DISMISSED: <一句依据>
F3  争议 -> probe#1 verifier(opus) -> accepted: <一行证据>
F4  WARN 争议 -> 已知风险区，不阻塞
F5  fix 冲突 -> 采纳 <哪一侧>：层归属正确 / 改动面更小 / 合既有惯例（三选一写清）
```

**probe 队列每包上限 3 条**，按 `severity × confidence` 排序；溢出的降级登记"已知风险"。
verifier 的结论是**终局**，两侧都不再表态——这是"不各说各话"的最后一道闸。

### 5.4 收敛失败的两种典型形态（看到就停）

| 形态 | signature | 处置 |
|---|---|---|
| **对着轰** | 两侧都在追加新 finding，而不是就已有 finding 表态 | 强制截断：Round 1 之后不再接受新 finding，`missed_by_other` 之外的一律推迟 |
| **同义反复** | 同一条 finding 换措辞反复出现，双方各持"我觉得" | 判为 needs-probe 交 verifier；verifier 也定不了 → WARN 降级登记"已知风险"，不阻塞收口 |

---

## 6. codex 派单开发包 prompt 骨架（SKILL.md §4.7）

```
# 工作包 WP-xx：<一句话目标>

## 范围
<做什么、不做什么>

## 独占文件清单（只许改这些，其它一律禁止创建/修改/删除）
- <路径/模块A/...>
- <路径/模块B/...>

## 接缝（本包拥有 / 消费的行，抄自任务板接缝登记表）
| # | 接缝 | 两端 | owner | 契约 | 机判验证 |

## 验收标准（可机判优先）
- AC1 当<条件>，系统应<行为>

## 现状侦察
<task-dir>/packets/recon.md   ← 先读它，不要从零重探

## 硬约束
- 遵守项目 CLAUDE.md 与 .claude/rules/*（编码规范、目录禁区、文件编码与换行约定）
- 根因优先：禁 nil 守卫 / try-catch 吞错 / 特例分支 / 补偿调用掩盖问题
- 数据缺失不假设：字段/配表行/资源不存在 → 空实现 + TODO 并回报，不编造来源
- 移植类需求不自创绕过：缺依赖继续从源头移植
- 完工前自跑：预扫描（改一处同步全引用 / 无残留旧模式 / 产出点一致 / 依赖真实存在）
  + 廉价闸门（lint / 编译 / 相关测试）+ 自有接缝的机判验证

## 回报契约（≤30 行）
1. 摘要 2. 实际改动文件逐行列出 3. 闸门结果 + log 路径与 mtime 4. 接缝验证结果
5. 未解问题 / 你做过的假设
```

wrapper 会在这份 prompt 顶部自动注入写契约或只读契约（`codex-dev.ps1` 的 ACCESS MODEL），
**但清单仍要列全**：契约只声明边界在哪，清单才定义边界是什么。

收包时按 SKILL.md §4.7 的五条复核表逐项过；`stdout.log` / `stderr.log` 里的 thinking 流
是写权限包唯一的事后审计材料，越界动作会留在那里。
