namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Defines post-processing behavior that runs after a request handler succeeds.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Processes the request and response after the request handler runs.
    /// </summary>
    /// <param name="request">The request that was handled.</param>
    /// <param name="response">The response produced by the handler.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed during processing.</param>
    /// <returns>A task that completes when post-processing is finished.</returns>
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
