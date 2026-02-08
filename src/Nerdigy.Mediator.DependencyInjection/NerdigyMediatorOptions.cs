using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Nerdigy.Mediator;
using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Represents configuration options for <c>AddMediator</c> registration.
/// </summary>
public sealed class NerdigyMediatorOptions
{
    private readonly HashSet<Assembly> _assembliesToScan = [];
    private readonly List<Type> _openBehaviorTypes = [];

    /// <summary>
    /// Gets the assemblies configured for service scanning.
    /// </summary>
    public IReadOnlyCollection<Assembly> AssembliesToScan => _assembliesToScan;

    /// <summary>
    /// Gets open-generic request pipeline behaviors configured explicitly in registration order.
    /// </summary>
    internal IReadOnlyList<Type> OpenBehaviorTypes => _openBehaviorTypes;

    /// <summary>
    /// Gets or sets the service lifetime used for mediator interface registrations.
    /// </summary>
    public ServiceLifetime MediatorLifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Gets or sets the service lifetime used for scanned handler and pipeline component registrations.
    /// </summary>
    public ServiceLifetime HandlerLifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Gets the configured notification publisher type.
    /// </summary>
    internal Type NotificationPublisherType { get; private set; } = typeof(ForeachAwaitPublisher);

    /// <summary>
    /// Gets the configured notification publisher instance, when explicitly provided.
    /// </summary>
    internal INotificationPublisher? NotificationPublisherInstance { get; private set; }

    /// <summary>
    /// Registers a single assembly for mediator service scanning.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The current options instance.</returns>
    public NerdigyMediatorOptions RegisterServicesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _assembliesToScan.Add(assembly);

        return this;
    }

    /// <summary>
    /// Registers multiple assemblies for mediator service scanning.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The current options instance.</returns>
    public NerdigyMediatorOptions RegisterServicesFromAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies)
        {
            RegisterServicesFromAssembly(assembly);
        }

        return this;
    }

    /// <summary>
    /// Registers the assembly containing <typeparamref name="TMarker"/> for service scanning.
    /// </summary>
    /// <typeparam name="TMarker">A marker type in the target assembly.</typeparam>
    /// <returns>The current options instance.</returns>
    public NerdigyMediatorOptions RegisterServicesFromAssemblyContaining<TMarker>()
    {
        return RegisterServicesFromAssembly(typeof(TMarker).Assembly);
    }

    /// <summary>
    /// Registers an open-generic request pipeline behavior in explicit execution order.
    /// </summary>
    /// <param name="openBehaviorType">The open-generic behavior type.</param>
    /// <returns>The current options instance.</returns>
    public NerdigyMediatorOptions AddOpenBehavior(Type openBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        if (!openBehaviorType.IsClass || openBehaviorType.IsAbstract)
        {
            throw new ArgumentException(
                $"Open behavior type '{openBehaviorType}' must be a concrete class.",
                nameof(openBehaviorType));
        }

        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Open behavior type '{openBehaviorType}' must be an open generic type definition (for example, MyBehavior<,>).",
                nameof(openBehaviorType));
        }

        var implementsPipelineBehavior = openBehaviorType
            .GetInterfaces()
            .Any(static interfaceType =>
                interfaceType.IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

        if (!implementsPipelineBehavior)
        {
            throw new ArgumentException(
                $"Open behavior type '{openBehaviorType}' must implement IPipelineBehavior<,>.",
                nameof(openBehaviorType));
        }

        if (_openBehaviorTypes.Contains(openBehaviorType))
        {
            return this;
        }

        _openBehaviorTypes.Add(openBehaviorType);

        return this;
    }

    /// <summary>
    /// Configures the notification publisher using a built-in strategy.
    /// </summary>
    /// <param name="strategy">The notification publisher strategy.</param>
    /// <returns>The current options instance.</returns>
    public NerdigyMediatorOptions UseNotificationPublisherStrategy(NerdigyMediatorNotificationPublisherStrategy strategy)
    {
        NotificationPublisherInstance = null;

        NotificationPublisherType = strategy switch
        {
            NerdigyMediatorNotificationPublisherStrategy.Sequential => typeof(ForeachAwaitPublisher),
            NerdigyMediatorNotificationPublisherStrategy.Parallel => typeof(TaskWhenAllPublisher),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported notification publisher strategy.")
        };

        return this;
    }

    /// <summary>
    /// Configures the notification publisher to a custom publisher type.
    /// </summary>
    /// <typeparam name="TPublisher">The notification publisher type.</typeparam>
    /// <returns>The current options instance.</returns>
    public NerdigyMediatorOptions UseNotificationPublisher<TPublisher>()
        where TPublisher : class, INotificationPublisher
    {
        NotificationPublisherInstance = null;
        NotificationPublisherType = typeof(TPublisher);

        return this;
    }

    /// <summary>
    /// Configures the notification publisher to a specific publisher instance.
    /// </summary>
    /// <param name="publisher">The notification publisher instance.</param>
    /// <returns>The current options instance.</returns>
    public NerdigyMediatorOptions UseNotificationPublisher(INotificationPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        NotificationPublisherInstance = publisher;
        NotificationPublisherType = publisher.GetType();

        return this;
    }
}
