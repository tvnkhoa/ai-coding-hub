using Microsoft.Extensions.DependencyInjection;

namespace AiCodingHub.App.Widgets;

/// <summary>
/// Registers the widget host and every widget as a <see cref="WidgetViewModel"/>. This is the
/// single place to add a new widget to the composition — the host discovers them via the
/// injected <c>IEnumerable&lt;WidgetViewModel&gt;</c>.
/// </summary>
public static class WidgetRegistration
{
    public static IServiceCollection AddWidgets(this IServiceCollection services)
    {
        services.AddSingleton<WidgetHostViewModel>();

        services.AddSingleton<WidgetViewModel, SessionsWidgetViewModel>();
        services.AddSingleton<WidgetViewModel, ModelWidgetViewModel>();
        services.AddSingleton<WidgetViewModel, SessionWidgetViewModel>();
        services.AddSingleton<WidgetViewModel, ContextWidgetViewModel>();
        services.AddSingleton<WidgetViewModel, CostWidgetViewModel>();
        services.AddSingleton<WidgetViewModel, CpuWidgetViewModel>();
        services.AddSingleton<WidgetViewModel, RamWidgetViewModel>();
        services.AddSingleton<WidgetViewModel, HistoryWidgetViewModel>();

        return services;
    }
}
