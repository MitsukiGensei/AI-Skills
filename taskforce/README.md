# taskforce — Requirement-delivery engine (project-manager mode)

**English** | [中文](README.zh-CN.md)

## What it does

An orchestration skill that takes a requirement from "a one-line description" to "deliverable with quality closed out". The main agent acts only as the **project manager (PM)**: it sizes the task, splits it into work packages, dispatches subagents in parallel, converges quality through objective gates and independent review, and finally collects the changes into a P4 pending changelist for the user to sign off — writing as little code itself as possible.

Core design:

- **Automatic tiering**: the task is sized S / M / L from objective signals — number of files touched, number of layers crossed, contract changes, risk areas. Small requirements drop to a lightweight tier (one dev agent + one single review); only large ones fan out into multiple parallel waves. Tier changes are driven by objective triggers, not gut feeling.
- **Tiered model dispatch**: every Agent call passes an explicit model (haiku for errands / sonnet for mechanical work / opus for regular work / fable for architecture and adversarial final verification). Supports "speculative drafting" — a package with a complete spec is drafted one tier down, with the review step acting as the verifier.
- **A fleet of Codex agents, not one at a time**: work packages with a frozen spec go to Codex CLI, which runs on a separate quota pool. Several of them run at once from a single manifest — each with its own prompt, its own output directory and its own access grant — instead of the old "start one, wait for it, start the next", which serialised the slowest resource in the system. Codex takes development packages as a peer of the `opus` tier, not just review work, and its output goes through exactly the same gates, ownership checks and independent review as a Claude agent's; being the strongest external model buys it no exemption.
- **The prompt is the boundary, not the sandbox**: from Codex CLI 0.150 the read-only sandbox on Windows rejects *every* child process, so a "read-only" reviewer cannot even grep — and still exits successfully with a confident-looking empty report. taskforce therefore bypasses the sandbox and has the wrapper prepend a read-only or write contract to the prompt itself, so a hand-written packet cannot forget it; asking for the old sandbox on a version where it is broken makes the script refuse to run rather than hand back a hollow review.
- **Independent review convergence**: reviewers are always fresh subagents, fed only the diff + acceptance criteria, never the implementer's reasoning. S tier: one review across four lenses; M and L tiers: Claude and Codex review the same diff independently, then converge. Disputed findings are resolved by evidence, not by adding debate rounds.
- **Review output is structured, so merging is mechanical**: both engines answer against a JSON schema. Every finding must carry evidence, a root cause, which layer owns that root cause (code / config table / asset / protocol / third party / spec) and a concrete fix — "I don't like this here" with no proposed fix does not count as a finding. There is then exactly one rebuttal round in which each side must take a position on every one of the other's findings: silence counts as agreement, a rejection without code-level evidence is discarded, and neither side may open a new front. What survives is merged by a fixed table rather than by the PM reading two essays and trying to reconcile them; only genuine deadlocks go to an evidence probe, at most three per package. This is what keeps two strong models from simply talking past each other.
- **Externalized state**: all state lives in `<workspace root>/.taskforce/<task-slug>/taskboard.md` (the single source of truth), so it survives frequent context compaction — after compaction, re-reading the task board resumes from the checkpoint with no resume parameter needed.
- **Every seam has an owner**: splitting a requirement into work packages inevitably creates interfaces between those packages — "seams". Each seam is written into a seam registry on the task board with exactly one owner, its contract (signature / data shape / semantics), a machine-checkable verification step and a version history. On the first wave nobody is dispatched until the registry is filled in, and placeholder contracts such as "name TBD" or "see recon" are not accepted. Changing a contract means appending a version row and sending the affected agents a one-line pointer to it, not a fresh round of files or discussion.
- **Seams are checked early, not at the end**: the owning package verifies its own seams as part of its gate, and a cheap runner scans the whole registry at every wave boundary. The final cross-package check on large tasks then starts from "all seams green" evidence instead of hunting for gaps from scratch.
- **Don't split what has to be done in order**: when each step's meaning depends on the previous one, the whole chain goes to a single agent end-to-end — only the review intensity scales with size, not the number of agents. Packages too thin to be fully specified are merged into a neighbour; a smaller, fully briefed team beats a busy-looking one.
- **Evidence over self-report**: a gate result only counts when the report includes a log file whose timestamp is later than the start of the round; anything else is re-run by a cheap runner before it is trusted. Out-of-ownership edits are caught by diffing the actual change list against the declared ownership, not by the PM's eye.
- **Physically isolated cross review**: when Claude and Codex review the same diff, each side gets its own copy of the input and writes its verdict outside the task directory; results are moved in only after both have finished. "You are not allowed to read the other side" is replaced with "you cannot reach it".
- **Aware of sibling tasks**: before starting, the PM checks a shared `.taskforce/_active.md` for other taskforce runs that touch the same files, registers itself there, and coordinates with the sibling session before dispatching overlapping work.
- **Verification moves forward, not to the end**: a package that touches UI, scenes, assets or runtime registration must pass its own smoke check before it leaves its gate, on the Editor belonging to its own workspace. The wave-level integration pass keeps only cross-package cases, is sharded when it grows past eight of them or half an hour, and re-tests failures rather than restarting from the first case. A reported defect must arrive with a reproduction over the real call path and evidence of the root cause; after a second failed fix of the same defect, dispatch stops and an evidence probe takes over instead of a third blind attempt.
- **One workspace, one project**: the skill detects which of three shapes it is running in — a copy-on-write worktree cloned from a pool (its own P4 client, its own `.taskforce/`, its own Editor), a conventional P4 workspace, or plain git — and routes isolation, early locking and wrap-up accordingly. In the worktree shape every read and write stays inside that workspace: no borrowing another worktree's recon, task board or Editor, and the only remaining coupling with its siblings is the shared depot stream, which is checked with a single `p4 opened -a` before dispatch rather than negotiated across sessions.
- **The PM is a singleton**: a session can be restarted from the same transcript while the original process is still alive, which quietly produces two PMs re-dispatching the same subagents over each other's files. So the PM writes a lock with its own process id at startup and re-checks it after every resume marker: if the original process is still running, this session announces itself as a duplicate and stops rather than negotiating.
- **Budgeted, because the account is shared**: Claude subagents draw on the same quota as the main session, and parallel taskforce runs stack on top of each other. Caps are explicit — at most three opus/fable agents in flight per wave, at most four per machine across all runs — and every dispatched agent carries a turn-and-time budget with instructions to save progress and report what is done rather than push to completion. Long-running agents append each finished unit to disk, so an interrupted run re-dispatches only what never landed.
- **checkout ≠ submit**: wrap-up only aggregates the changes into a pending changelist for review; the decision to submit always belongs to the user (a hard veto).

