namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Defines a handler for requests that return a response payload.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles a request and returns a response payload.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while handling the request.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Defines a handler for requests that do not return a response payload.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestHandler<in TRequest>
    where TRequest : IRequest
{
    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while handling the request.</param>
    /// <returns>A task that completes when request handling has finished.</returns>
    Task Handle(TRequest request, CancellationToken cancellationToken);
}