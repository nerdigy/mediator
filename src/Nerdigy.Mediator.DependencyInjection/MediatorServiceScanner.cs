using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Discovers mediator components in assemblies.
/// </summary>
internal static class MediatorServiceScanner
{
    /// <summary>
    /// Discovers the mediator components implemented by concrete classes in the given assemblies.
    /// </summary>
    /// <remarks>
    /// Assemblies are scanned in the order given, and types within an assembly in order of full name, so the order of
    /// scanned behaviors does not depend on how the compiler laid out the assembly.
    /// </remarks>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <param name="ignoreTypeLoadFailures">Whether to scan the types that loaded when some types fail to load.</param>
    /// <param name="lifetime">The lifetime to register discovered components with.</param>
    /// <param name="problems">Collects components that cannot be registered or would never run.</param>
    /// <returns>The discovered components in registration order.</returns>
    [RequiresUnreferencedCode("Assembly scanning discovers components with reflection, which trimming can remove.")]
    public static List<ScannedComponent> Scan(
        IEnumerable<Assembly> assemblies,
        bool ignoreTypeLoadFailures,
        ServiceLifetime lifetime,
        List<string> problems)
    {
        List<ScannedComponent> components = [];

        foreach (var assembly in assemblies)
        {
            var types = GetLoadableTypes(assembly, ignoreTypeLoadFailures, problems)
                .Where(static type => type.IsClass && !type.IsAbstract && (!type.ContainsGenericParameters || type.IsGenericTypeDefinition))
                .OrderBy(static type => type.FullName, StringComparer.Ordinal);

            foreach (var type in types)
            {
                AddImplementedComponents(type, lifetime, components, problems);
            }
        }

        return components;
    }

    /// <summary>
    /// Adds the mediator components a type implements.
    /// </summary>
    /// <param name="implementationType">The concrete implementation type.</param>
    /// <param name="lifetime">The lifetime to register discovered components with.</param>
    /// <param name="components">The discovered components.</param>
    /// <param name="problems">Collects components that cannot be registered or would never run.</param>
    [RequiresUnreferencedCode("Assembly scanning discovers components with reflection, which trimming can remove.")]
    private static void AddImplementedComponents(
        Type implementationType,
        ServiceLifetime lifetime,
        List<ScannedComponent> components,
        List<string> problems)
    {
        foreach (var implementedInterface in implementationType.GetInterfaces())
        {
            if (!implementedInterface.IsGenericType)
            {
                continue;
            }

            var serviceTypeDefinition = implementedInterface.GetGenericTypeDefinition();

            if (!MediatorComponentRules.IsComponent(serviceTypeDefinition))
            {
                continue;
            }

            if (implementationType.IsGenericTypeDefinition)
            {
                if (!MediatorComponentRules.CanContainerCloseOpenGeneric(implementationType, implementedInterface))
                {
                    problems.Add(
                        $"{implementationType.Namespace}.{MediatorComponentRules.FormatTypeName(implementationType)} implements {MediatorComponentRules.FormatTypeName(implementedInterface)}, " +
                        "but the dependency injection container can only close an open generic whose type parameters match the service interface's type arguments one-to-one and in the same order. " +
                        "Declare the type parameters in the interface's order (for example, MyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>), " +
                        "or close the type for a specific request (for example, MyBehavior : IPipelineBehavior<MyRequest, MyResponse>).");

                    continue;
                }

                components.Add(new ScannedComponent(ServiceDescriptor.Describe(serviceTypeDefinition, implementationType, lifetime), implementationType));

                continue;
            }

            var problem = MediatorComponentRules.GetUnreachableComponentProblem(implementationType, implementedInterface);

            if (problem is not null)
            {
                problems.Add(problem);

                continue;
            }

            components.Add(new ScannedComponent(ServiceDescriptor.Describe(implementedInterface, implementationType, lifetime), implementationType));
        }
    }

    /// <summary>
    /// Gets the types defined in an assembly, reporting types that fail to load.
    /// </summary>
    /// <param name="assembly">The assembly to inspect.</param>
    /// <param name="ignoreTypeLoadFailures">Whether to return the types that loaded when some types fail to load.</param>
    /// <param name="problems">Collects the load failure when it is not ignored.</param>
    /// <returns>The types that loaded.</returns>
    [RequiresUnreferencedCode("Assembly scanning discovers components with reflection, which trimming can remove.")]
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly, bool ignoreTypeLoadFailures, List<string> problems)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            if (!ignoreTypeLoadFailures)
            {
                var loaderErrors = exception.LoaderExceptions
                    .Where(static loaderException => loaderException is not null)
                    .Select(static loaderException => loaderException!.Message)
                    .Distinct()
                    .ToArray();
                var details = loaderErrors.Length == 0 ? exception.Message : string.Join(" ", loaderErrors);

                problems.Add(
                    $"Some types in assembly '{assembly.FullName}' could not be loaded, so handlers in them would be missing: {details} " +
                    "Fix the missing dependency, or set options.IgnoreTypeLoadFailures = true to scan only the types that loaded.");
            }

            return exception.Types.Where(static type => type is not null).Select(static type => type!);
        }
    }

    /// <summary>
    /// Represents a discovered mediator component.
    /// </summary>
    /// <param name="Descriptor">The service descriptor for the component.</param>
    /// <param name="ImplementationType">The implementation type.</param>
    internal readonly record struct ScannedComponent(ServiceDescriptor Descriptor, Type ImplementationType);
}