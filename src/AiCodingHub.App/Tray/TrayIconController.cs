using System;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace AiCodingHub.App.Tray;

/// <summary>
/// System-tray presence for the HUD. Because the overlay is a tool-window (hidden from the
/// taskbar and Alt-Tab), the tray icon is the only way to re-show it after hiding, and to exit.
/// Uses the built-in WinForms <see cref="WinForms.NotifyIcon"/> — no extra package.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly Window _window;
    private readonly WinForms.NotifyIcon _icon;

    public TrayIconController(Window window)
    {
        _window = window;

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Claude Code Mini Alert",
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleVisibility());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());
        _icon.ContextMenuStrip = menu;

        _icon.DoubleClick += (_, _) => ToggleVisibility();
    }

    /// <summary>
    /// Loads the Straw Hat Pirates Jolly Roger from the embedded resource. Falls back to the
    /// generic application icon if the resource is ever missing — a tray glyph must never crash
    /// the only affordance for re-showing / exiting the HUD.
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/luffy.ico", UriKind.Absolute);
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
