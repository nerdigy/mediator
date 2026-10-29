namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Provides a minimal service provider for unit tests.
/// </summary>
/// <remarks>
/// Like Microsoft.Extensions.DependencyInjection, it resolves an unregistered <see cref="IEnumerable{T}"/> as an empty
/// array rather than <see langword="null"/>, so unit tests exercise the same paths as production.
/// </remarks>
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
    /// <returns>The registered service instance, an empty array for an unregistered sequence, or <see langword="null"/>.</returns>
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (_services.TryGetValue(serviceType, out var service))
        {
            return service;
        }

        return serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? Array.CreateInstance(serviceType.GetGenericArguments()[0], 0)
            : null;
    }
}