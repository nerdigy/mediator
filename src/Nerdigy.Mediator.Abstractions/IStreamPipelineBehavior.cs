namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Defines a middleware component for stream request handling.
/// </summary>
/// <typeparam name="TRequest">The stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Handles a stream request and optionally invokes the next delegate in the stream pipeline.
    /// </summary>
    /// <param name="request">The request being handled.</param>
    /// <param name="next">The next delegate in the stream pipeline.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while handling the request.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
