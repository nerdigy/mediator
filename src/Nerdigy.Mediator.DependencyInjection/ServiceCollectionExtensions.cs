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
    /// mediator is combined with scoped components. A call that fails this way leaves the service collection as it was. A
    /// handler registered in the service collection before <c>AddMediator</c> is kept, and an explicitly registered handler
    /// replaces a scanned one. An explicit registration that names a lifetime sets the lifetime of an existing registration
    /// of the same implementation and keeps its position.
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

        List<string> problems = [];
        var scannedComponents = MediatorServiceScanner.Scan(
            options.AssembliesToScan,
            options.IgnoreTypeLoadFailures,
            options.HandlerLifetime,
            problems);

        ServiceDescriptor[] descriptorsBefore = [.. services];
        var state = RegisterCoreServices(services, options);
        var handlersBefore = state.CopyHandlers();
        var registrar = new HandlerRegistrar(services, state);

        foreach (var registration in options.Registrations)
        {
            var descriptor = ServiceDescriptor.Describe(
                registration.ServiceType,
                registration.ImplementationType,
                registration.Lifetime ?? options.HandlerLifetime);
            var namesLifetime = registration.Lifetime is not null;

            if (registration.IsSingleRegistration)
            {
                registrar.AddHandler(descriptor, registration.ImplementationType, isExplicit: true, namesLifetime, problems);
            }
            else
            {
                if (namesLifetime)
                {
                    SetLifetime(services, descriptor);
                }

                services.TryAddEnumerable(descriptor);
            }
        }

        foreach (var component in scannedComponents)
        {
            var serviceType = component.Descriptor.ServiceType;
            var serviceTypeDefinition = serviceType.IsGenericTypeDefinition ? serviceType : serviceType.GetGenericTypeDefinition();

            if (MediatorComponentRules.IsSingleRegistration(serviceTypeDefinition))
            {
                registrar.AddHandler(component.Descriptor, component.ImplementationType, isExplicit: false, namesLifetime: false, problems);
            }
            else
            {
                services.TryAddEnumerable(component.Descriptor);
            }
        }

        AddLifetimeProblem(services.Except(descriptorsBefore).Concat(registrar.DuplicateDescriptors), state, options, problems);

        if (problems.Count > 0)
        {
            // Every change above is undone here, so a failed call can be corrected and repeated on the same collection.
            services.Clear();

            foreach (var descriptor in descriptorsBefore)
            {
                services.Add(descriptor);
            }

            state.RestoreHandlers(handlersBefore);

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
    /// Reports a singleton mediator combined with scoped components, which would fail when the first request is sent.
    /// </summary>
    /// <param name="applied">The descriptors this call added or replaced, and the duplicates it rejected.</param>
    /// <param name="state">The registration state, holding the mediator lifetime.</param>
    /// <param name="options">The configured options.</param>
    /// <param name="problems">Collects the lifetime problem.</param>
    private static void AddLifetimeProblem(
        IEnumerable<ServiceDescriptor> applied,
        MediatorRegistrationState state,
        MediatorOptions options,
        List<string> problems)
    {
        if (state.MediatorLifetime != ServiceLifetime.Singleton)
        {
            return;
        }

        var scoped = applied
            .Where(static descriptor => descriptor.Lifetime == ServiceLifetime.Scoped && descriptor.ServiceType != typeof(INotificationPublisher))
            .ToList();
        var namedScoped = options.Registrations
            .Where(registration => registration.Lifetime == ServiceLifetime.Scoped && scoped.Exists(descriptor => IsRegistrationOf(descriptor, registration)))
            .ToList();
        var scopedComponents = new List<string>();

        if (scoped.Exists(descriptor => !namedScoped.Exists(registration => IsRegistrationOf(descriptor, registration))))
        {
            scopedComponents.Add("HandlerLifetime is Scoped");
        }

        scopedComponents.AddRange(namedScoped.Select(static registration => $"{registration.ImplementationType.Name} is registered as Scoped"));

        if (state.Publisher.Lifetime == ServiceLifetime.Scoped)
        {
            scopedComponents.Add("the notification publisher is Scoped");
        }

        if (scopedComponents.Count > 0)
        {
            problems.Add(
                $"A singleton mediator resolves handlers from the root service provider, which cannot resolve scoped services, but {string.Join(", ", scopedComponents)}. " +
                "Use a Transient or Scoped MediatorLifetime, or register the components as Transient or Singleton.");
        }
    }

    /// <summary>
    /// Determines whether a descriptor is for the service and implementation of an explicit registration.
    /// </summary>
    /// <param name="descriptor">A non-keyed service descriptor.</param>
    /// <param name="registration">The explicit registration.</param>
    /// <returns><see langword="true"/> when both name the same service and implementation.</returns>
    private static bool IsRegistrationOf(ServiceDescriptor descriptor, ComponentRegistration registration)
    {
        return descriptor.ServiceType == registration.ServiceType && descriptor.ImplementationType == registration.ImplementationType;
    }

    /// <summary>
    /// Replaces, at the same position, any registration of a descriptor's service and implementation types.
    /// </summary>
    /// <remarks>
    /// Keyed, instance, and factory registrations have no <see cref="ServiceDescriptor.ImplementationType"/>, so they are left alone.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="descriptor">The descriptor that names the lifetime.</param>
    private static void SetLifetime(IServiceCollection services, ServiceDescriptor descriptor)
    {
        for (var index = 0; index < services.Count; index++)
        {
            var existing = services[index];

            if (existing.ServiceType == descriptor.ServiceType && existing.ImplementationType == descriptor.ImplementationType)
            {
                services[index] = descriptor;
            }
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
        /// Gets the descriptors rejected because their request already has a different handler.
        /// </summary>
        public List<ServiceDescriptor> DuplicateDescriptors { get; } = [];

        /// <summary>
        /// Adds a handler registration, or records a problem when the request already has a different handler.
        /// </summary>
        /// <param name="descriptor">The handler service descriptor.</param>
        /// <param name="implementationType">The handler implementation type.</param>
        /// <param name="isExplicit">Whether the handler was registered explicitly rather than found by scanning.</param>
        /// <param name="namesLifetime">Whether the registration names its own lifetime rather than using the default.</param>
        /// <param name="problems">Collects duplicate handler problems.</param>
        public void AddHandler(ServiceDescriptor descriptor, Type implementationType, bool isExplicit, bool namesLifetime, List<string> problems)
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

                    if (namesLifetime)
                    {
                        SetLifetime(services, descriptor);
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

                DuplicateDescriptors.Add(descriptor);
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