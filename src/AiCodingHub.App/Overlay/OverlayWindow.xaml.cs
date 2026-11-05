using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AiCodingHub.App.Interop;
using AiCodingHub.App.Settings;
using AiCodingHub.App.Shell;
using AiCodingHub.App.Theming;
using AiCodingHub.Core.Options;
using Microsoft.Extensions.Options;

namespace AiCodingHub.App.Overlay;

/// <summary>
/// The always-on-top HUD shell: DWM rounded corners + acrylic backdrop (Windows 11),
/// tool-window (hidden from Alt-Tab), drag, resizable width, compact/full mode, and
/// position/width persistence.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly IOptionsMonitor<WindowOptions> _windowOptions;
    private readonly IUserSettingsWriter _writer;
    private readonly ThemeManager _theme;

    private IntPtr _hwnd;

    public OverlayWindow(
        ShellViewModel viewModel,
        IOptionsMonitor<WindowOptions> windowOptions,
        IUserSettingsWriter writer,
        ThemeManager theme)
    {
        InitializeComponent();
        _vm = viewModel;
        _windowOptions = windowOptions;
        _writer = writer;
        _theme = theme;
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += (_, _) => UpdateMaxHeight();
    }

    // The XAML MaxHeight is the upper cap; below it the window may not run past the bottom of the
    // work area of the monitor it sits on, so the footer stays visible and the list scrolls.
    private const double HeightCap = 860;
    private const double BottomMargin = 8;
    private const int WmSettingChange = 0x001A, WmDisplayChange = 0x007E, SpiSetWorkArea = 0x002F;

    private void UpdateMaxHeight()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            var workBottom = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea.Bottom / scale;
            var max = Math.Clamp(workBottom - Top - BottomMargin, MinHeight, HeightCap);
            if (Math.Abs(MaxHeight - max) > 0.5)
            {
                MaxHeight = max;
            }
        }
        catch (Exception)
        {
            // Best-effort: keep the current cap.
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDisplayChange || (msg == WmSettingChange && wParam.ToInt64() == SpiSetWorkArea))
        {
            Dispatcher.BeginInvoke(UpdateMaxHeight, System.Windows.Threading.DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(UpdateMaxHeight, System.Windows.Threading.DispatcherPriority.Background);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        WindowInterop.EnableRoundedCorners(_hwnd);
        WindowInterop.SetToolWindow(_hwnd, true);

        // The acrylic backdrop shows through a transparent composition background.
        if (HwndSource.FromHwnd(_hwnd) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
        }

        HwndSource.FromHwnd(_hwnd)?.AddHook(OnWindowMessage);

        _vm.UseBackdrop = WindowInterop.TryEnableBackdrop(_hwnd, dark: !_theme.IsLight);
        _theme.ThemeChanged += isLight => WindowInterop.SetDarkMode(_hwnd, !isLight);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Fixed width; the height always follows the content (SizeToContent), so the HUD grows and
        // shrinks with the sessions it shows. Only the position is restored.
        var opts = _windowOptions.CurrentValue;

        if (opts.Left is { } left && opts.Top is { } top && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        UpdateMaxHeight();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _writer.SaveWindow(new WindowOptions
        {
            Left = Left,
            Top = Top,
            Width = ActualWidth,
            Height = ActualHeight,
            Mode = _vm.IsCompact ? "Compact" : "Full",
            Topmost = _vm.IsPinned,
        });
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A click on a header button must reach the button. DragMove() captures the mouse in a
        // modal loop that eats the button's release (and its Click), so skip dragging when the
        // press originates on a Button.
        if (e.OriginalSource is DependencyObject src && FindAncestor<Button>(src) is not null)
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsOnScreen(double left, double top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        return left >= virtualLeft && top >= virtualTop
            && left < virtualRight - 50 && top < virtualBottom - 50;
    }
}
