namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Represents a request that streams response payloads asynchronously.
/// </summary>
/// <typeparam name="TResponse">The streamed payload type.</typeparam>
public interface IStreamRequest<out TResponse> : IBaseRequest;