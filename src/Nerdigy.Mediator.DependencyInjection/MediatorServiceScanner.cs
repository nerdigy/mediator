using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nerdigy.Mediator.Contracts;

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

        List<string> unsupportedRegistrations = [];

        foreach (var assembly in assemblies.Distinct())
        {
            RegisterFromAssembly(services, assembly, serviceLifetime, unsupportedRegistrations);
        }

        if (unsupportedRegistrations.Count > 0)
        {
            throw new InvalidOperationException(
                "The following open-generic mediator components cannot be registered because the dependency injection container " +
                "can only close an open generic whose type parameters match the service interface's type arguments one-to-one and in the same order:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, unsupportedRegistrations.Select(static registration => $"  - {registration}")) +
                Environment.NewLine +
                "Declare the type parameters in the interface's order (for example, MyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>), " +
                "or close the type for a specific request (for example, MyBehavior : IPipelineBehavior<MyRequest, MyResponse>).");
        }
    }

    /// <summary>
    /// Determines whether the dependency injection container can close an open-generic implementation as the implemented service interface.
    /// </summary>
    /// <remarks>
    /// The container closes an open-generic implementation by passing the service's type arguments to the implementation positionally,
    /// so the implementation's type parameters must be exactly the implemented interface's type arguments, in the same order.
    /// </remarks>
    /// <param name="implementationTypeDefinition">The open-generic implementation type definition.</param>
    /// <param name="implementedInterface">An interface implemented by <paramref name="implementationTypeDefinition"/>.</param>
    /// <returns><see langword="true"/> when the container can close the mapping; otherwise, <see langword="false"/>.</returns>
    internal static bool CanContainerCloseOpenGeneric(Type implementationTypeDefinition, Type implementedInterface)
    {
        return implementedInterface.GetGenericArguments().SequenceEqual(implementationTypeDefinition.GetGenericArguments());
    }

    /// <summary>
    /// Registers mediator services from a single assembly.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="serviceLifetime">The service lifetime used for registrations.</param>
    /// <param name="unsupportedRegistrations">Collects descriptions of open-generic components the container cannot close.</param>
    private static void RegisterFromAssembly(
        IServiceCollection services,
        Assembly assembly,
        ServiceLifetime serviceLifetime,
        List<string> unsupportedRegistrations)
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

            RegisterImplementedServiceInterfaces(services, type, serviceLifetime, unsupportedRegistrations);
        }
    }

    /// <summary>
    /// Registers implemented mediator service interfaces for a concrete type.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="implementationType">The implementation type being inspected.</param>
    /// <param name="serviceLifetime">The service lifetime used for registrations.</param>
    /// <param name="unsupportedRegistrations">Collects descriptions of open-generic components the container cannot close.</param>
    private static void RegisterImplementedServiceInterfaces(
        IServiceCollection services,
        Type implementationType,
        ServiceLifetime serviceLifetime,
        List<string> unsupportedRegistrations)
    {
        var implementedInterfaces = implementationType.GetInterfaces();

        foreach (var implementedInterface in implementedInterfaces)
        {
            if (!implementedInterface.IsGenericType)
            {
                continue;
            }

            var serviceTypeDefinition = implementedInterface.GetGenericTypeDefinition();

            if (!MultiRegistrationServiceTypeDefinitions.Contains(serviceTypeDefinition) &&
                !SingleRegistrationServiceTypeDefinitions.Contains(serviceTypeDefinition))
            {
                continue;
            }

            var isOpenGenericRegistration = implementationType.IsGenericTypeDefinition;

            if (isOpenGenericRegistration && !CanContainerCloseOpenGeneric(implementationType, implementedInterface))
            {
                unsupportedRegistrations.Add(
                    $"{implementationType.Namespace}.{FormatTypeName(implementationType)} implements {FormatTypeName(implementedInterface)}");
                continue;
            }

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
    /// Formats a type name using C#-style generic argument syntax.
    /// </summary>
    /// <param name="type">The type to format.</param>
    /// <returns>The formatted type name, such as <c>IPipelineBehavior&lt;TRequest, String&gt;</c>.</returns>
    internal static string FormatTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var arityIndex = type.Name.IndexOf('`');
        var name = arityIndex < 0 ? type.Name : type.Name[..arityIndex];

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))}>";
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