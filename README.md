# Claude Code Mini Alert

An always-on-top Windows HUD that monitors **every running Claude Code session** in real
time, like *MSI Afterburner for Claude Code*. Each session gets one row with its model,
context usage, cost and activity. Sessions are grouped by repo, with the leader of a
multi-session workflow shown first.

It reads Claude's own session transcripts **passively and read-only**. Nothing in your Claude
setup changes, your transcript content never leaves the machine, and it needs no admin rights.

```text
╭ ⚡ Mini Monitor  >_ 6          🔔1  ☀  ⤢  📌  ✕ ╮
│ 🔔 wec.be · Leader needs you        4m   Open │
│ ▾ wec.aria  3 live                 58%  $68.89 │
│   ★ Leader              Opus 5.5  ███░░  38% ● │
│     aria-spec094-build  Opus 5.5  ████░  58% ● │
│ ─────────────────────────────────────────────  │
│ ▾ wec.be  2 live  •                24%   $9.62 │
│   ★ Leader              Opus 5.5  ██░░░  24% ◉ │
├────────────────────────────────────────────────┤
│ TODAY $156   WEEK $364   5H LIMIT ⟳ 62%  1% · 2.5G │
╰────────────────────────────────────────────────╯
```

## Features

- **Multi-session dashboard**: one compact row per session: ★ leader, the session's name
  (its launcher-given `agent-name`; "Leader" or the short id otherwise) with the id below,
  model, animated context bar with an 80% tick, %, and an activity dot. Click a repo header to
  collapse it; double-click a row to bring its terminal forward. Repo cards show their total
  cost, and a comet circles a card while any of its sessions is working.
  Compact mode shows group headers only.
- **Leader / member grouping**: optional. If a hook writes a session registry, roles are
  read from it (see [How it works](#how-it-works)). Otherwise sessions are grouped by project.
- **Fast across window switches**: each session is tailed incrementally and keeps its own
  state, so switching between terminals never re-reads a transcript.
- **Accurate cost**:
  - Each API response is counted once.
  - Per-model list prices, including 1-hour cache writes and per-model cache-read rates.
  - Today, week and month totals across all transcripts, refreshed incrementally.
- **Context usage**: tokens versus the model's window (1M on current models; the `[1m]` beta
  is detected for older ones). Colour bands follow your alert thresholds.
- **Notifications**: per-session context alerts (80/90/95 %), "a session needs your decision"
  (in the HUD and as a Windows notification), and an "all sessions idle" alert. Each fires once
  when its condition is reached.
- **Fluent look**:
  - Opaque dark / light palettes with a sun/moon toggle (or Auto, following Windows),
    vector icons, Windows 11 rounded corners.
  - Tabular numbers, resizable width, per-monitor DPI.
  - Always-on-top, system tray.
- **Footer**: today / week spend, the subscription 5-hour limit with a manual refresh (weekly
  limit in its tooltip), and Claude CPU/RAM.
- **Optional detail widgets** (model, context, cost, CPU, RAM, history) can be switched back on
  in settings.

## Requirements

- **Windows 10/11**. This is a WPF app and runs on Windows only. The acrylic backdrop needs
  Windows 11 22H2 or later; older versions get an opaque background.
- **.NET 10 SDK**. Use a recent Visual Studio or `dotnet` CLI that understands the `.slnx`
  solution format.
- Claude Code installed locally, writing session transcripts under
  `%USERPROFILE%\.claude\projects\`.

## Getting started

```bash
dotnet build AiCodingHub.slnx                  # build everything
dotnet run --project src/AiCodingHub.App       # run the HUD
dotnet test                                    # run the tests
```

### Publish a self-contained executable

```bash
dotnet publish src/AiCodingHub.App -p:PublishProfile=win-x64
```

This produces a single self-contained `ClaudeMiniAlert.exe` under
`src/AiCodingHub.App/bin/publish/win-x64/`. There is no installer and no admin rights are
needed.

## Configuration

Settings are layered and hot-reloadable (no restart needed):

1. Bundled defaults in `src/AiCodingHub.App/appsettings.json`.
2. Per-user overrides in `%APPDATA%\ClaudeMiniAlert\settings.json`. The file is created
   automatically and is written back when you move or resize the window.

Logs roll daily under `%APPDATA%\ClaudeMiniAlert\logs\` and are kept for 7 days.

| Section   | Keys |
|-----------|------|
| `Hud`     | `RefreshIntervalMs`, `Theme` (Dark/Light/Auto), `Opacity` (60–100, background only), `Font`, `IdleMinutes`, `ClaudeProjectsRoot`, `HandoverDir`, `MaxSessions`, `ActiveWindowMinutes`, `EndedHideMinutes` (0 = an exited session disappears at once), `DayStartHour` (start of the "today" cost bucket) |
| `Widgets` | `Sessions` (main view) plus optional detail widgets: `Model`, `Context`, `Cost`, `Cpu`, `Ram`, `Session`, `History`, `Notification`, `Usage` |
| `Alerts`  | context warn/high/critical %, per-alert enable flags (incl. `EnableDecision`) |
| `Window`  | `Width`, `Mode` (Full/Compact), `Topmost`, last position |
| `Usage`   | optional subscription 5-hour / weekly usage-limit polling (off by default) |

## How it works

**Transcripts.** Claude Code writes one transcript per session at
`%USERPROFILE%\.claude\projects\<encoded-cwd>\<session-uuid>.jsonl`. The HUD discovers recent
transcripts every 2 s and keeps a tracker per session. Each tracker remembers its byte offset
and folds new lines into a running summary, reading the file with shared access so it is never
locked. When a long session appears for the first time, its last ~1 MB is read first so the
context % shows at once; the rest of the history is folded over the next few polls.

**Session registry (optional).** A SessionStart hook can append
`{session, repo, day, pid, …}` lines to `~/.claude/handover/.sessions.jsonl` and write the
leader's session id to `.coordinator-<day>-<repo>`. If those files exist, the HUD uses them
to label leader and member, to group sessions by repo, and to tell live sessions from ended
ones (by pid).

**Totals and UI.** Spend across all transcripts is computed by a separate incremental ledger
every 30 s. CPU/RAM is sampled every second. All slices are merged and pushed to the UI
through an in-process event bus.

See [`docs/architecture.md`](docs/architecture.md) for the technical blueprint and
[`docs/requirement.md`](docs/requirement.md) for the original product requirement.

## Project layout

```
AiCodingHub.slnx
├── src/
│   ├── AiCodingHub.Core            Domain models, contracts, pricing, alerts (no WPF)
│   ├── AiCodingHub.Infrastructure  Session tracking, cost ledger, registry, system metrics, hosting
│   └── AiCodingHub.App             WPF overlay, MVVM widgets, themes, tray, DI composition root
└── tests/
    └── AiCodingHub.Tests           xUnit unit tests (inline fixtures only)
```

The architecture has three layers, with dependencies pointing inward
(`App → Infrastructure → Core`). Core has no WPF reference, so parsing, cost and alert logic
are unit-tested against hand-authored fixtures. See `CLAUDE.md` for the extension points.

## Tech stack

WPF (.NET 10) · MVVM (CommunityToolkit.Mvvm) · Generic Host + DI
(Microsoft.Extensions.Hosting) · Serilog · System.Text.Json · xUnit.

## License

Copyright © 2026 tvnkhoa.
