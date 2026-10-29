using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nerdigy.Mediator;

/// <summary>
/// Creates closed generic invokers for types that are only known at runtime.
/// </summary>
internal static class GenericInvokerFactory
{
    /// <summary>
    /// Creates an invoker for a message type that was not registered through <see cref="MediatorRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Without runtime code generation the closed invoker may have no native code, which would fail on first use with a
    /// message that does not explain the fix. This method fails up front instead and names the registration to add.
    /// </remarks>
    /// <typeparam name="TInvoker">The invoker base type.</typeparam>
    /// <param name="invokerTypeDefinition">The open generic invoker type definition.</param>
    /// <param name="messageType">The request, stream request, or notification type being dispatched.</param>
    /// <param name="registration">The registration call that would make the message type dispatchable.</param>
    /// <param name="typeArguments">The invoker type arguments.</param>
    /// <returns>The created invoker.</returns>
    /// <exception cref="InvalidOperationException">Thrown when runtime code generation is not available.</exception>
    public static TInvoker CreateForMessage<TInvoker>(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type invokerTypeDefinition,
        Type messageType,
        string registration,
        params Type[] typeArguments)
        where TInvoker : class
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException(MediatorErrors.UnregisteredMessageType(messageType, registration));
        }

        return Create<TInvoker>(invokerTypeDefinition, typeArguments);
    }

    /// <summary>
    /// Creates a closed generic invoker.
    /// </summary>
    /// <remarks>
    /// The annotation on <paramref name="invokerTypeDefinition"/> keeps the invoker constructors. Exception invokers are created
    /// for exception types, which are reference types, so they share the canonical code that the statically referenced
    /// <see cref="Exception"/> instantiation already compiles, including under Native AOT.
    /// </remarks>
    /// <typeparam name="TInvoker">The invoker base type.</typeparam>
    /// <param name="invokerTypeDefinition">The open generic invoker type definition.</param>
    /// <param name="typeArguments">The invoker type arguments.</param>
    /// <returns>The created invoker.</returns>
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Message invokers are only created here when dynamic code is supported. Exception invokers vary only by a reference-type exception argument, so they share canonical code that is compiled ahead of time.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2055:UnrecognizedReflectionPattern",
        Justification = "The invoker types' generic parameters have no DynamicallyAccessedMembers requirements.")]
    public static TInvoker Create<TInvoker>(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type invokerTypeDefinition,
        params Type[] typeArguments)
        where TInvoker : class
    {
        var invokerType = invokerTypeDefinition.MakeGenericType(typeArguments);

        return (TInvoker)Activator.CreateInstance(invokerType)!;
    }
}