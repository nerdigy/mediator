namespace Nerdigy.Mediator.Contracts;

/// <summary>
/// Represents the next delegate in a stream request pipeline.
/// </summary>
/// <remarks>
/// Pass a token to give the rest of the pipeline and the handler a different cancellation token. Calling
/// <c>next()</c> without a token, or with <see langword="default"/>, keeps the token the current behavior received.
/// </remarks>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
/// <param name="cancellationToken">
/// The cancellation token for the rest of the pipeline, or <see langword="default"/> to keep the current token.
/// </param>
/// <returns>An asynchronous sequence of streamed response payloads.</returns>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);