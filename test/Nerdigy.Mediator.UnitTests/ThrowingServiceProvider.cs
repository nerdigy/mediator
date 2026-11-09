namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Wraps a service provider and throws when chosen service types are resolved.
/// </summary>
/// <param name="inner">The provider that resolves every other service type.</param>
/// <param name="shouldThrow">Selects the service types whose resolution fails.</param>
/// <param name="failure">The exception to throw.</param>
internal sealed class ThrowingServiceProvider(IServiceProvider inner, Func<Type, bool> shouldThrow, Exception failure) : IServiceProvider
{
    /// <summary>
    /// Resolves a service, or throws when the service type is selected to fail.
    /// </summary>
    /// <param name="serviceType">The service type to resolve.</param>
    /// <returns>The service from the inner provider.</returns>
    public object? GetService(Type serviceType)
    {
        if (shouldThrow(serviceType))
        {
            throw failure;
        }

        return inner.GetService(serviceType);
    }
}