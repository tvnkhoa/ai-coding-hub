# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Claude Code Mini Alert** (assembly name `ClaudeMiniAlert`) — an always-on-top Windows HUD
that monitors every running Claude Code session in real time ("MSI Afterburner for Claude Code").
Its main view is a dashboard with one row per session (model, context %, cost, activity),
grouped by repo with the owner's working-day leader/member roles, plus today's spend and
Claude CPU/RAM in the footer. It is **read-only** toward Claude's data and requires **zero changes** to the user's
Claude setup.

`docs/architecture.md` is the authoritative technical blueprint (requirements, layering,
contracts, roadmap); `docs/requirement.md` is the original product spec (in Vietnamese). Read
`docs/architecture.md` before making structural changes — it defines the intended boundaries.

## Commands

Solution file is `AiCodingHub.slnx` (new XML solution format — use a recent .NET 10 SDK / VS).

```bash
dotnet build AiCodingHub.slnx                       # build all four projects
dotnet test                                         # run the xUnit suite
dotnet run --project src/AiCodingHub.App            # launch the HUD (Windows only)

# run a single test / class (xUnit filter on FQN or display name substring)
dotnet test --filter "FullyQualifiedName~TranscriptDeriverTests"
dotnet test --filter "DisplayName~Derives_Model_Context"

# self-contained single-exe release (per docs/architecture.md §24)
dotnet publish src/AiCodingHub.App -c Release -r win-x64
```

All projects target **.NET 10** (`net10.0` for Core, `net10.0-windows` for the rest). The
blueprint originally called for .NET 9; the build machine only has .NET 8 and .NET 10, so the
project targets .NET 10 LTS. This is a **Windows-only WPF app** — the App/Infrastructure/Tests
projects will not build or run on Linux/macOS.

## Architecture

Clean three-layer split with dependencies pointing inward: **App → Infrastructure → Core**.
Core depends on nothing app-specific and has **no WPF reference**, so all parsing/derivation
and alert logic is unit-testable without a UI.

- **`src/AiCodingHub.Core`** (`net10.0`, no WPF/infra deps) — the stable contract surface.
  Immutable domain records (`HudSnapshot` + slices in `Models/`), provider/service interfaces
  (`Abstractions/Providers.cs`), alert rules, `ModelCatalog` pricing/context-window data,
  messenger message types (`Messaging/`), and strongly-typed `Options/`.
- **`src/AiCodingHub.Infrastructure`** (`net10.0-windows`) — the "how" of every data source.
  `Claude/` (transcript tailing + derivation, cost ledger, handover registry), `System/`
  (PerformanceCounter / process metrics / process tree), `History/`, `Notifications/`, and `Hosting/`
  (the background `IHostedService` collectors + `SnapshotAggregator`). DI wiring lives in
  `DependencyInjection/ServiceCollectionExtensions.cs` (`AddInfrastructure`).
- **`src/AiCodingHub.App`** (`net10.0-windows`, WinExe, WPF + WinForms for tray) — composition
  root and presentation only. `App.xaml.cs` builds the Generic Host, layered config, Serilog,
  DI, then shows `Overlay/OverlayWindow`. MVVM under `Widgets/`, `Shell/`, `Theming/`, `Tray/`,
  `Interop/` (DWM backdrop + click-through), `Converters/`.
- **`tests/AiCodingHub.Tests`** — xUnit; references Core + Infrastructure.

### Runtime data flow (the core mechanic)

