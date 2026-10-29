using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator.DependencyInjection;

/// <summary>
/// Defines which interfaces are mediator components and which component shapes the mediator can never run.
/// </summary>
internal static class MediatorComponentRules
{
    /// <summary>
    /// Gets the component interfaces that may have many implementations per closed service type.
    /// </summary>
    public static readonly Type[] MultiRegistrationServiceTypeDefinitions =
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

    /// <summary>
    /// Gets the handler interfaces that must have exactly one implementation per closed service type.
    /// </summary>
    public static readonly Type[] SingleRegistrationServiceTypeDefinitions =
    [
        typeof(IRequestHandler<,>),
        typeof(IRequestHandler<>),
        typeof(IStreamRequestHandler<,>)
    ];

    /// <summary>
    /// Determines whether a generic type definition is a mediator component interface.
    /// </summary>
    /// <param name="serviceTypeDefinition">The generic interface definition.</param>
    /// <returns><see langword="true"/> when the interface is a mediator component.</returns>
    public static bool IsComponent(Type serviceTypeDefinition)
    {
        return IsSingleRegistration(serviceTypeDefinition) || MultiRegistrationServiceTypeDefinitions.Contains(serviceTypeDefinition);
    }

    /// <summary>
    /// Determines whether a generic type definition is a handler interface that allows only one implementation.
    /// </summary>
    /// <param name="serviceTypeDefinition">The generic interface definition.</param>
    /// <returns><see langword="true"/> when the interface allows only one implementation per closed type.</returns>
    public static bool IsSingleRegistration(Type serviceTypeDefinition)
    {
        return SingleRegistrationServiceTypeDefinitions.Contains(serviceTypeDefinition);
    }

    /// <summary>
    /// Determines whether the container can close an open generic implementation for an implemented interface.
    /// </summary>
    /// <remarks>
    /// Microsoft.Extensions.DependencyInjection closes an open generic implementation by substituting the service type's
    /// type arguments for the implementation's type parameters positionally, so they must match one-to-one and in order.
    /// </remarks>
    /// <param name="implementationTypeDefinition">The open generic implementation type definition.</param>
    /// <param name="implementedInterface">The implemented interface, expressed in the implementation's type parameters.</param>
    /// <returns><see langword="true"/> when the container can close the implementation for any service type.</returns>
    public static bool CanContainerCloseOpenGeneric(Type implementationTypeDefinition, Type implementedInterface)
    {
        return implementedInterface.GetGenericArguments().SequenceEqual(implementationTypeDefinition.GetGenericArguments());
    }

    /// <summary>
    /// Describes why a closed component can never run, or returns <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// The mediator resolves components for a message's concrete runtime type, and the container does not apply variance,
    /// so a component closed over an interface or abstract message type registers without error and is then silently
    /// skipped. A void request handled through <c>IRequestHandler&lt;TRequest, Unit&gt;</c> is skipped the same way,
    /// because void requests are only dispatched to <c>IRequestHandler&lt;TRequest&gt;</c>.
    /// </remarks>
    /// <param name="implementationType">The component implementation type.</param>
    /// <param name="serviceType">The closed component interface.</param>
    /// <returns>A description of the problem, or <see langword="null"/>.</returns>
    public static string? GetUnreachableComponentProblem(Type implementationType, Type serviceType)
    {
        var serviceTypeDefinition = serviceType.GetGenericTypeDefinition();
        var messageType = serviceType.GetGenericArguments()[0];
        var implementation = $"{implementationType.Namespace}.{FormatTypeName(implementationType)} implements {FormatTypeName(serviceType)}";

        if (messageType.IsInterface || messageType.IsAbstract)
        {
            var kind = messageType.IsInterface ? "an interface" : "an abstract class";

            if (IsSingleRegistration(serviceTypeDefinition))
            {
                return $"{implementation}, but {FormatTypeName(messageType)} is {kind}. Requests are dispatched by their concrete type, so this handler never runs. Implement the handler interface for each concrete request type.";
            }

            var parameters = serviceTypeDefinition.GetGenericArguments();
            var parameterList = string.Join(", ", parameters.Select(static parameter => parameter.Name));
            var componentName = FormatTypeName(implementationType);
            var arityIndex = componentName.IndexOf('<');
            componentName = arityIndex < 0 ? componentName : componentName[..arityIndex];
            var interfaceName = FormatTypeName(serviceTypeDefinition);
            interfaceName = interfaceName[..interfaceName.IndexOf('<')];

            return $"{implementation}, but {FormatTypeName(messageType)} is {kind}. Components are resolved for a message's concrete type, so this component never runs. " +
                $"Declare it as an open generic constrained to {FormatTypeName(messageType)} instead, for example " +
                $"{componentName}<{parameterList}> : {interfaceName}<{parameterList}> where {parameters[0].Name} : {FormatTypeName(messageType)}.";
        }

        if (serviceTypeDefinition == typeof(IRequestHandler<,>)
            && serviceType.GetGenericArguments()[1] == typeof(Unit)
            && typeof(IRequest).IsAssignableFrom(messageType))
        {
            return $"{implementation}, but {FormatTypeName(messageType)} implements IRequest, so it is only dispatched to IRequestHandler<{FormatTypeName(messageType)}>. Implement IRequestHandler<{FormatTypeName(messageType)}> instead.";
        }

        return null;
    }

    /// <summary>
    /// Formats a type name using C#-style generic argument syntax.
    /// </summary>
    /// <param name="type">The type to format.</param>
    /// <returns>The formatted type name.</returns>
    public static string FormatTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var arityIndex = type.Name.IndexOf('`');
        var name = arityIndex < 0 ? type.Name : type.Name[..arityIndex];

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))}>";
    }
}