# taskforce — Requirement-delivery engine (project-manager mode)

**English** | [中文](README.zh-CN.md)

## What it does

An orchestration skill that takes a requirement from "a one-line description" to "deliverable with quality closed out". The main agent acts only as the **project manager (PM)**: it sizes the task, splits it into work packages, dispatches subagents in parallel, converges quality through objective gates and independent review, and finally collects the changes into a P4 pending changelist for the user to sign off — writing as little code itself as possible.

Core design:

- **Automatic tiering**: the task is sized S / M / L from objective signals — number of files touched, number of layers crossed, contract changes, risk areas. Small requirements drop to a lightweight tier (one dev agent + one single review); only large ones fan out into multiple parallel waves. Tier changes are driven by objective triggers, not gut feeling.
- **Tiered model dispatch**: every Agent call passes an explicit model (haiku for errands / sonnet for mechanical work / opus for regular work / fable for architecture and adversarial final verification). Supports "speculative drafting" — a package with a complete spec is drafted one tier down, with the review step acting as the verifier.
- **Codex as external help**: large work packages with a frozen spec can be handed to Codex CLI (gpt-5.6-sol, max + fast tiers) to run in the background, consuming a separate quota pool; by default a visible terminal window streams the process live.
- **Independent review convergence**: reviewers are always fresh subagents, fed only the diff + acceptance criteria, never the implementer's reasoning. S tier: one review across four lenses; M tier: one round of multi-model cross review; L tier: full multi-round cross review + a completeness-critic confirmation round. Disputed findings are resolved by evidence, not by adding debate rounds.
- **Externalized state**: all state lives in `<workspace root>/.taskforce/<task-slug>/taskboard.md` (the single source of truth), so it survives frequent context compaction — after compaction, re-reading the task board resumes from the checkpoint with no resume parameter needed.
- **Every seam has an owner**: splitting a requirement into work packages inevitably creates interfaces between those packages — "seams". Each seam is written into a seam registry on the task board with exactly one owner, its contract (signature / data shape / semantics), a machine-checkable verification step and a version history. On the first wave nobody is dispatched until the registry is filled in, and placeholder contracts such as "name TBD" or "see recon" are not accepted. Changing a contract means appending a version row and sending the affected agents a one-line pointer to it, not a fresh round of files or discussion.
- **Seams are checked early, not at the end**: the owning package verifies its own seams as part of its gate, and a cheap runner scans the whole registry at every wave boundary. The final cross-package check on large tasks then starts from "all seams green" evidence instead of hunting for gaps from scratch.
- **Don't split what has to be done in order**: when each step's meaning depends on the previous one, the whole chain goes to a single agent end-to-end — only the review intensity scales with size, not the number of agents. Packages too thin to be fully specified are merged into a neighbour; a smaller, fully briefed team beats a busy-looking one.
- **Evidence over self-report**: a gate result only counts when the report includes a log file whose timestamp is later than the start of the round; anything else is re-run by a cheap runner before it is trusted. Out-of-ownership edits are caught by diffing the actual change list against the declared ownership, not by the PM's eye.
- **Physically isolated cross review**: when Claude and Codex review the same diff, each side gets its own copy of the input and writes its verdict outside the task directory; results are moved in only after both have finished. "You are not allowed to read the other side" is replaced with "you cannot reach it".
- **Aware of sibling tasks**: before starting, the PM checks a shared `.taskforce/_active.md` for other taskforce runs that touch the same files, registers itself there, and coordinates with the sibling session before dispatching overlapping work.
- **checkout ≠ submit**: wrap-up only aggregates the changes into a pending changelist for review; the decision to submit always belongs to the user (a hard veto).

## Where the coordination rules come from

