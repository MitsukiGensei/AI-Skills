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
- **checkout ≠ submit**: wrap-up only aggregates the changes into a pending changelist for review; the decision to submit always belongs to the user (a hard veto).

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
