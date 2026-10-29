using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator;
using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Represents configuration options for <c>AddMediator</c> registration.
/// </summary>
/// <remarks>
/// <para>
/// Components can be discovered by scanning assemblies, registered explicitly with the <c>Add</c> methods, or both.
/// Explicit registration is required for trimmed and Native AOT apps, because it also registers each message type's
/// dispatch code with <see cref="MediatorRegistry"/>.
/// </para>
/// <para>
/// Pipeline behaviors run in registration order: explicit and open behaviors in the order they were added, then scanned
/// behaviors sorted by full type name. An explicitly registered handler replaces a scanned handler for the same request.
/// </para>
/// </remarks>
public sealed class MediatorOptions
{
    private const string ScanningRequiresUnreferencedCode =
        "Assembly scanning discovers handlers and pipeline components with reflection, which trimming can remove. " +
        "In trimmed and Native AOT apps, register them with AddRequestHandler, AddNotificationHandler, and the other Add methods instead.";

    private readonly List<Assembly> _assembliesToScan = [];
    private readonly List<ComponentRegistration> _registrations = [];
    private ServiceLifetime _mediatorLifetime = ServiceLifetime.Transient;
    private PublisherRegistration _publisher = PublisherRegistration.Default;

    /// <summary>
    /// Gets the assemblies configured for service scanning, in the order they were added.
    /// </summary>
    public IReadOnlyCollection<Assembly> AssembliesToScan => _assembliesToScan;

    /// <summary>
    /// Gets or sets the service lifetime used for the <see cref="IMediator"/>, <see cref="ISender"/>, and
    /// <see cref="IPublisher"/> registrations. Defaults to <see cref="ServiceLifetime.Transient"/>.
    /// </summary>
    /// <remarks>
    /// A singleton mediator resolves every handler from the root provider, so it cannot be combined with scoped handlers.
    /// </remarks>
    public ServiceLifetime MediatorLifetime
    {
        get => _mediatorLifetime;
        set
        {
            _mediatorLifetime = value;
            MediatorLifetimeConfigured = true;
        }
    }

    /// <summary>
    /// Gets or sets the default service lifetime for handler and pipeline component registrations.
    /// Defaults to <see cref="ServiceLifetime.Transient"/>.
    /// </summary>
    public ServiceLifetime HandlerLifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Gets or sets a value indicating whether scanning continues past types that fail to load.
    /// </summary>
    /// <remarks>
    /// By default a <see cref="ReflectionTypeLoadException"/> fails <c>AddMediator</c> with the assembly name and the
    /// loader errors, because handlers in the types that did not load would otherwise be silently missing.
    /// </remarks>
    public bool IgnoreTypeLoadFailures { get; set; }

    /// <summary>
    /// Gets the explicit component registrations in the order they were added.
    /// </summary>
    internal IReadOnlyList<ComponentRegistration> Registrations => _registrations;

    /// <summary>
    /// Gets the configured notification publisher.
    /// </summary>
    internal PublisherRegistration Publisher => _publisher;

    /// <summary>
    /// Gets a value indicating whether <see cref="MediatorLifetime"/> was set explicitly.
    /// </summary>
    internal bool MediatorLifetimeConfigured { get; private set; }

    /// <summary>
    /// Gets a value indicating whether a notification publisher was configured explicitly.
    /// </summary>
    internal bool PublisherConfigured { get; private set; }

