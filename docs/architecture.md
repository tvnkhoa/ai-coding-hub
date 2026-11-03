# Claude Mini Alert Screen — Software Architecture Document (Phase 1)

> **Role of this document.** This is the technical blueprint for a native Windows HUD that
> monitors Claude Code in real time (an "MSI Afterburner for Claude Code"). It contains **no
> source code** — it defines requirements, architecture, boundaries, contracts, and a
> phased roadmap detailed enough that Claude Code can implement each component independently
> in later prompts with minimal redesign. See `docs/requirement.md` for the original product
> requirement this blueprint realizes.

> **Update 2026-11-03 — multi-session dashboard.** The shipped HUD now monitors every running
> session instead of the single newest one, groups them by repo with the owner's working-day
> leader/member roles (read from `~/.claude/handover`), and drops the Git and Task widgets in
> favour of an overview (one row per session: model, context %, cost, activity). Cost is counted
> once per API response with current per-model prices (incl. 1-hour cache writes), and the
> today/week/month scan runs on its own incremental loop. The sections below still describe the
> layering and contracts; where they mention Git, the Task widget or "the newest session",
> `CLAUDE.md` § Runtime data flow is the current description.

---

## 0. Design North Star & Confirmed Decisions

- **Platform/UI:** .NET 10, **WPF**, **MVVM** (CommunityToolkit.Mvvm), Generic Host
  (Microsoft.Extensions.Hosting) for DI + background services. *(Blueprint originally
  specified .NET 9; the build machine has no .NET 9 SDK — only .NET 8 and .NET 10 — so we
  target the .NET 10 LTS, a superset of net9 with all required APIs.)*
- **Primary data source (feasibility-confirmed):** passively tail Claude Code session
  transcripts at `%USERPROFILE%\.claude\projects\<encoded-cwd>\<session-uuid>.jsonl`.
  Appended in real time; each assistant event carries `message.model`, a `usage` block
  (`input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`),
  plus per-event `cwd`, `gitBranch`, `version`, `timestamp`. This replaces the requirement's
  unreliable "Claude CLI stdout parsing" idea and needs **zero changes** to the user's Claude
  setup.
- **Optional richer signals (deferred, seam only):** Named-Pipe channel fed by Claude Code
  hooks/statusline for precise task-state transitions + push notifications.
- **Guardrails:** simple but scalable; composition over inheritance; interfaces only where
  they buy real extensibility; async everywhere; no static mutable state; every component
  independently testable and implementable.

---

## 1. Functional Requirements

**FR-1 Session Monitor** — show current Claude model, current project/workspace, Claude
version, Git branch, session duration.
**FR-2 Context Usage** — current tokens, max context window, remaining, percentage, colored
progress bar (green/yellow/orange/red per thresholds).
**FR-3 Cost Monitor** — session cost, plus today / week / month aggregates.
**FR-4 Request Statistics** — total requests, prompt count, tool calls, avg response time,
longest response, avg tokens.
**FR-5 Performance** — total CPU/RAM, Claude-process CPU/RAM; GPU optional.
**FR-6 Git Integration** — repository name, branch, modified/untracked count, uncommitted
count, ahead/behind.
**FR-7 File Activity** — current file, recently edited, total changed, current folder.
**FR-8 Task Monitor** — derived state: Generating / Thinking / Calling Tool / Searching /
Reading / Writing / Idle, with status color (🟢 Working / 🟡 Waiting / 🔵 Thinking / 🔴 Error).
**FR-9 Notifications** — alerts for context ≥80/90/95 %, cost exceeded, Claude idle, task
finished, tool failed, network lost.
**FR-10 History** — record context/cost/CPU/RAM/requests over time for charts.
**FR-11 Widget System** — each metric group is an independent, toggleable widget.
**FR-12 Modes** — Full (300×420), Compact (300×150), resizable, draggable, optional
click-through, system tray.
**FR-13 Settings** — refresh interval, theme (Dark/Light/Auto), opacity (60–100 %), font,
widget visibility, alert thresholds, window position, hotkeys.

---

## 2. Non-Functional Requirements

