namespace Nerdigy.Mediator;

/// <summary>
/// Provides standardized error messages for mediator runtime failures.
/// </summary>
internal static class MediatorErrors
{
    /// <summary>
    /// Creates a missing-request-handler registration message.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <param name="responseType">The response type.</param>
    /// <returns>An error message that explains the missing registration.</returns>
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
    /// <param name="hasUnitHandler">
    /// <see langword="true"/> when an <c>IRequestHandler&lt;TRequest, Unit&gt;</c> is registered for the request instead.
    /// </param>
    /// <returns>An error message that explains the missing registration.</returns>
    public static string MissingVoidRequestHandlerRegistration(Type requestType, bool hasUnitHandler)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        var message = $"No request handler is registered for request type '{requestType.FullName}'. Register IRequestHandler<{requestType.Name}> in your dependency injection container.";

        return hasUnitHandler
            ? message + $" An IRequestHandler<{requestType.Name}, Unit> is registered, but requests that implement IRequest are only dispatched to IRequestHandler<{requestType.Name}>."
            : message;
    }

    /// <summary>
    /// Creates a missing-stream-request-handler registration message.
    /// </summary>
    /// <param name="requestType">The stream request type.</param>
    /// <param name="responseType">The streamed response type.</param>
    /// <returns>An error message that explains the missing registration.</returns>
    public static string MissingStreamRequestHandlerRegistration(Type requestType, Type responseType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(responseType);

        return $"No stream request handler is registered for request type '{requestType.FullName}' and response type '{responseType.FullName}'. Register IStreamRequestHandler<{requestType.Name}, {responseType.Name}> in your dependency injection container.";
    }

    /// <summary>
    /// Creates a message for a message type whose dispatch code was not registered when runtime code generation is unavailable.
    /// </summary>
    /// <param name="messageType">The request, stream request, or notification type.</param>
    /// <param name="registration">The registration call that would make the message type dispatchable.</param>
    /// <returns>An error message that explains how to register the message type.</returns>
    public static string UnregisteredMessageType(Type messageType, string registration)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(registration);

        return $"Message type '{messageType.FullName}' cannot be dispatched because runtime code generation is not available (for example, under Native AOT) and its dispatch code was not registered. " +
            $"Register its handler with {registration} in AddMediator(options => ...), or call the matching MediatorRegistry method at startup.";
    }
}