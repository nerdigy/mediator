namespace Nerdigy.Mediator;

/// <summary>
/// Provides helper methods for resolving services from an <see cref="IServiceProvider"/>.
/// </summary>
internal static class ServiceProviderUtilities
{
    /// <summary>
    /// Resolves all registered services for a closed service type.
    /// </summary>
    /// <remarks>
    /// Microsoft.Extensions.DependencyInjection resolves <see cref="IEnumerable{T}"/> as an array, so the common case does
    /// not copy. Other containers' sequences are materialized once so callers can check the count and index into them.
    /// </remarks>
    /// <typeparam name="TService">The service type to resolve.</typeparam>
    /// <param name="serviceProvider">The service provider used for resolution.</param>
    /// <returns>The resolved services, or an empty array when none are registered.</returns>
    public static TService[] GetServices<TService>(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetService(typeof(IEnumerable<TService>)) switch
        {
            TService[] array => array,
            IEnumerable<TService> services => [.. services],
            _ => [],
        };
    }
}