## Where the coordination rules come from

Version 1.1.0 (2026-08-27) revised the dispatch, gate and review rules in light of *When Agents Coordinate: Measuring Coordination in Multi-Agent AI Coding* (Destefanis & Aste, UCL, 2026, [arXiv:2608.16801](https://arxiv.org/abs/2608.16801)), which measured 1,902 multi-agent coding runs, together with what we saw across our own taskforce runs. The findings that shaped the rules, in plain terms:

- **Teams break at the seams nobody owns.** On an eight-step chained task, teams of two or four succeeded in 9 of 10 runs; teams of eight succeeded in none. The failure was always a convention (rounding, in that case) that fell between two owners — every team discussed it, none resolved it. Hence the seam registry with exactly one owner per seam.
- **Agents with nothing to do generate most of the traffic.** In eight-agent teams, four agents that had no spec of their own sent 62% of all messages. Hence "fewer, fully briefed agents" and merging thin packages rather than splitting for the sake of parallelism.
- **Sequential work is best reconciled inside one head.** Splitting a step whose meaning depends on the previous step across two agents forces both halves of the convention to be negotiated over messages. One agent doing the chain end-to-end resolves it internally. Hence sequential chains are no longer split.
- **File-based coordination helps only when messages are the bottleneck.** Shared-file policies cut output tokens by roughly 42% in message-heavy eight-agent setups, but on chained tasks they *added* 10–17% cost. taskforce is already file-first, so contract changes get a registry row plus a one-line pointer — no new per-package or per-wave status files.
- **If it can be reached, it will be read.** In sealed replications, 80% of runs opened grading material they were not asked to look at and 66% read other agents' private prompts. Hence cross-review verdicts are written where the other side physically cannot reach them, rather than relying on a "do not read" instruction.
- **One run is a sample of one.** Two runs of the same configuration can differ 15× in message volume. Hence the speculative-drafting log now halves the speculation rate for a domain after a directional rejection instead of stopping after two, and a stuck agent is never retried verbatim without a diagnosis.

Version 1.2.0 (2026-08-31) turned those findings into a checklist to run through *before opening any new agent*, and applied it while adding real parallelism: one output directory per Codex packet with review verdicts sealed away until everyone is done (reachable paths get read); a mechanical merge table with the PM ruling once on conflicting fixes instead of chairing a debate (a "coordinator" title produced no measurable leadership); and a parallelism cap plus a "is this package fully specified?" question before adding anyone (sixteen agents sent no more messages than eight).

Versions 1.3.0 through 1.5.0 (2026-09-01/02) came from our own post-mortems rather than the paper: a run where four rounds of Editor play at the very end took 45% of the wall clock, and a day on which three parallel runs hit the account's session limit at once with nothing checked out. What those cost us is written up in `references/evidence.md`, and what changed as a result is the runtime-verification, workspace-shape, PM-lock and budget rules above.

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
| `--codex=N` | 3 | Cap on Codex agents running in parallel this wave (script hard limit 6, shared with the ≤6 per-wave budget for Claude agents) |
| `--no-codex` | off | No external help: Claude agents do all development and review degrades to a single engine (recorded on the task board) |

There is no tier parameter — the tier is decided automatically; a single sentence from the user (e.g. "run this as L", "review WP-05 as S") overrides it manually.

### Dependencies

- **Required**: Claude Code's Agent / SendMessage tools (subagent dispatch and reuse).
- **Optional — used if present, degraded gracefully if absent**: dedicated review agents, automated test and lint gates, a P4 checkout skill, Codex CLI (external help becomes available after `codex login`; when unavailable, all work is carried by Claude agents and the user is told explicitly).
- The Codex helper scripts assume a Windows + PowerShell 5.1 environment. They stay pure ASCII on purpose: Windows PowerShell 5.1 decodes a BOM-less `.ps1` as the ANSI code page, so a non-ASCII string literal inside a script would be mangled at parse time — which is why the prompt contracts live in `contracts/*.md` and are read back with an explicit UTF-8 decode.

## File layout

| File | Description |
|---|---|
| `SKILL.md` | Skill body: PM contract, automatic tiering, task board, splitting and dispatch, quality loop, delivery wrap-up, long-haul cadence |
| `references/convergence.md` | Convergence-loop details (pre-scan checklist, review-lens checklist, cross-convergence prompt skeletons and merge worksheet, Codex dispatch prompt skeleton, runtime-verification prompt skeleton and defect-record format, completion report template); read only when that step is reached |
| `references/codex.md` | Codex fleet appendix: fixed configuration, manifest shape, fleet discipline and measured signatures; read when launching a fleet or debugging one |
| `references/evidence.md` | Where the rules come from: the paper's seven failure modes against this skill's defences, the revision history with measured signatures, and the incident data behind versions 1.3–1.5 |
| `schemas/review-findings.schema.json` | The shape both engines must produce in round 0: findings with evidence, root cause, root-cause layer and a concrete fix |
| `schemas/review-rebuttal.schema.json` | The shape of the single rebuttal round: one position per finding, with evidence and a disposition |
| `contracts/read-only.md` | The read-only contract the wrapper prepends to a Codex prompt when the packet may not write |
| `contracts/write.md` | The write contract for packets that may edit files — only the exclusive file list, nothing else |
| `scripts/codex-fleet.ps1` | Runs several Codex packets in parallel from one manifest, one output directory each; `-Status` polls, `-Collect` moves the results in once everybody has finished |
| `scripts/codex-dev.ps1` | Runs one non-interactive Codex CLI round (access grant per packet: sandboxed read-only, bypass + read-only contract, or write) |
| `scripts/codex-window-runner.ps1` | Launched by codex-dev.ps1 to stream the codex exec process live in a visible terminal window (not invoked manually) |
