namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Provides a minimal service provider for unit tests.
/// </summary>
internal sealed class TestServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object?> _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestServiceProvider"/> class.
    /// </summary>
    /// <param name="registrations">The service registrations used for type resolution.</param>
    public TestServiceProvider(params (Type ServiceType, object? Implementation)[] registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _services = new Dictionary<Type, object?>(registrations.Length);

        foreach (var registration in registrations)
        {
            _services[registration.ServiceType] = registration.Implementation;
        }
    }

    /// <summary>
    /// Resolves a registered service instance.
    /// </summary>
    /// <param name="serviceType">The service type to resolve.</param>
    /// <returns>The registered service instance, or <see langword="null"/> when not registered.</returns>
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        
        _services.TryGetValue(serviceType, out var service);

        return service;
    }
}