Version 1.1.0 (2026-08-27) revised the dispatch, gate and review rules in light of *When Agents Coordinate: Measuring Coordination in Multi-Agent AI Coding* (Destefanis & Aste, UCL, 2026, [arXiv:2608.16801](https://arxiv.org/abs/2608.16801)), which measured 1,902 multi-agent coding runs, together with what we saw across our own taskforce runs. The findings that shaped the rules, in plain terms:

- **Teams break at the seams nobody owns.** On an eight-step chained task, teams of two or four succeeded in 9 of 10 runs; teams of eight succeeded in none. The failure was always a convention (rounding, in that case) that fell between two owners — every team discussed it, none resolved it. Hence the seam registry with exactly one owner per seam.
- **Agents with nothing to do generate most of the traffic.** In eight-agent teams, four agents that had no spec of their own sent 62% of all messages. Hence "fewer, fully briefed agents" and merging thin packages rather than splitting for the sake of parallelism.
- **Sequential work is best reconciled inside one head.** Splitting a step whose meaning depends on the previous step across two agents forces both halves of the convention to be negotiated over messages. One agent doing the chain end-to-end resolves it internally. Hence sequential chains are no longer split.
- **File-based coordination helps only when messages are the bottleneck.** Shared-file policies cut output tokens by roughly 42% in message-heavy eight-agent setups, but on chained tasks they *added* 10–17% cost. taskforce is already file-first, so contract changes get a registry row plus a one-line pointer — no new per-package or per-wave status files.
- **If it can be reached, it will be read.** In sealed replications, 80% of runs opened grading material they were not asked to look at and 66% read other agents' private prompts. Hence cross-review verdicts are written where the other side physically cannot reach them, rather than relying on a "do not read" instruction.
- **One run is a sample of one.** Two runs of the same configuration can differ 15× in message volume. Hence the speculative-drafting log now halves the speculation rate for a domain after a directional rejection instead of stopping after two, and a stuck agent is never retried verbatim without a diagnosis.

## Usage

### Install

Copy this directory as a whole into the Claude Code skills directory (user level or project level, pick one):

```powershell
# User level (applies to all projects)
Copy-Item -Recurse taskforce "$env:USERPROFILE\.claude\skills\taskforce"

# Project level (applies to one project only)
Copy-Item -Recurse taskforce "<project root>\.claude\skills\taskforce"
```

### Triggering

- Explicit: `/taskforce <requirement description or path to a requirement doc>`
- Natural language: mentions of "deliver this requirement", "get this feature done", "put together a dev squad", "split this into parallel tasks", "let codex help", etc. — whenever the intent is to implement a requirement through to a deliverable with quality closed out, the skill triggers automatically.

### Parameters

| Parameter | Default | Description |
|---|---|---|
| `--verify` | off | Convergence-verification mode: the changes already exist (hand-written / produced by another tool); skip sizing and splitting, run review convergence + wrap-up directly on the current uncommitted changes |
| `--no-test` | off | Skip automated tests in the cheap gate (static gates only) |
| `--no-checkout` | off | Skip the P4 wrap-up |

There is no tier parameter — the tier is decided automatically; a single sentence from the user (e.g. "run this as L", "review WP-05 as S") overrides it manually.

### Dependencies

- **Required**: Claude Code's Agent / SendMessage tools (subagent dispatch and reuse).
- **Optional — used if present, degraded gracefully if absent**: a multi-model cross-review skill (M/L tier mutual review), dedicated review agents, automated test and lint gates, a P4 checkout skill, Codex CLI (external help becomes available after `codex login`; when unavailable, all work is carried by Claude agents and the user is told explicitly).
- The Codex helper scripts assume a Windows + PowerShell 5.1 environment.

## File layout

| File | Description |
|---|---|
| `SKILL.md` | Skill body: PM contract, automatic tiering, task board, splitting and dispatch, quality loop, delivery wrap-up, long-haul cadence |
| `references/convergence.md` | Convergence-loop details (pre-scan checklist, review-lens checklist, completion report template); read only when that step is reached |
| `scripts/codex-dev.ps1` | Runs one non-interactive Codex CLI round as a developer (read-only by default; `-AllowWrite` grants write access per package) |
| `scripts/codex-window-runner.ps1` | Launched by codex-dev.ps1 to stream the codex exec process live in a visible terminal window (not invoked manually) |
