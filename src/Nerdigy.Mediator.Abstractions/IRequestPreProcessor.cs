namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Defines pre-processing behavior that runs before a request handler.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestPreProcessor<in TRequest>
    where TRequest : IBaseRequest
{
    /// <summary>
    /// Processes the request before the request handler runs.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed during processing.</param>
    /// <returns>A task that completes when pre-processing is finished.</returns>
    Task Process(TRequest request, CancellationToken cancellationToken);
}
