using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;
using AiCodingHub.Infrastructure.History;
using AiCodingHub.Infrastructure.Hosting;
using AiCodingHub.Infrastructure.Notifications;
using AiCodingHub.Infrastructure.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiCodingHub.Infrastructure.DependencyInjection;

/// <summary>
/// Registers all providers, the aggregator, alert/history services, and the background
/// collectors. The host (App) supplies configuration/options and the messenger.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(global::System.TimeProvider.System);

        // Providers + core services.
        services.TryAddSingleton<IPricingCatalog, ModelCatalog>();
        services.TryAddSingleton<IClaudeProvider, ClaudeProvider>();
        services.TryAddSingleton<IUsageProvider, AnthropicUsageProvider>();
        services.TryAddSingleton<IProcessInspector, ProcessInspector>();
        services.TryAddSingleton<ISystemProvider, SystemProvider>();
        services.TryAddSingleton<IMetricsProvider, MetricsProvider>();
        services.TryAddSingleton<IHistoryStore>(_ => new InMemoryRingHistoryStore());
        services.TryAddSingleton<ISnapshotAggregator, SnapshotAggregator>();
        services.TryAddSingleton<IAlertEvaluator, AlertEvaluator>();
        services.TryAddSingleton<INotificationProvider, LogNotificationProvider>();

        // Background collectors (order does not matter — all event/timer driven).
        services.AddHostedService<StatusPollingService>();
        services.AddHostedService<MetricsCollector>();
        services.AddHostedService<UsageCollector>();
        services.AddHostedService<CostScanService>();
        services.AddHostedService<HistoryRecorder>();
        services.AddHostedService<NotificationService>();

        return services;
    }
}