The primary data source is **passive tailing** of Claude Code session transcripts at
`%USERPROFILE%\.claude\projects\<encoded-cwd>\<session-uuid>.jsonl`. `ClaudeProvider` monitors
**many sessions at once**: every 2 s it lists transcripts (`SessionLocator`, top level only —
`subagents\` excluded) and keeps a `SessionTracker` per relevant file — today's sessions in the
handover registry while their process runs, plus any transcript written within
`Hud:ActiveWindowMinutes`. Each tracker owns a `JsonlTailer` (byte offset, shared read access,
bounded chunks per poll) and a `SessionAccumulator` (incremental fold), so a poll reads only
appended bytes and switching windows costs nothing. A large unseen backlog is folded over
several polls; meanwhile a tail peek (last ~1 MB) supplies the current context %.

Roles come from the owner's working-day hook (read-only): `~/.claude/handover/.sessions.jsonl`
(session → repo, day, claude.exe pid) and `.coordinator-<day>-<repo>` (leader session id); see
`HandoverRegistryReader`. The pid also gives liveness and the focused-window highlight
(`ProcessInspector`). A session missing from the registry is matched to a running `claude.exe`
instead (its command line names the session id, or a bare `claude` runs in the transcript cwd,
read from the PEB); none → Ended and hidden like a registered dead one. Its group is named from
git's common dir like the hook (`WorktreeRepoResolver`), so a linked worktree — or a removed
`<repo>-wt-<x>` on branch `wt/<x>` — joins its main repo as a member. The result is `ClaudeSnapshot.Groups` (repo → sessions, leader first); the
positional fields describe a *primary* session (focused, else most recent) for history.
Context alerts are evaluated per session; a session with an unanswered `AskUserQuestion` /
`ExitPlanMode` carries a `DecisionRequest` ("needs you" strip, bell, Windows notification).
Claude Code writes those tool calls to the transcript only once they are answered, so the live
signal is the optional hook `tools/hooks/hud-decision.mjs`: it leaves
`~/.claude/hud-pending/<session>.json` while the prompt is open (`DecisionMarkerReader`,
`Hud:DecisionMarkerDir`). A marker whose `tool_use_id` the transcript has already answered is ignored.

Spend across all transcripts (today/week/month) comes from `CostLedger` on its own loop
(`CostScanService`, 30 s, per-file offsets), never on the session poll.

Everything is glued by an **in-process event bus** — CommunityToolkit.Mvvm's
`WeakReferenceMessenger` (registered as `IMessenger`). There is **no custom event bus**.

```
Background IHostedServices (Hosting/)          publish            consumers
─────────────────────────────────────         ───────            ─────────
StatusPollingService  → IClaudeProvider ─┐
MetricsCollector      → System/Metrics  ─┤→ SnapshotAggregator → SnapshotUpdatedMessage →
CostScanService       → CostLedger      ─┤    (holds last-known      • widget ViewModels
UsageCollector        → IUsageProvider  ─┘     slice per source)     • HistoryRecorder
NotificationService  subscribes to snapshots → IAlertEvaluator → AlertRaisedMessage + toast
```

`SnapshotAggregator` keeps the last-known slice from each provider and merges them into a
complete `HudSnapshot` on every update, so a single provider's cadence never blanks the HUD.

### Non-obvious conventions (follow these)

- **Providers never throw to the host.** On failure a provider logs a warning and returns
  `Empty`/last-known slice (see `ClaudeProvider.GetAsync`). A failing source degrades only its
  widget — it must not crash the app or blank other widgets.
- **`TranscriptDeriver` / `SessionAccumulator` are pure** — no I/O, no wall-clock. Time is passed
  in as `DateTimeOffset now`. Keep it that way so they stay testable against hand-authored JSONL
  fixtures. **Tests must never read the user's real transcripts** — always use inline fixtures.
- **Count usage once per API response.** Claude Code writes one transcript line per content
  block and repeats the response's `usage` on each; cost and request counts key on
  `message.id` + `requestId` (`TranscriptEvent.ResponseKey`). Tool calls still sum across lines.
- **Prices live in `ModelCatalog` as data**, matched by model-id substring, most specific first
  (`opus-5-5` before `opus-5`). Cache writes are priced by TTL (`cache_creation.ephemeral_1h_*`
  is 2× input, 5-minute writes 1.25×); cache-read multipliers differ per model.
- **`TimeProvider` is injected everywhere** time matters (idle detection, cadence). Tests use
  `FakeTimeProvider`. Don't call `DateTime.Now`/`DateTimeOffset.UtcNow` directly in Core/Infra.
- **ViewModels hold no provider references** — they are pure sinks that receive messages and
  project into display strings. `WidgetHostViewModel` coalesces snapshots (newest wins) and
  applies them with a non-blocking `BeginInvoke`; `WidgetViewModel.Update` is always called on
  the UI thread. The sessions dashboard diffs its collections in place (no rebuild per tick).
- **Domain models are immutable records**; mutate via `with` expressions (see `ClaudeProvider`
  patching context window / cost buckets). Safe to hand across threads without locks.
- Context % = `(input + cache_read + cache_creation)` of the latest assistant turn ÷ the model's
  native window (`ModelCatalog`; 1M on current models). For older 200k models the `[1m]` beta is
  not in the transcript, so `ClaudeProvider` widens to `LargeWindow` when `~/.claude/settings.json`
  selects it for that model family, or when the context already exceeds 200k.
- **Theme colours are tokens** in `Themes/Dark.xaml` / `Light.xaml` (`Hud.Status.*`, `Hud.Card`,
  `Hud.Leader`, …) referenced with `DynamicResource`; no hard-coded colours in templates. The
  window is non-layered with WindowChrome glass so Windows 11 draws the acrylic backdrop
  (`WindowInterop.TryEnableBackdrop`); `Hud:Opacity` applies to the tint only, never the text.

### Adding a widget or provider (the extension points)

- **New widget:** subclass `WidgetViewModel` (implement `Key`, `Order`, `IsEnabled`, `Update`),
  register it in `App/Widgets/WidgetRegistration.cs` (`AddWidgets`), add a `DataTemplate` keyed
  on the VM type in `WidgetTemplates.xaml`, and add its toggle to `WidgetOptions` + the
  `Widgets` section of `appsettings.json`. The host discovers widgets via the injected
  `IEnumerable<WidgetViewModel>` — no shell edit needed.
- **New data source / agent (e.g. Codex, Gemini):** implement the relevant `I*Provider` from
  `Core/Abstractions/Providers.cs` and register it in `AddInfrastructure`. Core, UI, and other
  providers stay unchanged.

## Configuration

Layered via `IOptionsMonitor` (hot-reloadable, no restart): bundled
`src/AiCodingHub.App/appsettings.json` (defaults, copied to output) → per-user overrides at
`%APPDATA%\ClaudeMiniAlert\settings.json` (writable via `UserSettingsWriter`, atomic
temp+move). Logs roll under `%APPDATA%\ClaudeMiniAlert\logs\` (7-day retention). Option classes
(`HudOptions`, `WidgetOptions`, `AlertOptions`, `WindowOptions`, `UsageOptions`) each expose a
`SectionName` bound in `App.xaml.cs`.
