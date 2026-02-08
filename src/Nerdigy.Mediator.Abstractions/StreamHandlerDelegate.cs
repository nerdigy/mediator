namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Represents the next delegate in a stream request pipeline.
/// </summary>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
/// <returns>An asynchronous sequence of streamed response payloads.</returns>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>();
