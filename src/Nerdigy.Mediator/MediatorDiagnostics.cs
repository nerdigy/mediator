namespace Nerdigy.Mediator;

/// <summary>
/// Provides standardized diagnostic messages for mediator runtime failures.
/// </summary>
internal static class MediatorDiagnostics
{
    /// <summary>
    /// Creates a missing-dispatch-method message.
    /// </summary>
    /// <param name="dispatcherType">The dispatcher type that should expose the method.</param>
    /// <param name="methodName">The missing method name.</param>
    /// <returns>A diagnostic message that explains the missing method condition.</returns>
    public static string MissingDispatchMethod(Type dispatcherType, string methodName)
    {
        ArgumentNullException.ThrowIfNull(dispatcherType);
        ArgumentNullException.ThrowIfNull(methodName);

        return $"Could not find method '{methodName}' on dispatcher type '{dispatcherType.FullName}'.";
    }

    /// <summary>
    /// Creates a missing-handler-method message.
    /// </summary>
    /// <param name="handlerType">The closed handler type.</param>
    /// <param name="requestType">The request type associated with the handler.</param>
    /// <returns>A diagnostic message that explains the missing method condition.</returns>
    public static string MissingHandleMethod(Type handlerType, Type requestType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(requestType);

        return $"Handler type '{handlerType.FullName}' does not expose a matching Handle method for '{requestType.FullName}'.";
    }

    /// <summary>
    /// Creates a missing-request-handler registration message.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <param name="responseType">The response type.</param>
    /// <returns>A diagnostic message that explains the missing registration.</returns>
    public static string MissingRequestHandlerRegistration(Type requestType, Type responseType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(responseType);

        return $"No request handler is registered for request type '{requestType.FullName}' and response type '{responseType.FullName}'. Register IRequestHandler<{requestType.Name}, {responseType.Name}> in your dependency injection container.";
    }

    /// <summary>
    /// Creates a missing-void-request-handler registration message.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <returns>A diagnostic message that explains the missing registration.</returns>
    public static string MissingVoidRequestHandlerRegistration(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        return $"No request handler is registered for request type '{requestType.FullName}'. Register IRequestHandler<{requestType.Name}> in your dependency injection container.";
    }

    /// <summary>
    /// Creates a missing-stream-request-handler registration message.
    /// </summary>
    /// <param name="requestType">The stream request type.</param>
    /// <param name="responseType">The streamed response type.</param>
    /// <returns>A diagnostic message that explains the missing registration.</returns>
    public static string MissingStreamRequestHandlerRegistration(Type requestType, Type responseType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(responseType);

        return $"No stream request handler is registered for request type '{requestType.FullName}' and response type '{responseType.FullName}'. Register IStreamRequestHandler<{requestType.Name}, {responseType.Name}> in your dependency injection container.";
    }
}
