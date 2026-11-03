using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AiCodingHub.App.Overlay;
using AiCodingHub.App.Settings;
using AiCodingHub.App.Shell;
using AiCodingHub.App.Theming;
using AiCodingHub.App.Tray;
using AiCodingHub.App.Widgets;
using AiCodingHub.Core.Options;
using AiCodingHub.Infrastructure.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AiCodingHub.App;

/// <summary>
/// Composition root: builds the Generic Host (DI + Serilog + layered config), starts it,
/// then resolves and shows the overlay window. Background services (Phase 3) will be
/// registered here as <see cref="IHostedService"/>s and started by the same host.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private TrayIconController? _tray;

    /// <summary>Per-user data root for settings and logs.</summary>
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeMiniAlert");

    public App()
    {
        // Keep the HUD alive on unexpected errors — a stale widget beats a crash.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception, "Unhandled domain exception");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(AppDataDir);
        var userSettings = Path.Combine(AppDataDir, "settings.json");

        var builder = Host.CreateApplicationBuilder();

        // Layered config: bundled defaults, then optional writable per-user overrides.
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile(userSettings, optional: true, reloadOnChange: true);

        // Serilog: config-driven + rolling file under %APPDATA%\ClaudeMiniAlert\logs.
        builder.Services.AddSerilog((services, cfg) => cfg
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(AppDataDir, "logs", "hud-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7));

        // Strongly-typed, hot-reloadable options.
        builder.Services.Configure<HudOptions>(builder.Configuration.GetSection(HudOptions.SectionName));
        builder.Services.Configure<WidgetOptions>(builder.Configuration.GetSection(WidgetOptions.SectionName));
        builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.SectionName));
        builder.Services.Configure<WindowOptions>(builder.Configuration.GetSection(WindowOptions.SectionName));
        builder.Services.Configure<UsageOptions>(builder.Configuration.GetSection(UsageOptions.SectionName));

        // In-process event bus + presentation.
        builder.Services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        builder.Services.AddWidgets();
        builder.Services.AddSingleton<AlertBannerViewModel>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<OverlayWindow>();
        builder.Services.AddSingleton<ThemeManager>();
        builder.Services.AddSingleton<IUserSettingsWriter>(sp =>
            new UserSettingsWriter(userSettings,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<UserSettingsWriter>()));

        // Providers + background collectors (Claude sessions, cost, system, aggregator, alerts, history).
        builder.Services.AddInfrastructure();

        _host = builder.Build();
        await _host.StartAsync();

        Log.Information("AiCodingHub starting; data dir {AppDataDir}", AppDataDir);

        // Apply the configured theme before showing the window.
        _host.Services.GetRequiredService<ThemeManager>().Initialize();

        var window = _host.Services.GetRequiredService<OverlayWindow>();
        MainWindow = window;
        window.Show();

        // Tray icon owns the only re-show/exit affordance once the tool-window is hidden.
        _tray = new TrayIconController(window, _host.Services.GetRequiredService<IMessenger>());
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception");
        e.Handled = true;
    }
}
