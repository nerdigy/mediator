namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Defines exception handling behavior for request processing.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
/// <typeparam name="TException">The exception type this handler can process.</typeparam>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IRequest<TResponse>
    where TException : Exception
{
    /// <summary>
    /// Handles an exception thrown while processing a request.
    /// </summary>
    /// <param name="request">The request being handled when the exception occurred.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="state">The exception handling state used to mark an exception as handled.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while handling the exception.</param>
    /// <returns>A task that completes when exception handling finishes.</returns>
    Task Handle(
        TRequest request,
        TException exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}