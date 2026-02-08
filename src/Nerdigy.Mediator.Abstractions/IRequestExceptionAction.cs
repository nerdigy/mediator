namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Defines an exception action that runs when request processing fails.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TException">The exception type this action can process.</typeparam>
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IBaseRequest
    where TException : Exception
{
    /// <summary>
    /// Executes logic in response to an exception.
    /// </summary>
    /// <param name="request">The request being handled when the exception occurred.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while executing the action.</param>
    /// <returns>A task that completes when execution finishes.</returns>
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}
