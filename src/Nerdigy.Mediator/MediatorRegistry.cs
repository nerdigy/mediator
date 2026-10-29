using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Registers the dispatch code for message types ahead of time, so dispatching them does not need runtime code generation.
/// </summary>
/// <remarks>
/// <para>
/// The mediator dispatches on a message's runtime type. A message type that is not registered here gets its dispatch
/// code created on first use, which needs runtime code generation and therefore fails under Native AOT.
/// </para>
/// <para>
/// The explicit registration methods in <c>Nerdigy.Mediator.DependencyInjection</c>, such as
/// <c>options.AddRequestHandler&lt;TRequest, TResponse, THandler&gt;()</c>, call these methods for you. Call them
/// directly only when you construct <see cref="Mediator"/> yourself and publish with Native AOT. Registration is
/// idempotent and applies to every mediator in the process.
/// </para>
/// </remarks>
public static class MediatorRegistry
{
    /// <summary>
    /// Registers dispatch code for a request type that returns <typeparamref name="TResponse"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <exception cref="ArgumentException">
    /// Thrown when <typeparamref name="TRequest"/> implements <see cref="IRequest"/>; use <see cref="RegisterVoidRequest{TRequest}"/>.
    /// </exception>
    public static void RegisterRequest<TRequest, TResponse>()
        where TRequest : IRequest<TResponse>
    {
        if (typeof(IRequest).IsAssignableFrom(typeof(TRequest)))
        {
            throw new ArgumentException(
                $"Request type '{typeof(TRequest).FullName}' implements IRequest, so it is handled by IRequestHandler<{typeof(TRequest).Name}>. Register it with RegisterVoidRequest<{typeof(TRequest).Name}>().",
                nameof(TRequest));
        }

        RequestInvoker<TResponse>.Register(typeof(TRequest), new RequestInvoker<TRequest, TResponse>());
    }

    /// <summary>
    /// Registers dispatch code for a void-style request type.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    public static void RegisterVoidRequest<TRequest>()
        where TRequest : IRequest
    {
        RequestInvoker<Unit>.Register(typeof(TRequest), new VoidRequestInvoker<TRequest>());
    }

    /// <summary>
    /// Registers dispatch code for a stream request type that streams <typeparamref name="TResponse"/>.
    /// </summary>
    /// <typeparam name="TRequest">The concrete stream request type.</typeparam>
    /// <typeparam name="TResponse">The streamed response payload type.</typeparam>
    public static void RegisterStreamRequest<TRequest, TResponse>()
        where TRequest : IStreamRequest<TResponse>
    {
        StreamInvoker<TResponse>.Register(typeof(TRequest), new StreamInvoker<TRequest, TResponse>());
    }

    /// <summary>
    /// Registers dispatch code for a notification type.
    /// </summary>
    /// <remarks>
    /// Only needed when the notification is published through a less specific static type, such as
    /// <c>Publish((INotification)notification)</c>. Publishing through the notification's own type needs no registration.
    /// </remarks>
    /// <typeparam name="TNotification">The concrete notification type.</typeparam>
    public static void RegisterNotification<TNotification>()
        where TNotification : INotification
    {
        NotificationInvoker.Register(typeof(TNotification), new NotificationInvoker<TNotification>());
    }
}