| Attribute | Target |
|---|---|
| Memory | < 100 MB working set |
| CPU (idle) | ≈ 0 % (event/timer-driven, no busy loops) |
| Startup | < 2 s to first render |
| Refresh | 500 ms default (250 ms / 500 ms / 1 s / 2 s configurable) |
| Reliability | Read-only w.r.t. Claude data; must not lock transcript files (`FileShare.ReadWrite`) |
| Resilience | A failing provider degrades only its widget, never crashes the app |
| Privacy | Local-only; no network egress of transcript content |
| Testability | Core + Infrastructure have no WPF dependency; deterministic time via `TimeProvider` |
| Extensibility | New provider/widget = new class + one DI registration |
| Footprint | Single self-contained executable, no admin rights |

---

## 3. Technology Justification

- **WPF (not WinUI 3):** simplest path to a borderless always-on-top overlay with per-pixel
  alpha and `WS_EX_TRANSPARENT` click-through; mature, single-exe deploy. Win11 Mica/Acrylic
  via `DwmSetWindowAttribute` interop (+ optional **Wpf.Ui** for Fluent controls/backdrop).
- **Generic Host + DI:** uniform lifetime management for `IHostedService` background
  collectors; `IOptionsMonitor` gives hot-reloadable settings.
- **CommunityToolkit.Mvvm:** source-generated `ObservableProperty`/`RelayCommand` and
  `WeakReferenceMessenger` — a built-in in-process event bus, so **no custom event bus**.
- **Serilog:** structured file logging with rolling files.
- **LiveCharts2:** GPU-friendly WPF charts for the History widget.
- **LibGit2Sharp:** in-process Git status without shelling out to `git.exe`.
- **System.Text.Json:** fast, allocation-light JSONL parsing (`Utf8JsonReader`) + settings.
- **xUnit:** de-facto .NET test runner; pairs with `Microsoft.Extensions.TimeProvider` fakes.

---

## 4. Solution Architecture

Clean-ish layering (dependencies point inward). Three runtime projects + tests:

```
AiCodingHub.sln
├── src/
│   ├── AiCodingHub.Core            (net10.0; NO WPF, NO infra deps)
│   │     Domain models, provider/service abstractions, alert rules, pricing contracts.
│   ├── AiCodingHub.Infrastructure  (net10.0-windows)
│   │     Provider + background-service implementations: Claude JSONL, Git, System, History.
│   └── AiCodingHub.App             (net10.0-windows, WinExe, UseWPF)
│         Generic Host bootstrap, DI wiring, Overlay window, ViewModels, Widgets, Tray.
└── tests/
      └── AiCodingHub.Tests         (xUnit; references Core + Infrastructure)
```

- **Core** = the stable contract surface everything else depends on. Because it has no WPF
  reference, it is 100 % unit-testable and reusable for a future cross-platform / CLI head.
- **Infrastructure** = "how" the data is obtained. Swappable per OS or per agent (Claude →
  Codex/Gemini later).
- **App** = composition root + presentation only.

Dependency direction: `App → Infrastructure → Core`, and `App → Core`. Core depends on
nothing app-specific.

---

## 5. Project Folder Structure

```
AiCodingHub.Core/
├── Models/            HudSnapshot, SessionInfo, ContextInfo, CostInfo, TaskStatus,
│                      RequestStats, PerfInfo, GitInfo, FileActivity, HistorySample
├── Abstractions/      IClaudeProvider, IGitProvider, ISystemProvider, IMetricsProvider,
│                      INotificationProvider, IHistoryStore, ISnapshotAggregator
├── Alerts/            AlertRule, AlertKind, AlertThresholds, IAlertEvaluator
├── Pricing/           ModelCatalog (context windows + $/token), IPricingCatalog
├── Messaging/         SnapshotUpdatedMessage, AlertRaisedMessage, TaskStateChangedMessage
└── Options/           HudOptions, WidgetOptions, AlertOptions, WindowOptions

AiCodingHub.Infrastructure/
├── Claude/            ClaudeProvider, SessionLocator, JsonlTailer, TranscriptParser,
│                      TokenCostCalculator
├── Git/               GitProvider (LibGit2Sharp)
├── System/            SystemProvider, MetricsProvider (PerformanceCounter / Process)
├── History/           InMemoryRingHistoryStore (+ optional JsonHistoryStore)
├── Notifications/     ToastNotificationProvider (Windows), AlertEvaluator
└── Hosting/           StatusPollingService, GitWatcherService, MetricsCollector,
                       NotificationService, HistoryRecorder   (all IHostedService)

AiCodingHub.App/
├── App.xaml(.cs)      Host builder, DI registration, Serilog, lifetime
├── Overlay/           OverlayWindow, WindowInterop (DWM/click-through), DragBehavior
├── Shell/             ShellViewModel (window state, mode, settings commands)
├── Widgets/           <Widget>View.xaml + <Widget>ViewModel  (Context, Cost, Model,
│                      Git, Cpu, Ram, Session, Task, History, Notification)
├── Tray/              TrayIconService, TrayMenu
├── Converters/        ContextColorConverter, PercentToWidthConverter, BytesToMbConverter
├── Themes/            Dark.xaml, Light.xaml, Generic resources, fonts
└── appsettings.json   default configuration (bundled)
```

