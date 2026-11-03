Nếu mục tiêu là có một **mini HUD (Heads-Up Display)** cho Claude Code giống MSI Afterburner hoặc FPS Monitor, thì mình sẽ thiết kế nó như một ứng dụng độc lập thay vì sửa Claude Code. Cách này ổn định hơn và không phụ thuộc vào việc Anthropic thay đổi giao diện.

# Claude Code Mini Alert Screen (Windows)

## Mục tiêu

Xây dựng một ứng dụng Windows luôn nổi (Always On Top) hiển thị realtime trạng thái của Claude Code.

Đối tượng sử dụng:

* Claude Code CLI
* Claude Code Desktop
* Power user
* AI Engineer
* Developer

---

# Demo UI

```text
╭────────────────────────────────────╮
│ 🤖 Claude Code                     │
├────────────────────────────────────┤
│ Model      Sonnet 4                │
│ Project    GoogleAds               │
│ Branch     feature/oauth           │
│                                    │
│ Context    ████████░░ 81%          │
│ Tokens     162,431 / 200,000       │
│ Cost       $3.18                   │
│ Session    01:24:13                │
│                                    │
│ Requests   128                     │
│ Avg Latency 3.2s                   │
│                                    │
│ Auto Accept     OFF                │
│ Dynamic WF      ON                 │
│ Ultracode       ON                 │
├────────────────────────────────────┤
│ CPU  8%      RAM 780 MB            │
╰────────────────────────────────────╯
```

---

# Design

Theme

* Fluent Design
* Windows 11
* Acrylic
* Mica
* Rounded Corner

Opacity

```
80%
```

Always On Top

```
true
```

Click Through

```
optional
```

Small size

```
300x420
```

Resizable

```
Yes
```

Compact mode

```
300x150
```

---

# Main Features

## 1. Session Monitor

Hiển thị

```
Current Project

Current Model

Claude Version

Workspace

Session Duration
```

Ví dụ

```
Model

Claude Sonnet 4

Workspace

D:\Projects\GoogleAds

Running

2h 18m
```

---

## 2. Context Usage

Realtime

```
Current Tokens

Max Context

Remaining

Percentage
```

Progress Bar

```
█████████░ 90%
```

Color

```
Green

Yellow

Orange

Red
```

---

## 3. Cost Monitor

Hiển thị

```
Current Session Cost

Today Cost

Weekly Cost

Monthly Cost
```

Ví dụ

```
Session

$2.18

Today

$5.63

Month

$81.12
```

---

## 4. Request Statistics

```
Total Requests

Prompt Count

Tool Calls

Average Response

Longest Response

Average Tokens
```

---

## 5. Performance

```
CPU

RAM

Claude Process

GPU (optional)
```

---

## 6. Git Integration

Hiển thị

```
Repository

Current Branch

Modified Files

Uncommitted

Ahead/Behind
```

Ví dụ

```
GoogleAds

feature/oauth

5 Modified

2 Commit Ahead
```

---

## 7. File Activity

Theo dõi

```
Current File

Recently Edited

Total Changed

Current Folder
```

---

## 8. Task Monitor

Nếu Claude đang chạy

```
Generating

Thinking

Calling Tool

Searching

Reading File

Writing File

Idle
```

Status

```
🟢 Working

🟡 Waiting

🔵 Thinking

🔴 Error
```

---

## 9. Notification

Popup nhỏ

```
Context reaches 90%

Cost exceeds $5

Claude waiting input

Task completed

Tool failed

Network lost
```

---

## 10. History

Mini chart

```
Cost

Context

Requests

per minute
```

---

# Alert Rules

## Context

```
80%

Yellow
```

```
90%

Orange
```

```
95%

Red
```

---

## Cost

User configurable

```
$5

$10

$20
```

---

## Idle

```
No activity 5 min

Show Idle
```

---

# Widget System

Cho phép bật/tắt

```
□ Context

□ Cost

□ Git

□ CPU

□ RAM

□ Task

□ Notification

□ Session

□ History
```

---

# Mini Mode

```text
╭──────────────────────╮
│ 🤖 Sonnet 4          │
│ Context 81%          │
│ Cost $2.18           │
│ Task Thinking...     │
╰──────────────────────╯
```

---

# Tray Mode

System Tray

```
Claude Running

Context

Cost

Current Model
```

Right Click

```
Open

Hide

Restart

Settings

Exit
```

---

# Settings

Refresh

```
250 ms

500 ms

1 s

2 s
```

Theme

```
Dark

Light

Auto
```

Transparency

```
60~100%
```

Font

```
Segoe UI

JetBrains Mono
```

---

# Data Sources

| Data    | Source                       |
| ------- | ---------------------------- |
| Model   | Claude session/log           |
| Context | Claude status/session        |
| Cost    | Claude usage/session         |
| Git     | Local Git repository         |
| CPU/RAM | Windows Performance Counters |
| Files   | FileSystemWatcher            |
| Task    | Claude CLI output parsing    |

---

# Tech Stack

| Layer              | Technology                |
| ------------------ | ------------------------- |
| UI                 | WinUI 3 hoặc WPF (.NET 9) |
| Background Service | .NET Worker               |
| IPC                | Named Pipe / WebSocket    |
| Config             | JSON                      |
| Logging            | Serilog                   |
| Charts             | LiveCharts2               |
| Git                | LibGit2Sharp              |
| Notifications      | Windows App SDK Toast     |

---

# Plugin Architecture (v2)

Để dễ mở rộng, ứng dụng nên có kiến trúc plugin:

```text
MiniAlert
│
├── Core
├── Widgets
│   ├── ContextWidget
│   ├── CostWidget
│   ├── GitWidget
│   ├── TaskWidget
│   ├── CPUWidget
│   └── RAMWidget
│
├── Providers
│   ├── ClaudeProvider
│   ├── GitProvider
│   ├── WindowsProvider
│   └── MetricsProvider
│
└── Overlay
```

Mỗi widget chỉ cần implement:

```csharp
interface IWidget
{
    string Name { get; }
    Task UpdateAsync();
    UserControl View { get; }
}
```

Nhờ đó sau này có thể bổ sung các widget như:

* Token tốc độ (tokens/s)
* Prompt queue
* MCP Server status
* Docker status
* VS Code status
* GitHub Actions
* Azure/OpenAI API usage

mà không cần thay đổi phần lõi của ứng dụng.

## Lộ trình phát triển

* **MVP (1–2 tuần):** Overlay luôn nổi, hiển thị model, context, cost, trạng thái hiện tại, CPU/RAM, tích hợp Git cơ bản.
* **v1.0:** Thêm biểu đồ lịch sử, cảnh báo cấu hình được, tray icon, chế độ compact/full, lưu cấu hình người dùng.
* **v2.0:** Kiến trúc plugin, hỗ trợ nhiều AI coding agent (Claude Code, Codex CLI, Gemini CLI), đồng bộ nhiều session, API/WebSocket để bên thứ ba có thể phát triển widget riêng.

Theo mình, nếu đầu tư nghiêm túc thì nên hướng tới **một AI Coding HUD đa nền tảng** thay vì chỉ dành riêng cho Claude Code. Thiết kế theo hướng provider/plugin sẽ giúp sau này chỉ cần viết thêm một `IProvider` mới là có thể hỗ trợ các công cụ khác mà không phải thay đổi giao diện hay kiến trúc tổng thể.
