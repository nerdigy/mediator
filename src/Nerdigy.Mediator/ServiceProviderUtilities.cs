using System.Collections;

namespace Nerdigy.Mediator;

/// <summary>
/// Provides helper methods for resolving single and multiple services from an <see cref="IServiceProvider"/>.
/// </summary>
internal static class ServiceProviderUtilities
{
    /// <summary>
    /// Resolves all registered services for a closed generic service type.
    /// </summary>
    /// <typeparam name="TService">The service type to resolve.</typeparam>
    /// <param name="serviceProvider">The service provider used for resolution.</param>
    /// <returns>A sequence of resolved services, or an empty sequence when none are registered.</returns>
    public static IEnumerable<TService> GetServices<TService>(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var services = serviceProvider.GetService(typeof(IEnumerable<TService>)) as IEnumerable<TService>;

        if (services is null)
        {
            return [];
        }

        return services;
    }

    /// <summary>
    /// Resolves all registered services for a runtime service type.
    /// </summary>
    /// <param name="serviceProvider">The service provider used for resolution.</param>
    /// <param name="serviceType">The runtime service type to resolve.</param>
    /// <returns>A read-only list of resolved services, or an empty list when none are registered.</returns>
    public static IReadOnlyList<object> GetServices(IServiceProvider serviceProvider, Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(serviceType);

        var enumerableServiceType = typeof(IEnumerable<>).MakeGenericType(serviceType);
        var services = serviceProvider.GetService(enumerableServiceType);

        if (services is not IEnumerable enumerableServices)
        {
            return [];
        }

        var capacity = enumerableServices is ICollection collection
            ? collection.Count
            : 0;
        List<object> resolvedServices = capacity == 0
            ? []
            : new List<object>(capacity);

        foreach (var service in enumerableServices)
        {
            if (service is null)
            {
                continue;
            }

            resolvedServices.Add(service);
        }

        if (resolvedServices.Count == 0)
        {
            return [];
        }

        return resolvedServices;
    }
}
