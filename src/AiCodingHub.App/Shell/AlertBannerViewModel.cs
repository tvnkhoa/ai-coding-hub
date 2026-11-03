using System;
using System.Windows;
using System.Windows.Threading;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Messaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AiCodingHub.App.Shell;

/// <summary>
/// Transient in-app banner for raised alerts. Subscribes to <see cref="AlertRaisedMessage"/>
/// (already fanned out by the notification service), shows the latest alert, and auto-hides
/// after a few seconds. Purely presentational — no evaluation logic lives here.
/// </summary>
public sealed partial class AlertBannerViewModel : ObservableObject
{
    private static readonly TimeSpan AutoHide = TimeSpan.FromSeconds(6);
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private AlertSeverity _severity = AlertSeverity.Info;

    public AlertBannerViewModel(IMessenger messenger)
    {
        // Created on the UI thread during startup, so this timer binds to the UI dispatcher.
        _timer = new DispatcherTimer { Interval = AutoHide };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            IsVisible = false;
        };

        messenger.Register<AlertBannerViewModel, AlertRaisedMessage>(
            this, static (recipient, message) => recipient.OnAlert(message.Alert));
    }

    private void OnAlert(Alert alert)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            Title = alert.Title;
            Message = alert.Message;
            Severity = alert.Severity;
            IsVisible = true;
            _timer.Stop();
            _timer.Start();
        });
    }

    [RelayCommand]
    private void Dismiss()
    {
        _timer.Stop();
        IsVisible = false;
    }
}
