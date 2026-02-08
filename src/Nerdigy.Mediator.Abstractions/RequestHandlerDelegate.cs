namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Represents the next delegate in a request pipeline.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
/// <returns>A task that resolves to the response payload.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();
