# Claude Code Mini Alert

An always-on-top Windows HUD that monitors **every running Claude Code session** in real
time, like *MSI Afterburner for Claude Code*. Each session gets one row with its model,
context usage, cost and activity. Sessions are grouped by repo, with the leader of a
multi-session workflow shown first.

It reads Claude's own session transcripts **passively and read-only**. Nothing in your Claude
setup changes, your transcript content never leaves the machine, and it needs no admin rights.

```text
╭ Mini Monitor  5/6 ─────────────────── ⤢ 📌 ✕ ╮
│ ▾ wec.aria          3/4 live   44%   $38.16  │
│   ★ 22a7e5c0  Opus 5.5   ███░░░░  30% $10.40 ●│
│     d866422f  Opus 5.5   ████░░░  44% $19.16 ●│
│     90753428  Opus 5.5   ██░░░░░  18%  $4.10 ○│
│ ▸ wec.be            1 live     16%    $2.89  │
├──────────────────────────────────────────────┤
│ Today $78.51   Week $286   5h 32%    3% · 2.0G│
╰──────────────────────────────────────────────╯
```

## Features

- **Multi-session dashboard**: one compact row per session (★ leader, short session id,
  model, animated context bar, %, cost, activity dot). Click a repo header to collapse it.
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
- **Notifications**: per-session context alerts (80/90/95 %), a per-session cost threshold,
  and an "all sessions idle" alert. Each alert fires once when its condition is reached.
- **Fluent look**:
  - Windows 11 acrylic backdrop, Fluent icons, Dark / Light / Auto themes (follows Windows).
  - Tabular numbers, resizable width, per-monitor DPI.
  - Always-on-top, system tray.
- **Optional detail widgets** (model, context, cost, CPU, RAM, history, subscription usage)
  can be switched back on in settings.

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
| `Hud`     | `RefreshIntervalMs`, `Theme` (Dark/Light/Auto), `Opacity` (60–100, background only), `Font`, `IdleMinutes`, `ClaudeProjectsRoot`, `HandoverDir`, `MaxSessions`, `ActiveWindowMinutes`, `EndedHideMinutes`, `DayStartHour` (start of the "today" cost bucket) |
| `Widgets` | `Sessions` (main view) plus optional detail widgets: `Model`, `Context`, `Cost`, `Cpu`, `Ram`, `Session`, `History`, `Notification`, `Usage` |
| `Alerts`  | context warn/high/critical %, cost threshold, per-alert enable flags |
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