Organization is **feature-based inside each layer** (a widget's View + VM live together;
each provider gets its own folder).

---

## 6. MVVM Design

- **View** (XAML UserControl per widget) — pure declarative bindings, no code-behind logic
  beyond interop that can't be expressed in XAML (e.g. window drag).
- **ViewModel** — `ObservableObject` with `[ObservableProperty]` fields; receives domain
  snapshots via `WeakReferenceMessenger` and projects them into display strings/values.
  ViewModels hold **no** provider references — they are pure sinks + command handlers.
- **Model** — immutable Core records (`HudSnapshot` et al.); never bound directly, always
  projected by a VM (keeps formatting/culture out of the domain).
- **ShellViewModel** — owns window-level state (mode, opacity, topmost, click-through) and
  commands (ToggleCompact, OpenSettings, Exit); binds to `OverlayWindow`.
- **Composition:** `MainViewModel` exposes child widget VMs as properties; the overlay hosts
  an `ItemsControl`/explicit grid of widget views bound to those VMs. Widget visibility binds
  to `WidgetOptions`.

Threading rule: messenger delivery to VMs is marshalled to the UI `Dispatcher` (see §18).

---

## 7. Class Responsibilities (core types)

| Type | Responsibility |
|---|---|
| `HudSnapshot` (record) | Immutable point-in-time composite of all sub-infos + `Timestamp`. |
| `SessionInfo` | Model, workspace path, project name, Claude version, start time, duration. |
| `ContextInfo` | CurrentTokens, MaxTokens, Remaining, Percentage. |
| `CostInfo` | SessionUsd, TodayUsd, WeekUsd, MonthUsd. |
| `RequestStats` | Requests, Prompts, ToolCalls, AvgLatency, LongestLatency, AvgTokens. |
| `TaskStatus` | `TaskState` enum + status color + last-activity time. |
| `PerfInfo` | TotalCpu%, TotalRamMb, ClaudeCpu%, ClaudeRamMb, optional Gpu%. |
| `GitInfo` | Repo, Branch, Modified, Untracked, Uncommitted, Ahead, Behind, IsDirty. |
| `SessionLocator` | Finds the active session `.jsonl` (newest-modified, excludes `subagents\`). |
| `JsonlTailer` | Incremental append-only reader (offset bookkeeping, shared read). |
| `TranscriptParser` | Turns raw JSONL events into typed turns/usages via `Utf8JsonReader`. |
| `TokenCostCalculator` | Maps usage × `IPricingCatalog` → per-turn and aggregate cost. |
| `ModelCatalog` | Model id → context window + input/output/cache prices (data-driven). |
| `AlertEvaluator` | Applies `AlertRule`s to successive snapshots, emits de-duplicated alerts. |
| `SnapshotAggregator` | Merges provider slices into a single `HudSnapshot`. |

Each entry in §8–§11 lists Purpose / Responsibilities / Dependencies / Public interface /
Extension points / Future.

---

## 8. Service Layer Design (Background `IHostedService`s)

All are hosted services started by the Generic Host; each uses a `PeriodicTimer` or
`FileSystemWatcher` — **no `Thread.Sleep`, no busy-wait**. Each publishes results through the
messenger; none touch UI controls directly.

- **StatusPollingService** — *Purpose:* drive the primary refresh cadence. *Responsibilities:*
  every `RefreshInterval`, call `IClaudeProvider` (+ `ISnapshotAggregator`) to build a
  `HudSnapshot`, publish `SnapshotUpdatedMessage`. *Deps:* `IClaudeProvider`,
  `ISnapshotAggregator`, `IMessenger`, `IOptionsMonitor<HudOptions>`. *Extension:* cadence
  reacts to options hot-reload. *Future:* multi-session polling.
- **GitWatcherService** — *Purpose:* cheap, event-driven Git state. *Responsibilities:*
  `FileSystemWatcher` on `.git/` of the active workspace; debounce → `IGitProvider` → publish.
  *Deps:* `IGitProvider`, `IMessenger`. *Future:* multi-repo.
- **MetricsCollector** — *Purpose:* CPU/RAM sampling. *Responsibilities:* periodic
  `ISystemProvider`/`IMetricsProvider` sample → publish. *Deps:* those providers, messenger.
  *Future:* GPU counters.
- **NotificationService** — *Purpose:* turn threshold breaches into user alerts.
  *Responsibilities:* subscribe to snapshots, run `IAlertEvaluator`, raise
  `AlertRaisedMessage` + OS toast via `INotificationProvider`. *Deps:* evaluator, provider.
- **HistoryRecorder** — *Purpose:* time series for charts. *Responsibilities:* subscribe to
  snapshots, downsample to `HistorySample`, append to `IHistoryStore` (bounded ring buffer).
  *Deps:* `IHistoryStore`, messenger. *Future:* persist to disk / SQLite.

---

## 9. Provider Design

Providers are the **only** components that know a data source's shape. All async, all return
Core models, all resilient (catch → return last-known/empty slice + log, never throw to host).

- **`IClaudeProvider`** → `Task<ClaudeSnapshot> GetAsync(CancellationToken)`.
  *Impl:* `SessionLocator` + `JsonlTailer` + `TranscriptParser` + `TokenCostCalculator`.
  Derives model, context tokens/%, session/today/week/month cost, request/tool stats,
  duration, task state, workspace, branch. *Extension point:* `ISessionSource` abstraction so
  a future Named-Pipe/hook source can supplement file tailing. *Future:* Codex/Gemini
  providers implementing the same contract.
- **`IGitProvider`** → repo/branch/dirty/ahead-behind via LibGit2Sharp for a given path.
- **`ISystemProvider`** → total CPU/RAM (PerformanceCounter). **`IMetricsProvider`** →
  process-scoped Claude CPU/RAM (locate `node`/`claude` process). *Future:* GPU.
- **`INotificationProvider`** → OS toast abstraction (Windows App SDK / `H.NotifyIcon`).
- **`IHistoryStore`** → append/query samples (in-memory ring default).

**Contract rules:** stateless-preferred; time via injected `TimeProvider`; no UI types;
one source per provider (keeps them independently testable and AI-implementable).

---

## 10. Widget System Design

- **Independence:** each widget = `{XxxViewModel : ObservableObject}` + `XxxView`. The VM
  subscribes to exactly the messages it needs and exposes only display properties.
- **Registration:** widgets are DI-registered and enumerated by the shell; a `WidgetOptions`
  map (`Context=true`, `Cost=true`, …) drives visibility bindings — matching the doc's
  bật/tắt checklist.
- **Contract (kept intentionally thin to avoid over-engineering):**
  ```
  interface IWidget { string Key { get; } string Title { get; } bool IsEnabled { get; } }
  ```
  The View is resolved by convention/DataTemplate keyed on the VM type — no `UserControl`
  reference leaks into Core.
- **Extension point:** a new widget is a new VM+View+registration; no core change. **Future
  (v2):** external assemblies discovered and loaded as plugins implementing `IWidget`.
- **MVP widgets:** Model, Context, Cost, Task, Cpu, Ram, Session, Git. **Deferred:** History,
  Notification-history panel.

---

## 11. Dependency Injection Plan

`Host.CreateApplicationBuilder`:
- **Options:** bind `HudOptions`, `WidgetOptions`, `AlertOptions`, `WindowOptions` from
  layered config (§14) via `IOptionsMonitor` (hot reload).
- **Singletons:** `IMessenger` (WeakReferenceMessenger.Default wrapper), `IPricingCatalog`,
  `IHistoryStore`, all providers, `ISnapshotAggregator`, `IAlertEvaluator`, `TimeProvider`.
- **Hosted services:** the five §8 services registered via `AddHostedService`.
- **Presentation:** `ShellViewModel`, `MainViewModel`, each widget VM (singleton — one live
  overlay), `OverlayWindow`, `TrayIconService`.
- **Composition root** = `App.OnStartup`: build host, `StartAsync`, resolve + show
  `OverlayWindow`. Graceful `StopAsync` on exit.

Rationale: everything constructor-injected; no service locator; no statics beyond the
messenger facade.

---

## 12. Event Flow

```
PeriodicTimer tick (StatusPollingService)
   └─> IClaudeProvider.GetAsync → ClaudeSnapshot
        └─> ISnapshotAggregator.Merge(claude, lastGit, lastMetrics) → HudSnapshot
             └─> IMessenger.Send(SnapshotUpdatedMessage)
                  ├─> MainViewModel + widget VMs (marshalled to Dispatcher) → UI updates
                  ├─> NotificationService → IAlertEvaluator → AlertRaisedMessage + toast
                  └─> HistoryRecorder → IHistoryStore.Append

FileSystemWatcher(.git) → GitWatcherService (debounced) → IGitProvider → Send(git slice)
MetricsCollector tick → System/Metrics providers → Send(perf slice)
```

The messenger decouples producers (services) from consumers (VMs, recorder, notifier). Git
and metrics arrive on their own cadence; the aggregator keeps last-known slices so any single
tick yields a complete snapshot.

---

## 13. Data Flow

```
~/.claude/projects/**/<session>.jsonl   ──tail──▶ JsonlTailer ─▶ TranscriptParser
                                                                     │
appsettings + user settings ─▶ IOptions        TokenCostCalculator ◀┘ (+ IPricingCatalog)
                                                                     │
Working repo .git ──▶ IGitProvider ─┐                               ▼
OS counters ──▶ System/Metrics ─────┼─▶ ISnapshotAggregator ─▶ HudSnapshot ─▶ (Messenger)
                                    ┘                                   │
                                                              History ring ◀┘  ▶ ViewModels ▶ XAML
```

- **Context %** = `(input + cache_read + cache_creation)` of the latest assistant turn ÷
  model context window.
- **Cost** = Σ over turns of `tokens × price(model, bucket)`; day/week/month buckets keyed by
  `timestamp`, aggregated across the user's sessions.
- **Task state** = classify latest event (`tool_use` name → Reading/Writing/Searching/Calling;
  text/thinking → Generating/Thinking; no event > idle-threshold → Idle).

---

## 14. Configuration Strategy

- **Layered:** bundled `appsettings.json` (defaults) → user overrides at
  `%APPDATA%\ClaudeMiniAlert\settings.json` (writable) → in-session changes persisted back.
- **Strongly typed:** `IOptionsMonitor<HudOptions>` etc.; `OnChange` propagates refresh
  interval/opacity/theme live without restart.
- **Covered keys:** refresh interval, theme, opacity, font, per-widget visibility, alert
  thresholds (context 80/90/95, cost 5/10/20), window position/size, mode, hotkeys,
  click-through, idle-minutes, Claude projects root override.
- **Writer:** a small `ISettingsWriter` serializes the user layer with System.Text.Json
  (atomic write to temp + move).

---

## 15. Logging Strategy

- **Serilog** via `UseSerilog`; sinks: rolling file at `%APPDATA%\ClaudeMiniAlert\logs\`
  (7-day retention) + Debug sink in DEV.
- **Levels:** Information for lifecycle/provider cadence summaries; Warning for
  degraded/skipped providers; Error for caught exceptions (with source context). Verbose
  parser tracing behind a config flag.
- **Structured properties:** `ProviderName`, `SessionId`, `Elapsed` to make performance
  regressions greppable. No transcript *content* is logged (privacy).

---

## 16. Error Handling Strategy

- **Provider isolation:** each provider call wrapped; on failure it logs, returns the
  last-known/empty slice, and the aggregator keeps the previous good value. One bad source
  never blanks the whole HUD.
- **Parser robustness:** malformed/partial JSONL lines (mid-append) are skipped, offset only
  advances on complete lines; the tailer tolerates file truncation/rotation (session switch).
- **Fault surfacing:** repeated failures flip the relevant widget to a 🔴 Error micro-state and
  raise a throttled `AlertRaisedMessage`.
- **Global:** `DispatcherUnhandledException` + `AppDomain.UnhandledException` logged; app
  attempts to stay alive (a HUD crashing is worse than a stale widget).

---

## 17. Background Service Strategy

- All collectors are `IHostedService` under the Generic Host with a shared
  `CancellationToken`; `StopAsync` drains cleanly on exit/tray-exit.
- Cadence via `PeriodicTimer` (not timers-that-reenter); Git via `FileSystemWatcher` + debounce
  so idle CPU stays ≈0 %.
- Services communicate **only** through the messenger and Core abstractions — never reach into
  the UI or each other. This keeps them independently testable and satisfies the ≈0 %-idle and
  "must not block UI" NFRs.

---

## 18. Threading Strategy

- **UI thread:** owns all WPF controls + the overlay window.
- **Background threads:** all provider I/O and parsing (`async`/`await`, `ConfigureAwait(false)`
  in Core/Infra).
- **Marshalling:** messenger handlers in ViewModels post to `Dispatcher` (a
  `IDispatcherService` wrapper so VMs stay unit-testable). Snapshots are immutable records →
  safe to hand across threads without locks.
- **Determinism in tests:** inject `TimeProvider`; use `FakeTimeProvider` to advance timers.
- **No shared mutable state:** aggregator's last-known slices are guarded by a single
  lightweight lock or an immutable-swap pattern.

---

## 19. UI Composition Strategy

- **Shell:** `OverlayWindow` hosts a root layout bound to `MainViewModel`; a header
  (title/model + drag grip), a stack/grid of widget views, and a footer (CPU/RAM).
- **Widget hosting:** `ItemsControl` over enabled widget VMs with `DataTemplate`s keyed by VM
  type → adding a widget needs no shell edit.
- **Modes:** Full (300×420) vs Compact (300×150) swap a layout state (VisualStateManager /
  bound template selector); compact shows Model/Context/Cost/Task only.
- **Theming:** merged ResourceDictionaries (Dark/Light) selected by `Theme` option (Auto reads
  Windows apps-theme); Segoe UI / JetBrains Mono selectable.
- **Fluent look:** Wpf.Ui controls + DWM backdrop; rounded corners + acrylic/mica.

---

## 20. Overlay Window Design

- **Style:** `WindowStyle=None`, `AllowsTransparency=True`, `ResizeMode=CanResizeWithGrip`,
  `Topmost=True`, `ShowInTaskbar=False`.
- **Interop (`WindowInterop`):** `DwmSetWindowAttribute` for rounded corners + system backdrop
  (Mica/Acrylic); optional `WS_EX_TRANSPARENT | WS_EX_LAYERED` for **click-through** toggle;
  optional `WS_EX_TOOLWINDOW` to hide from Alt-Tab.
- **Behavior:** drag-anywhere via `DragMove` on the header (a `DragBehavior`); remembers
  position/size in `WindowOptions`; opacity bound to settings (60–100 %); global hotkey
  (`RegisterHotKey`) to show/hide.
- **Purpose/Extension:** single window instance; future multi-monitor snapping and edge-dock.

---

## 21. Notification Design

- **Rules → alerts:** `IAlertEvaluator` holds `AlertRule`s (context 80/90/95, cost threshold,
  idle, task-finished, tool-failed, network-lost). It compares consecutive snapshots and emits
  **edge-triggered, de-duplicated** `AlertRaisedMessage`s (no repeat spam while a condition
  persists; re-arm on recovery).
- **Delivery:** `INotificationProvider` → Windows toast (Windows App SDK / `H.NotifyIcon`), plus
  an in-HUD transient banner and tray balloon. Severity maps to color (🟡/🟠/🔴).
- **Config:** thresholds + per-alert enable in `AlertOptions`. *Future:* sound, Slack/webhook.

---

## 22. Extensibility Strategy

- **New data source / agent:** implement the relevant `I*Provider` (e.g. `CodexProvider`),
  register in DI — Core, UI, and other providers unchanged.
- **New widget:** add VM + View + DI + `WidgetOptions` flag; DataTemplate auto-hosts it.
- **New alert:** add an `AlertRule` (data/config-driven where possible).
- **Richer live task state:** drop-in `ISessionSource` backed by the deferred Named-Pipe/hook
  channel — `IClaudeProvider` merges it with file tailing.
- **v2 plugin model:** load external assemblies exposing `IWidget`/`I*Provider`; the thin
  contracts in Core are already the plugin ABI. No core redesign required — the explicit
  design intent of this document.

---

## 23. Testing Strategy

- **Unit (xUnit) — Core/Infra have no WPF dep:** `TranscriptParser` & `TokenCostCalculator`
  against **hand-authored JSONL fixtures** (do **not** read the user's real transcripts);
  `SessionLocator` selection logic; `AlertEvaluator` edge-trigger/de-dup; `ModelCatalog`
  pricing math; `SnapshotAggregator` merge/last-known behavior.
- **Time-dependent logic:** `FakeTimeProvider` for idle detection, latency, cadence.
- **ViewModel tests:** feed messages, assert projected properties (with a fake dispatcher).
- **Git provider:** run against a temp repo created in the test.
- **Coverage focus:** parsing/derivation correctness and alert semantics (the risk-heavy
  logic), not XAML.

---

## 24. Deployment Strategy

- **Build:** `dotnet publish -c Release -r win-x64` — self-contained single-file (trimmed
  where safe), no admin, no installer required for MVP.
- **Config:** `appsettings.json` bundled; user layer auto-created in `%APPDATA%`.
- **Distribution:** zipped exe for MVP. *Future:* MSIX package (Start-menu, clean uninstall) +
  auto-update (Velopack/Squirrel); optional "start with Windows" via a Startup shortcut/registry
  Run key (user-opt-in).
- **Diagnostics:** logs + settings co-located under `%APPDATA%\ClaudeMiniAlert\`.

---

## 25. Future Roadmap (aligned to the requirement's MVP → v1 → v2)

- **MVP (this blueprint):** overlay + Model/Context/Cost/Task/CPU/RAM/basic-Git widgets,
  passive JSONL tailing, settings, compact/full, minimal tray, alert colors.
- **v1.0:** History widget (LiveCharts2), configurable toast notifications, full tray menu,
  saved layouts, hotkeys, click-through, Named-Pipe hook/statusline channel for precise task
  state.
- **v2.0:** plugin loading (`IWidget`/`I*Provider` from external assemblies), multi-agent
  providers (Codex CLI, Gemini CLI), multi-session sync, third-party WebSocket API,
  cross-platform head reusing Core.

---

## Suggested Implementation Phases (post-approval)

1. **Phase 2 – Bootstrap:** solution, 3 projects + tests, Generic Host, DI, Serilog,
   layered config, empty overlay shell.
2. **Phase 3 – Core Infra:** Core models/abstractions, pricing catalog, messaging,
   Claude/Git/System providers, background services, history ring, alert evaluator.
3. **Phase 4 – UI:** OverlayWindow (interop, drag, backdrop, modes), theming, widget host.
4. **Phase 5 – Widgets:** implement each widget VM+View independently (Context, Cost, Model,
   Git, Cpu, Ram, Session, Task) against the finished providers.
5. **Phase 6 – Polish:** notifications/toasts, performance pass to hit NFRs, tray, packaging,
   unit tests to green.

## Open Items / Risks

- Model→context-window & pricing map must track new models — keep it as data in `ModelCatalog`.
- Multiple concurrent Claude sessions: MVP picks newest-modified; session picker in v1.
- Task sub-states (Thinking vs Generating) are inferred from event types until the hook
  channel lands in v1.
- Claude-process identification (`node` vs `claude`) may need heuristics per install method.
