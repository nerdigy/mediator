using Microsoft.Extensions.DependencyInjection;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Remembers what earlier <c>AddMediator</c> calls registered on a service collection.
/// </summary>
/// <remarks>
/// <c>AddMediator</c> can be called more than once, for example once per module. The first call sets the mediator
/// lifetime and notification publisher, and later calls may only repeat them. Handlers registered by every call are
/// tracked, so a second implementation for the same request is reported instead of being silently dropped.
/// </remarks>
internal sealed class MediatorRegistrationState
{
    private readonly Dictionary<Type, HandlerRegistration> _handlers = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="MediatorRegistrationState"/> class.
    /// </summary>
    /// <param name="mediatorLifetime">The mediator lifetime set by the first call.</param>
    /// <param name="publisher">The notification publisher set by the first call.</param>
    public MediatorRegistrationState(ServiceLifetime mediatorLifetime, PublisherRegistration publisher)
    {
        MediatorLifetime = mediatorLifetime;
        Publisher = publisher;
    }

    /// <summary>
    /// Gets the mediator lifetime set by the first <c>AddMediator</c> call.
    /// </summary>
    public ServiceLifetime MediatorLifetime { get; }

    /// <summary>
    /// Gets the notification publisher set by the first <c>AddMediator</c> call.
    /// </summary>
    public PublisherRegistration Publisher { get; }

    /// <summary>
    /// Gets the state stored on a service collection, or <see langword="null"/> when <c>AddMediator</c> was not called yet.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The stored state, if any.</returns>
    public static MediatorRegistrationState? Find(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(MediatorRegistrationState) && !descriptor.IsKeyedService)
            {
                return descriptor.ImplementationInstance as MediatorRegistrationState;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the handler a mediator registration added for a handler service type.
    /// </summary>
    /// <param name="serviceType">The closed handler interface.</param>
    /// <param name="registration">The handler registration, when found.</param>
    /// <returns><see langword="true"/> when a mediator registration added a handler for the service type.</returns>
    public bool TryGetHandler(Type serviceType, out HandlerRegistration registration)
    {
        return _handlers.TryGetValue(serviceType, out registration);
    }

    /// <summary>
    /// Records the handler a mediator registration added for a handler service type.
    /// </summary>
    /// <param name="serviceType">The closed handler interface.</param>
    /// <param name="registration">The handler registration.</param>
    public void SetHandler(Type serviceType, HandlerRegistration registration)
    {
        _handlers[serviceType] = registration;
    }

    /// <summary>
    /// Represents a handler added by a mediator registration.
    /// </summary>
    /// <param name="ImplementationType">The handler implementation type.</param>
    /// <param name="IsExplicit">Whether the handler was registered explicitly rather than found by scanning.</param>
    internal readonly record struct HandlerRegistration(Type ImplementationType, bool IsExplicit);
}