namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Defines a handler for stream requests.
/// </summary>
/// <typeparam name="TRequest">The stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed payload type.</typeparam>
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Handles a stream request and returns a stream of response payloads.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while streaming.</param>
    /// <returns>An asynchronous sequence of response payloads.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}