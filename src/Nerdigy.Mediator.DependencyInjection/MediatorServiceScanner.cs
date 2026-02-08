using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Scans assemblies and registers mediator handlers and pipeline components.
/// </summary>
internal static class MediatorServiceScanner
{
    private static readonly Type[] MultiRegistrationServiceTypeDefinitions =
    [
        typeof(INotificationHandler<>),
        typeof(IPipelineBehavior<,>),
        typeof(IStreamPipelineBehavior<,>),
        typeof(IRequestPreProcessor<>),
        typeof(IRequestPostProcessor<,>),
        typeof(IRequestExceptionHandler<,,>),
        typeof(IRequestExceptionAction<,>),
        typeof(IStreamRequestExceptionHandler<,,>)
    ];

    private static readonly Type[] SingleRegistrationServiceTypeDefinitions =
    [
        typeof(IRequestHandler<,>),
        typeof(IRequestHandler<>),
        typeof(IStreamRequestHandler<,>)
    ];

    /// <summary>
    /// Registers mediator services from the configured assemblies.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <param name="serviceLifetime">The service lifetime used for registrations.</param>
    public static void RegisterFromAssemblies(
        IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        ServiceLifetime serviceLifetime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies.Distinct())
        {
            RegisterFromAssembly(services, assembly, serviceLifetime);
        }
    }

    /// <summary>
    /// Registers mediator services from a single assembly.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="serviceLifetime">The service lifetime used for registrations.</param>
    private static void RegisterFromAssembly(IServiceCollection services, Assembly assembly, ServiceLifetime serviceLifetime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in GetLoadableTypes(assembly))
        {
            if (!type.IsClass || type.IsAbstract)
            {
                continue;
            }

            if (type.ContainsGenericParameters && !type.IsGenericTypeDefinition)
            {
                continue;
            }

            RegisterImplementedServiceInterfaces(services, type, serviceLifetime);
        }
    }

    /// <summary>
    /// Registers implemented mediator service interfaces for a concrete type.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="implementationType">The implementation type being inspected.</param>
    /// <param name="serviceLifetime">The service lifetime used for registrations.</param>
    private static void RegisterImplementedServiceInterfaces(
        IServiceCollection services,
        Type implementationType,
        ServiceLifetime serviceLifetime)
    {
        var implementedInterfaces = implementationType.GetInterfaces();

        foreach (var implementedInterface in implementedInterfaces)
        {
            if (!implementedInterface.IsGenericType)
            {
                continue;
            }

            var serviceTypeDefinition = implementedInterface.GetGenericTypeDefinition();
            var isOpenGenericRegistration = implementationType.IsGenericTypeDefinition;
            var serviceType = isOpenGenericRegistration
                ? serviceTypeDefinition
                : implementedInterface;

            if (MultiRegistrationServiceTypeDefinitions.Contains(serviceTypeDefinition))
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Describe(serviceType, implementationType, serviceLifetime));
                continue;
            }

            if (SingleRegistrationServiceTypeDefinitions.Contains(serviceTypeDefinition))
            {
                services.TryAdd(
                    ServiceDescriptor.Describe(serviceType, implementationType, serviceLifetime));
            }
        }
    }

    /// <summary>
    /// Returns all loadable types from an assembly, excluding unloadable types.
    /// </summary>
    /// <param name="assembly">The assembly to inspect.</param>
    /// <returns>A sequence of loadable types.</returns>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(static type => type is not null).Select(static type => type!);
        }
    }
}