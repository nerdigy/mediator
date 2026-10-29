using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Represents one explicitly registered mediator component.
/// </summary>
/// <param name="serviceType">The component interface, closed or open generic.</param>
/// <param name="implementationType">The component implementation type.</param>
/// <param name="lifetime">The lifetime, or <see langword="null"/> to use <see cref="MediatorOptions.HandlerLifetime"/>.</param>
internal sealed class ComponentRegistration(
    Type serviceType,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
    ServiceLifetime? lifetime)
{
    /// <summary>
    /// Gets the component interface, closed or open generic.
    /// </summary>
    public Type ServiceType { get; } = serviceType;

    /// <summary>
    /// Gets the component implementation type.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type ImplementationType { get; } = implementationType;

    /// <summary>
    /// Gets the lifetime, or <see langword="null"/> to use <see cref="MediatorOptions.HandlerLifetime"/>.
    /// </summary>
    public ServiceLifetime? Lifetime { get; } = lifetime;

    /// <summary>
    /// Gets a value indicating whether the component is a handler, which allows only one implementation per service type.
    /// </summary>
    public bool IsSingleRegistration => MediatorComponentRules.IsSingleRegistration(
        ServiceType.IsGenericTypeDefinition ? ServiceType : ServiceType.GetGenericTypeDefinition());
}

/// <summary>
/// Represents the configured notification publisher.
/// </summary>
internal sealed class PublisherRegistration
{
    /// <summary>
    /// Gets the sequential publisher registration used when none is configured.
    /// </summary>
    public static PublisherRegistration Default { get; } = FromType(typeof(ForeachAwaitPublisher), ServiceLifetime.Singleton);

    /// <summary>
    /// Gets the publisher type, when the publisher is resolved by type.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type? Type { get; private init; }

    /// <summary>
    /// Gets the publisher instance, when one was supplied.
    /// </summary>
    public INotificationPublisher? Instance { get; private init; }

    /// <summary>
    /// Gets the publisher lifetime.
    /// </summary>
    public ServiceLifetime Lifetime { get; private init; }

    /// <summary>
    /// Creates a registration for a publisher resolved by type.
    /// </summary>
    /// <param name="type">The publisher type.</param>
    /// <param name="lifetime">The publisher lifetime.</param>
    /// <returns>The publisher registration.</returns>
    public static PublisherRegistration FromType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type,
        ServiceLifetime lifetime)
    {
        return new PublisherRegistration { Type = type, Lifetime = lifetime };
    }

    /// <summary>
    /// Creates a registration for a publisher instance.
    /// </summary>
    /// <param name="instance">The publisher instance.</param>
    /// <returns>The publisher registration.</returns>
    public static PublisherRegistration FromInstance(INotificationPublisher instance)
    {
        return new PublisherRegistration { Instance = instance, Lifetime = ServiceLifetime.Singleton };
    }

    /// <summary>
    /// Creates the service descriptor for this publisher.
    /// </summary>
    /// <returns>The service descriptor.</returns>
    public ServiceDescriptor ToDescriptor()
    {
        return Instance is not null
            ? ServiceDescriptor.Singleton(Instance)
            : ServiceDescriptor.Describe(typeof(INotificationPublisher), Type!, Lifetime);
    }

    /// <summary>
    /// Determines whether two registrations configure the same publisher.
    /// </summary>
    /// <param name="other">The registration to compare with.</param>
    /// <returns><see langword="true"/> when both registrations configure the same publisher.</returns>
    public bool IsSameAs(PublisherRegistration other)
    {
        return Type == other.Type && ReferenceEquals(Instance, other.Instance) && Lifetime == other.Lifetime;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Instance is not null
            ? $"instance of {Instance.GetType().Name}"
            : $"{Type!.Name} ({Lifetime})";
    }
}