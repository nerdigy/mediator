namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Represents a request that expects a response payload.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public interface IRequest<out TResponse> : IBaseRequest
{
}

/// <summary>
/// Represents a request that does not return a response payload.
/// </summary>
public interface IRequest : IRequest<Unit>
{
}
