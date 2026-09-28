using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator.IntegrationTests.UnsupportedComponents;

/// <summary>
/// Fixes the response type of the pipeline behavior interface, leaving one fewer type parameter than the interface.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public sealed class StringResponseBehavior<TRequest> : IPipelineBehavior<TRequest, string>
    where TRequest : IRequest<string>
{
    /// <summary>
    /// Passes the request to the next delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public Task<string> Handle(TRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        return next();
    }
}

/// <summary>
/// Declares its type parameters in the opposite order to the pipeline behavior interface.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
/// <typeparam name="TRequest">The request type.</typeparam>
public sealed class ReversedParametersBehavior<TResponse, TRequest> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Passes the request to the next delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        return next();
    }
}

/// <summary>
/// Fixes the exception type of the request exception handler interface.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public sealed class AnyExceptionHandler<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse, Exception>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Leaves the exception unhandled.
    /// </summary>
    /// <param name="request">The failed request.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="state">The exception handling state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Handle(
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}