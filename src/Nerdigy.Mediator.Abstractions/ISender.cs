namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Provides request and stream dispatch operations.
/// </summary>
public interface ISender
{
    /// <summary>
    /// Sends a request to a single handler and returns its response.
    /// </summary>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request with no response payload to a single handler.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that completes when handling finishes.</returns>
    Task Send(IRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a response stream for a stream request.
    /// </summary>
    /// <typeparam name="TResponse">The streamed payload type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while streaming.</param>
    /// <returns>An asynchronous sequence of response payloads.</returns>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);
}