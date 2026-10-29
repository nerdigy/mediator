namespace Nerdigy.Mediator.Contracts;

/// <summary>
/// Represents the next delegate in a request pipeline.
/// </summary>
/// <remarks>
/// Pass a token to give the rest of the pipeline and the handler a different cancellation token, for example a token
/// linked to a timeout. Calling <c>next()</c> without a token, or with <see langword="default"/>, keeps the token the
/// current behavior received.
/// </remarks>
/// <typeparam name="TResponse">The response payload type.</typeparam>
/// <param name="cancellationToken">
/// The cancellation token for the rest of the pipeline, or <see langword="default"/> to keep the current token.
/// </param>
/// <returns>A task that resolves to the response payload.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);