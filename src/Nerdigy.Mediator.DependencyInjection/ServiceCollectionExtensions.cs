using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nerdigy.Mediator;
using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Provides extension methods for registering Nerdigy Mediator services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Nerdigy Mediator services using a configuration callback.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="configure">The options configuration callback.</param>
    /// <returns>The configured service collection.</returns>
    public static IServiceCollection AddMediator(
        this IServiceCollection services,
        Action<NerdigyMediatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NerdigyMediatorOptions();
        configure(options);

        if (options.AssembliesToScan.Count == 0)
        {
            throw new InvalidOperationException(
                "No assemblies were configured for mediator scanning. Call RegisterServicesFromAssembly(...) or RegisterServicesFromAssemblies(...) inside AddMediator(options => ...).");
        }

        RegisterCoreMediatorServices(services, options);
        RegisterConfiguredPipelineBehaviors(services, options);
        MediatorServiceScanner.RegisterFromAssemblies(services, options.AssembliesToScan, options.HandlerLifetime);

        return services;
    }

    /// <summary>
    /// Registers Nerdigy Mediator services by scanning provided assemblies.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The configured service collection.</returns>
    public static IServiceCollection AddMediator(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        return services.AddMediator(options => options.RegisterServicesFromAssemblies(assemblies));
    }

    /// <summary>
    /// Registers core mediator services and configured publisher strategy.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="options">The mediator options.</param>
    private static void RegisterCoreMediatorServices(IServiceCollection services, NerdigyMediatorOptions options)
    {
        services.TryAdd(ServiceDescriptor.Describe(typeof(IMediator), typeof(Mediator), options.MediatorLifetime));
        services.TryAdd(ServiceDescriptor.Describe(typeof(ISender), static provider => provider.GetRequiredService<IMediator>(), options.MediatorLifetime));
        services.TryAdd(ServiceDescriptor.Describe(typeof(IPublisher), static provider => provider.GetRequiredService<IMediator>(), options.MediatorLifetime));
        RegisterNotificationPublisher(services, options);
    }

    /// <summary>
    /// Registers the configured notification publisher.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="options">The mediator options.</param>
    private static void RegisterNotificationPublisher(IServiceCollection services, NerdigyMediatorOptions options)
    {
        if (options.NotificationPublisherInstance is not null)
        {
            services.TryAddSingleton(options.NotificationPublisherInstance);
            services.TryAddSingleton<INotificationPublisher>(options.NotificationPublisherInstance);

            return;
        }

        services.TryAdd(ServiceDescriptor.Singleton(typeof(INotificationPublisher), options.NotificationPublisherType));
    }

    /// <summary>
    /// Registers open-generic request pipeline behaviors configured through options.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="options">The mediator options.</param>
    private static void RegisterConfiguredPipelineBehaviors(IServiceCollection services, NerdigyMediatorOptions options)
    {
        foreach (var behaviorType in options.OpenBehaviorTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Describe(typeof(IPipelineBehavior<,>), behaviorType, options.HandlerLifetime));
        }
    }
}