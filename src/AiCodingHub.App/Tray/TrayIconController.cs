using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AiCodingHub.App.Interop;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using WinForms = System.Windows.Forms;

namespace AiCodingHub.App.Tray;

/// <summary>
/// System-tray presence for the HUD. Because the overlay is a tool-window (hidden from the
/// taskbar and Alt-Tab), the tray icon is the way to re-show it after hiding, and to exit.
/// It also raises the Windows notification when a session needs the owner's decision (shown
/// even while the HUD is hidden; clicking it opens that session's terminal) and keeps a short
/// live summary in its tooltip. Uses the built-in WinForms <see cref="WinForms.NotifyIcon"/>.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly Window _window;
    private readonly IMessenger _messenger;
    private readonly WinForms.NotifyIcon _icon;
    private int? _notifiedPid;

    public TrayIconController(Window window, IMessenger messenger)
    {
        _window = window;
        _messenger = messenger;

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Mini Monitor",
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleVisibility());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (_, _) => ToggleVisibility();
        _icon.BalloonTipClicked += (_, _) => OpenNotifiedSession();

        messenger.Register<TrayIconController, AlertRaisedMessage>(this, static (r, m) => r.OnAlert(m.Alert));
        messenger.Register<TrayIconController, SnapshotUpdatedMessage>(this, static (r, m) => r.OnSnapshot(m.Snapshot));
    }

    private void OnAlert(Alert alert)
    {
        if (alert.Kind != AlertKind.DecisionNeeded)
        {
            return;
        }

        Post(() =>
        {
            _notifiedPid = FindPid(alert.Title);
            // Short on purpose: who needs you, no preview of the question.
            _icon.ShowBalloonTip(10_000, alert.Title, "Click to open the terminal.", WinForms.ToolTipIcon.None);
        });
    }

    private HudSnapshot? _last;

    private void OnSnapshot(HudSnapshot snapshot)
    {
        _last = snapshot;
        var live = snapshot.Groups.Sum(g => g.LiveCount);
        var waiting = snapshot.Groups.Sum(g => g.Sessions.Count(s => s.Decision is not null));
        var text = waiting > 0 ? $"Mini Monitor · {live} live · {waiting} need you" : $"Mini Monitor · {live} live";
        Post(() =>
        {
            if (_icon.Text != text)
            {
                _icon.Text = text.Length > 63 ? text[..63] : text;
            }
        });
    }

    /// <summary>The pid of the session an alert title names ("group · name needs your decision").</summary>
    private int? FindPid(string title)
    {
        if (_last is null)
        {
            return null;
        }

        foreach (var g in _last.Groups)
        {
            foreach (var s in g.Sessions)
            {
                if (s.Decision is not null && title.StartsWith($"{g.Name} · {s.DisplayName} ", StringComparison.Ordinal))
                {
                    return s.Pid;
                }
            }
        }
        return null;
    }

    private void OpenNotifiedSession()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_notifiedPid is not { } pid || !WindowActivator.TryFocusSession(pid))
        {
            _window.Activate();
        }
    }

    /// <summary>
    /// Loads the Mini Monitor mark from the embedded resource. Falls back to the generic
    /// application icon if the resource is ever missing — a tray glyph must never crash the only
    /// affordance for re-showing / exiting the HUD.
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                return new Icon(stream, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            // fall through to the system default below
        }

        return SystemIcons.Application;
    }

    private void ToggleVisibility()
    {
        if (_window.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
            _window.Activate();
        }
    }

    private static void Post(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _icon.Visible = false;
        _icon.Dispose();
    }
}
