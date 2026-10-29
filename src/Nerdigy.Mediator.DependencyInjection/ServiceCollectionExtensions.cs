using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nerdigy.Mediator;
using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Provides dependency injection registration extensions for the mediator runtime.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mediator runtime and the handlers and pipeline components configured in <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AddMediator</c> can be called more than once, for example once per module. The first call sets
    /// <see cref="MediatorOptions.MediatorLifetime"/> and the notification publisher; a later call that sets them to
    /// different values throws.
    /// </para>
    /// <para>
    /// Registration fails with an <see cref="InvalidOperationException"/> that lists every problem when a request has more
    /// than one handler, a component could never run, a scanned assembly has types that fail to load, or a singleton
    /// mediator is combined with scoped components. A handler registered in the service collection before
    /// <c>AddMediator</c> is kept, and an explicitly registered handler replaces a scanned one.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The registration options callback.</param>
    /// <returns>The same service collection.</returns>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Scanning only runs for assemblies added with MediatorOptions.RegisterServicesFromAssembly*, which are annotated with RequiresUnreferencedCode.")]
    public static IServiceCollection AddMediator(
        this IServiceCollection services,
        Action<MediatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MediatorOptions();
        configure(options);

        var state = RegisterCoreServices(services, options);
        ValidateLifetimes(state, options);

        var registrar = new HandlerRegistrar(services, state);
        List<string> problems = [];

        foreach (var registration in options.Registrations)
        {
            var descriptor = ServiceDescriptor.Describe(
                registration.ServiceType,
                registration.ImplementationType,
                registration.Lifetime ?? options.HandlerLifetime);

            if (registration.IsSingleRegistration)
            {
                registrar.AddHandler(descriptor, registration.ImplementationType, isExplicit: true, problems);
            }
            else
            {
                services.TryAddEnumerable(descriptor);
            }
        }

        if (options.AssembliesToScan.Count > 0)
        {
            var components = MediatorServiceScanner.Scan(
                options.AssembliesToScan,
                options.IgnoreTypeLoadFailures,
                options.HandlerLifetime,
                problems);

            foreach (var component in components)
            {
                var serviceType = component.Descriptor.ServiceType;
                var serviceTypeDefinition = serviceType.IsGenericTypeDefinition ? serviceType : serviceType.GetGenericTypeDefinition();

                if (MediatorComponentRules.IsSingleRegistration(serviceTypeDefinition))
                {
                    registrar.AddHandler(component.Descriptor, component.ImplementationType, isExplicit: false, problems);
                }
                else
                {
                    services.TryAddEnumerable(component.Descriptor);
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Mediator registration failed:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(static problem => $"  - {problem}")));
        }

        return services;
    }

    /// <summary>
    /// Registers the mediator runtime and scans the given assemblies for handlers and pipeline components.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The same service collection.</returns>
    [RequiresUnreferencedCode("Assembly scanning discovers handlers and pipeline components with reflection, which trimming can remove. In trimmed and Native AOT apps, use AddMediator(options => ...) with explicit registrations instead.")]
    public static IServiceCollection AddMediator(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        return services.AddMediator(options => options.RegisterServicesFromAssemblies(assemblies));
    }

    /// <summary>
    /// Registers the mediator runtime without any handlers, for handlers registered directly in the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddMediator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddMediator(static _ => { });
    }

    /// <summary>
    /// Registers the mediator interfaces and notification publisher, or checks that an earlier call registered the same ones.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The configured options.</param>
    /// <returns>The registration state shared by every <c>AddMediator</c> call on the collection.</returns>
    private static MediatorRegistrationState RegisterCoreServices(IServiceCollection services, MediatorOptions options)
    {
        var state = MediatorRegistrationState.Find(services);

        if (state is not null)
        {
            if (options.MediatorLifetimeConfigured && options.MediatorLifetime != state.MediatorLifetime)
            {
                throw new InvalidOperationException(
                    $"AddMediator was already called with MediatorLifetime {state.MediatorLifetime}, so a later call cannot change it to {options.MediatorLifetime}. Set the mediator lifetime in the first AddMediator call only.");
            }

            if (options.PublisherConfigured && !options.Publisher.IsSameAs(state.Publisher))
            {
                throw new InvalidOperationException(
                    $"AddMediator was already called with notification publisher {state.Publisher}, so a later call cannot change it to {options.Publisher}. Configure the publisher in the first AddMediator call only.");
            }

            return state;
        }

        state = new MediatorRegistrationState(options.MediatorLifetime, options.Publisher);
        services.AddSingleton(state);

        services.TryAdd(ServiceDescriptor.Describe(
            typeof(IMediator),
            static provider => new Mediator(provider, provider.GetRequiredService<INotificationPublisher>()),
            options.MediatorLifetime));
        services.TryAdd(ServiceDescriptor.Describe(typeof(ISender), static provider => provider.GetRequiredService<IMediator>(), options.MediatorLifetime));
        services.TryAdd(ServiceDescriptor.Describe(typeof(IPublisher), static provider => provider.GetRequiredService<IMediator>(), options.MediatorLifetime));
        services.TryAdd(options.Publisher.ToDescriptor());

        return state;
    }

    /// <summary>
    /// Rejects a singleton mediator combined with scoped components, which would fail when the first request is sent.
    /// </summary>
    /// <param name="state">The registration state, holding the mediator lifetime.</param>
    /// <param name="options">The configured options.</param>
    private static void ValidateLifetimes(MediatorRegistrationState state, MediatorOptions options)
    {
        if (state.MediatorLifetime != ServiceLifetime.Singleton)
        {
            return;
        }

        var scopedComponents = new List<string>();

        if (options.HandlerLifetime == ServiceLifetime.Scoped
            && (options.AssembliesToScan.Count > 0 || options.Registrations.Any(static registration => registration.Lifetime is null)))
        {
            scopedComponents.Add("HandlerLifetime is Scoped");
        }

        scopedComponents.AddRange(options.Registrations
            .Where(static registration => registration.Lifetime == ServiceLifetime.Scoped)
            .Select(static registration => $"{registration.ImplementationType.Name} is registered as Scoped"));

        if (state.Publisher.Lifetime == ServiceLifetime.Scoped)
        {
            scopedComponents.Add("the notification publisher is Scoped");
        }

        if (scopedComponents.Count > 0)
        {
            throw new InvalidOperationException(
                $"A singleton mediator resolves handlers from the root service provider, which cannot resolve scoped services, but {string.Join(", ", scopedComponents)}. " +
                "Use a Transient or Scoped MediatorLifetime, or register the components as Transient or Singleton.");
        }
    }

    /// <summary>
    /// Adds handler registrations, keeping exactly one handler per request type.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="state">The registration state shared by every <c>AddMediator</c> call.</param>
    private sealed class HandlerRegistrar(IServiceCollection services, MediatorRegistrationState state)
    {
        private readonly HashSet<Type> _existingServiceTypes = [.. services.Where(static descriptor => !descriptor.IsKeyedService).Select(static descriptor => descriptor.ServiceType)];

        /// <summary>
        /// Adds a handler registration, or records a problem when the request already has a different handler.
        /// </summary>
        /// <param name="descriptor">The handler service descriptor.</param>
        /// <param name="implementationType">The handler implementation type.</param>
        /// <param name="isExplicit">Whether the handler was registered explicitly rather than found by scanning.</param>
        /// <param name="problems">Collects duplicate handler problems.</param>
        public void AddHandler(ServiceDescriptor descriptor, Type implementationType, bool isExplicit, List<string> problems)
        {
            var serviceType = descriptor.ServiceType;

            if (state.TryGetHandler(serviceType, out var existing))
            {
                if (existing.ImplementationType == implementationType)
                {
                    if (isExplicit && !existing.IsExplicit)
                    {
                        state.SetHandler(serviceType, new(implementationType, IsExplicit: true));
                    }

                    return;
                }

                if (isExplicit && !existing.IsExplicit)
                {
                    // An explicit registration is a deliberate override of a handler an earlier call found by scanning.
                    services.Replace(descriptor);
                    state.SetHandler(serviceType, new(implementationType, IsExplicit: true));

                    return;
                }

                if (!isExplicit && existing.IsExplicit)
                {
                    return;
                }

                problems.Add(
                    $"{MediatorComponentRules.FormatTypeName(serviceType)} has more than one handler: {existing.ImplementationType.FullName} and {implementationType.FullName}. " +
                    (isExplicit
                        ? "Register only one of them."
                        : "Keep one, or register the one you want explicitly (for example, with options.AddRequestHandler) to override the scanned one."));

                return;
            }

            if (_existingServiceTypes.Contains(serviceType))
            {
                // Registered directly in the service collection before AddMediator: that registration wins.
                return;
            }

            services.Add(descriptor);
            _existingServiceTypes.Add(serviceType);
            state.SetHandler(serviceType, new(implementationType, isExplicit));
        }
    }
}