    /// <summary>
    /// Registers a single assembly for mediator service scanning.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The current options instance.</returns>
    [RequiresUnreferencedCode(ScanningRequiresUnreferencedCode)]
    public MediatorOptions RegisterServicesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!_assembliesToScan.Contains(assembly))
        {
            _assembliesToScan.Add(assembly);
        }

        return this;
    }

    /// <summary>
    /// Registers multiple assemblies for mediator service scanning.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The current options instance.</returns>
    [RequiresUnreferencedCode(ScanningRequiresUnreferencedCode)]
    public MediatorOptions RegisterServicesFromAssemblies(params Assembly[] assemblies)
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
    /// <typeparam name="TMarker">A type from the assembly to scan.</typeparam>
    /// <returns>The current options instance.</returns>
    [RequiresUnreferencedCode(ScanningRequiresUnreferencedCode)]
    public MediatorOptions RegisterServicesFromAssemblyContaining<TMarker>()
    {
        return RegisterServicesFromAssembly(typeof(TMarker).Assembly);
    }

    /// <summary>
    /// Registers the handler for requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="lifetime">The handler lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddRequestHandler<
        TRequest,
        TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(ServiceLifetime? lifetime = null)
        where TRequest : IRequest<TResponse>
        where THandler : class, IRequestHandler<TRequest, TResponse>
    {
        AddComponent<IRequestHandler<TRequest, TResponse>, THandler>(lifetime);
        MediatorRegistry.RegisterRequest<TRequest, TResponse>();

        return this;
    }

    /// <summary>
    /// Registers the handler for void-style requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="lifetime">The handler lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddRequestHandler<
        TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(ServiceLifetime? lifetime = null)
        where TRequest : IRequest
        where THandler : class, IRequestHandler<TRequest>
    {
        AddComponent<IRequestHandler<TRequest>, THandler>(lifetime);
        MediatorRegistry.RegisterVoidRequest<TRequest>();

        return this;
    }

    /// <summary>
    /// Registers the handler for stream requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete stream request type.</typeparam>
    /// <typeparam name="TResponse">The streamed response payload type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="lifetime">The handler lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddStreamRequestHandler<
        TRequest,
        TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(ServiceLifetime? lifetime = null)
        where TRequest : IStreamRequest<TResponse>
        where THandler : class, IStreamRequestHandler<TRequest, TResponse>
    {
        AddComponent<IStreamRequestHandler<TRequest, TResponse>, THandler>(lifetime);
        MediatorRegistry.RegisterStreamRequest<TRequest, TResponse>();

        return this;
    }

    /// <summary>
    /// Registers a handler for notifications of type <typeparamref name="TNotification"/>.
    /// </summary>
    /// <typeparam name="TNotification">The concrete notification type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="lifetime">The handler lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddNotificationHandler<
        TNotification,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(ServiceLifetime? lifetime = null)
        where TNotification : INotification
        where THandler : class, INotificationHandler<TNotification>
    {
        AddComponent<INotificationHandler<TNotification>, THandler>(lifetime);
        MediatorRegistry.RegisterNotification<TNotification>();

        return this;
    }

    /// <summary>
    /// Registers a pipeline behavior for requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <typeparam name="TBehavior">The behavior type.</typeparam>
    /// <param name="lifetime">The behavior lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddBehavior<
        TRequest,
        TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior>(ServiceLifetime? lifetime = null)
        where TRequest : IRequest<TResponse>
        where TBehavior : class, IPipelineBehavior<TRequest, TResponse>
    {
        return AddComponent<IPipelineBehavior<TRequest, TResponse>, TBehavior>(lifetime);
    }

    /// <summary>
    /// Registers a stream pipeline behavior for stream requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete stream request type.</typeparam>
    /// <typeparam name="TResponse">The streamed response payload type.</typeparam>
    /// <typeparam name="TBehavior">The behavior type.</typeparam>
    /// <param name="lifetime">The behavior lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddStreamBehavior<
        TRequest,
        TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior>(ServiceLifetime? lifetime = null)
        where TRequest : IStreamRequest<TResponse>
        where TBehavior : class, IStreamPipelineBehavior<TRequest, TResponse>
    {
        return AddComponent<IStreamPipelineBehavior<TRequest, TResponse>, TBehavior>(lifetime);
    }

    /// <summary>
    /// Registers an open generic request pipeline behavior that runs for every request it can be closed for.
    /// </summary>
    /// <param name="openBehaviorType">The open generic behavior type, for example <c>typeof(LoggingBehavior&lt;,&gt;)</c>.</param>
    /// <param name="lifetime">The behavior lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddOpenBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime? lifetime = null)
    {
        return AddOpenComponent(openBehaviorType, typeof(IPipelineBehavior<,>), lifetime);
    }

    /// <summary>
    /// Registers an open generic stream pipeline behavior that runs for every stream request it can be closed for.
    /// </summary>
    /// <param name="openBehaviorType">The open generic behavior type, for example <c>typeof(StreamLoggingBehavior&lt;,&gt;)</c>.</param>
    /// <param name="lifetime">The behavior lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddOpenStreamBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        ServiceLifetime? lifetime = null)
    {
        return AddOpenComponent(openBehaviorType, typeof(IStreamPipelineBehavior<,>), lifetime);
    }

    /// <summary>
    /// Registers a preprocessor for requests and stream requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TProcessor">The preprocessor type.</typeparam>
    /// <param name="lifetime">The preprocessor lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddPreProcessor<
        TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(ServiceLifetime? lifetime = null)
        where TRequest : IBaseRequest
        where TProcessor : class, IRequestPreProcessor<TRequest>
    {
        return AddComponent<IRequestPreProcessor<TRequest>, TProcessor>(lifetime);
    }

    /// <summary>
    /// Registers a postprocessor for requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <typeparam name="TProcessor">The postprocessor type.</typeparam>
    /// <param name="lifetime">The postprocessor lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddPostProcessor<
        TRequest,
        TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(ServiceLifetime? lifetime = null)
        where TRequest : IRequest<TResponse>
        where TProcessor : class, IRequestPostProcessor<TRequest, TResponse>
    {
        return AddComponent<IRequestPostProcessor<TRequest, TResponse>, TProcessor>(lifetime);
    }

    /// <summary>
    /// Registers an exception handler for requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <typeparam name="TException">The exception type the handler handles, including derived exceptions.</typeparam>
    /// <typeparam name="THandler">The exception handler type.</typeparam>
    /// <param name="lifetime">The handler lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddExceptionHandler<
        TRequest,
        TResponse,
        TException,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(ServiceLifetime? lifetime = null)
        where TRequest : IRequest<TResponse>
        where TException : Exception
        where THandler : class, IRequestExceptionHandler<TRequest, TResponse, TException>
    {
        return AddComponent<IRequestExceptionHandler<TRequest, TResponse, TException>, THandler>(lifetime);
    }

    /// <summary>
    /// Registers an exception action for requests and stream requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TException">The exception type the action observes, including derived exceptions.</typeparam>
    /// <typeparam name="TAction">The exception action type.</typeparam>
    /// <param name="lifetime">The action lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddExceptionAction<
        TRequest,
        TException,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAction>(ServiceLifetime? lifetime = null)
        where TRequest : IBaseRequest
        where TException : Exception
        where TAction : class, IRequestExceptionAction<TRequest, TException>
    {
        return AddComponent<IRequestExceptionAction<TRequest, TException>, TAction>(lifetime);
    }

    /// <summary>
    /// Registers a stream exception handler for stream requests of type <typeparamref name="TRequest"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete stream request type.</typeparam>
    /// <typeparam name="TResponse">The streamed response payload type.</typeparam>
    /// <typeparam name="TException">The exception type the handler handles, including derived exceptions.</typeparam>
    /// <typeparam name="THandler">The stream exception handler type.</typeparam>
    /// <param name="lifetime">The handler lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions AddStreamExceptionHandler<
        TRequest,
        TResponse,
        TException,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(ServiceLifetime? lifetime = null)
        where TRequest : IStreamRequest<TResponse>
        where TException : Exception
        where THandler : class, IStreamRequestExceptionHandler<TRequest, TResponse, TException>
    {
        return AddComponent<IStreamRequestExceptionHandler<TRequest, TResponse, TException>, THandler>(lifetime);
    }

    /// <summary>
    /// Uses a built-in notification publisher strategy.
    /// </summary>
    /// <param name="strategy">The publisher strategy.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions UseNotificationPublishStrategy(NotificationPublishStrategy strategy)
    {
        _publisher = strategy switch
        {
            NotificationPublishStrategy.Sequential => PublisherRegistration.Default,
            NotificationPublishStrategy.Parallel => PublisherRegistration.FromType(typeof(TaskWhenAllPublisher), ServiceLifetime.Singleton),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported notification publish strategy.")
        };
        PublisherConfigured = true;

        return this;
    }

    /// <summary>
    /// Uses a custom notification publisher resolved from the container.
    /// </summary>
    /// <remarks>
    /// The publisher is registered as a singleton by default, so it must be safe to share across scopes and threads.
    /// A scoped publisher cannot be combined with a singleton mediator.
    /// </remarks>
    /// <typeparam name="TPublisher">The publisher type.</typeparam>
    /// <param name="lifetime">The publisher lifetime. Defaults to <see cref="ServiceLifetime.Singleton"/>.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions UseNotificationPublisher<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPublisher>(
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TPublisher : class, INotificationPublisher
    {
        _publisher = PublisherRegistration.FromType(typeof(TPublisher), lifetime);
        PublisherConfigured = true;

        return this;
    }

    /// <summary>
    /// Uses a notification publisher instance, registered as a singleton.
    /// </summary>
    /// <param name="publisher">The publisher instance.</param>
    /// <returns>The current options instance.</returns>
    public MediatorOptions UseNotificationPublisher(INotificationPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = PublisherRegistration.FromInstance(publisher);
        PublisherConfigured = true;

        return this;
    }

    /// <summary>
    /// Records an explicit closed component registration after checking that the mediator can run it.
    /// </summary>
    /// <typeparam name="TService">The closed component interface.</typeparam>
    /// <typeparam name="TImplementation">The component implementation type.</typeparam>
    /// <param name="lifetime">The component lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    private MediatorOptions AddComponent<
        TService,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(ServiceLifetime? lifetime)
        where TImplementation : class, TService
    {
        var problem = MediatorComponentRules.GetUnreachableComponentProblem(typeof(TImplementation), typeof(TService));

        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(TService));
        }

        _registrations.Add(new ComponentRegistration(typeof(TService), typeof(TImplementation), lifetime));

        return this;
    }

    /// <summary>
    /// Records an explicit open generic component registration after checking that the container can close it.
    /// </summary>
    /// <param name="openBehaviorType">The open generic implementation type.</param>
    /// <param name="serviceTypeDefinition">The component interface definition it must implement.</param>
    /// <param name="lifetime">The component lifetime, or <see langword="null"/> to use <see cref="HandlerLifetime"/>.</param>
    /// <returns>The current options instance.</returns>
    private MediatorOptions AddOpenComponent(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type openBehaviorType,
        Type serviceTypeDefinition,
        ServiceLifetime? lifetime)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        var interfaceName = MediatorComponentRules.FormatTypeName(serviceTypeDefinition);

        if (!openBehaviorType.IsClass || openBehaviorType.IsAbstract)
        {
            throw new ArgumentException($"Open behavior type '{openBehaviorType}' must be a concrete class.", nameof(openBehaviorType));
        }

        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Open behavior type '{openBehaviorType}' must be an open generic type definition (for example, MyBehavior<,>).",
                nameof(openBehaviorType));
        }

        var implementedInterface = Array.Find(
            openBehaviorType.GetInterfaces(),
            interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == serviceTypeDefinition);

        if (implementedInterface is null)
        {
            throw new ArgumentException($"Open behavior type '{openBehaviorType}' must implement {interfaceName}.", nameof(openBehaviorType));
        }

        if (!MediatorComponentRules.CanContainerCloseOpenGeneric(openBehaviorType, implementedInterface))
        {
            throw new ArgumentException(
                $"Open behavior type '{MediatorComponentRules.FormatTypeName(openBehaviorType)}' implements " +
                $"{MediatorComponentRules.FormatTypeName(implementedInterface)}, but its type parameters must match " +
                $"{interfaceName} one-to-one and in the same order (for example, MyBehavior<TRequest, TResponse> : {interfaceName}). " +
                "To target a specific request, close the type (for example, MyBehavior : IPipelineBehavior<MyRequest, MyResponse>) and register it with AddBehavior.",
                nameof(openBehaviorType));
        }

        if (!_registrations.Exists(registration => registration.ImplementationType == openBehaviorType))
        {
            _registrations.Add(new ComponentRegistration(serviceTypeDefinition, openBehaviorType, lifetime));
        }

        return this;
    }
}