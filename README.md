# AI-Skills

**English** | [中文](README.zh-CN.md)

A personal collection of Claude Code skills and harness components. Each skill lives in its own directory containing `SKILL.md` (the skill itself) and `README.md` / `README.zh-CN.md` (what it does and how to use it; English is the default, Chinese is the companion). To use one, copy its directory to `~/.claude/skills/` (user level) or `<project root>/.claude/skills/` (project level).

## Skill index

| Skill | Description |
|---|---|
| [taskforce](taskforce/README.md) | Requirement-delivery engine (project-manager mode): the main agent acts only as PM — sizes the task, splits it into work packages, dispatches multi-tier model subagents in parallel (plus a fleet of Codex agents on a separate quota pool), converges quality through objective gates and schema-driven independent review, and collects the changes into a P4 pending changelist for the user to sign off; state lives in an external task board so it survives frequent context compaction |
| [profiler-analysis](profiler-analysis/README.md) | Unity performance-sample analysis: a script preprocesses AI Profiler exports (C#/Lua CPU, Mono/Lua VM GC, high-frequency calls, GPU counters, memory trends, frame spikes, view opening, scene switching); the main agent triages, then dispatches one read-only subagent per issue to dig out the root cause, then independent skeptics to adversarially verify, producing a P0/P1/P2 report with `file:line` fixes. Ships with a generic Unity-side companion (AI Profiler window / exporter / runtime capture / unattended menus / Lua backend abstraction + Miku adapter / pure-Lua instrumentation adapter; works in projects without Lua too — see [unity/](profiler-analysis/unity/README.md)) |

## Contributors

- [MitsukiGensei](https://github.com/MitsukiGensei) — author and maintainer
- [Claude Code](https://github.com/anthropics/claude-code) — co-author (skill design, implementation, review)
- [Codex](https://github.com/openai/codex) — co-author (external development and cross review)

Commits made with AI assistance carry `Co-authored-by` trailers so that contribution is attributed on GitHub.
