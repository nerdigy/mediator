namespace Nerdigy.Mediator;

/// <summary>
/// Resolves the response type a request declares for a response view requested by the caller.
/// </summary>
internal static class DeclaredResponseResolver
{
    /// <summary>
    /// Resolves the response type the request type declares for the requested response view.
    /// </summary>
    /// <remarks>
    /// Request contracts are covariant, so a request that declares <c>IRequest&lt;string&gt;</c> can be sent through
    /// <c>IRequest&lt;object&gt;</c>. Handlers and pipeline components are registered against the declared response type,
    /// so dispatch must use it rather than the caller's view.
    /// </remarks>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <param name="contractDefinition">The open generic request contract, such as <c>IRequest&lt;&gt;</c>.</param>
    /// <param name="requestedResponseType">The response type requested by the caller.</param>
    /// <returns>
    /// The requested response type when the request declares it or no declared response type is compatible;
    /// otherwise the single declared response type that is covariant with the requested one.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when more than one declared response type is covariant with the requested response type.
    /// </exception>
    public static Type Resolve(Type requestType, Type contractDefinition, Type requestedResponseType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(contractDefinition);
        ArgumentNullException.ThrowIfNull(requestedResponseType);

        var requestedContract = contractDefinition.MakeGenericType(requestedResponseType);
        List<Type> compatibleResponseTypes = [];

        foreach (var contract in requestType.GetInterfaces())
        {
            if (!contract.IsGenericType || contract.GetGenericTypeDefinition() != contractDefinition)
            {
                continue;
            }

            if (contract == requestedContract)
            {
                return requestedResponseType;
            }

            if (requestedContract.IsAssignableFrom(contract))
            {
                compatibleResponseTypes.Add(contract.GetGenericArguments()[0]);
            }
        }

        return compatibleResponseTypes.Count switch
        {
            0 => requestedResponseType,
            1 => compatibleResponseTypes[0],
            _ => throw new InvalidOperationException(
                MediatorDiagnostics.AmbiguousDeclaredResponse(requestType, requestedResponseType, compatibleResponseTypes)),
        };
    }
}