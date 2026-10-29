using System.Collections.Concurrent;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Dispatches requests whose runtime type is only known as <see cref="IRequest{TResponse}"/>.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal abstract class RequestInvoker<TResponse>
{
    private static readonly ConcurrentDictionary<Type, RequestInvoker<TResponse>> s_invokers = new();

    /// <summary>
    /// Runs the request through its pipeline and handler.
    /// </summary>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public abstract Task<TResponse> Invoke(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the cached invoker for a concrete request type.
    /// </summary>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <returns>The invoker for the request type.</returns>
    public static RequestInvoker<TResponse> Get(Type requestType)
    {
        return s_invokers.TryGetValue(requestType, out var invoker)
            ? invoker
            : s_invokers.GetOrAdd(requestType, static type => Create(type));
    }

    /// <summary>
    /// Registers a statically created invoker for a concrete request type.
    /// </summary>
    /// <param name="requestType">The concrete request type.</param>
    /// <param name="invoker">The invoker for the request type.</param>
    public static void Register(Type requestType, RequestInvoker<TResponse> invoker)
    {
        s_invokers.TryAdd(requestType, invoker);
    }

    /// <summary>
    /// Creates an invoker for a request type that was not registered up front.
    /// </summary>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <returns>The created invoker.</returns>
    private static RequestInvoker<TResponse> Create(Type requestType)
    {
        // IRequest extends IRequest<Unit>, so a void request can arrive through Send<Unit>. Its handler is
        // IRequestHandler<TRequest>, so it is dispatched the same way as the non-generic Send overload.
        if (typeof(TResponse) == typeof(Unit) && typeof(IRequest).IsAssignableFrom(requestType))
        {
            return GenericInvokerFactory.CreateForMessage<RequestInvoker<TResponse>>(
                typeof(VoidRequestInvoker<>),
                requestType,
                $"options.AddRequestHandler<{requestType.Name}, THandler>()",
                requestType);
        }

        return GenericInvokerFactory.CreateForMessage<RequestInvoker<TResponse>>(
            typeof(RequestInvoker<,>),
            requestType,
            $"options.AddRequestHandler<{requestType.Name}, {typeof(TResponse).Name}, THandler>()",
            requestType,
            typeof(TResponse));
    }
}

/// <summary>
/// Dispatches requests of type <typeparamref name="TRequest"/> to <see cref="IRequestHandler{TRequest, TResponse}"/>.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal sealed class RequestInvoker<TRequest, TResponse> : RequestInvoker<TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly RequestTerminal<TRequest, TResponse> s_terminal = static (serviceProvider, request, cancellationToken) =>
    {
        var handler = serviceProvider.GetService(typeof(IRequestHandler<TRequest, TResponse>)) as IRequestHandler<TRequest, TResponse>
            ?? throw new InvalidOperationException(
                MediatorErrors.MissingRequestHandlerRegistration(typeof(TRequest), typeof(TResponse)));

        return handler.Handle(request, cancellationToken);
    };

    /// <inheritdoc />
    public override Task<TResponse> Invoke(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        return RequestPipeline<TRequest, TResponse>.Execute(serviceProvider, (TRequest)request, s_terminal, cancellationToken);
    }
}

/// <summary>
/// Dispatches void-style requests of type <typeparamref name="TRequest"/> to <see cref="IRequestHandler{TRequest}"/>.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
internal sealed class VoidRequestInvoker<TRequest> : RequestInvoker<Unit>
    where TRequest : IRequest
{
    private static readonly Task<Unit> s_completed = Task.FromResult(Unit.Value);

    private static readonly RequestTerminal<TRequest, Unit> s_terminal = static (serviceProvider, request, cancellationToken) =>
    {
        if (serviceProvider.GetService(typeof(IRequestHandler<TRequest>)) is not IRequestHandler<TRequest> handler)
        {
            var hasUnitHandler = serviceProvider.GetService(typeof(IRequestHandler<TRequest, Unit>)) is not null;

            throw new InvalidOperationException(
                MediatorErrors.MissingVoidRequestHandlerRegistration(typeof(TRequest), hasUnitHandler));
        }

        var task = handler.Handle(request, cancellationToken);

        return task.IsCompletedSuccessfully ? s_completed : AwaitHandler(task);
    };

    /// <inheritdoc />
    public override Task<Unit> Invoke(
        IRequest<Unit> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        return RequestPipeline<TRequest, Unit>.Execute(serviceProvider, (TRequest)request, s_terminal, cancellationToken);
    }

    /// <summary>
    /// Awaits a handler that did not complete synchronously.
    /// </summary>
    /// <param name="task">The handler task.</param>
    /// <returns>A task that resolves to <see cref="Unit.Value"/>.</returns>
    private static async Task<Unit> AwaitHandler(Task task)
    {
        await task.ConfigureAwait(false);

        return Unit.Value;
    }
}

/// <summary>
/// Resolves and invokes the handler at the end of a request pipeline.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
/// <param name="serviceProvider">The service provider used to resolve the handler.</param>
/// <param name="request">The request to handle.</param>
/// <param name="cancellationToken">The cancellation token passed to the handler.</param>
/// <returns>A task that resolves to the response payload.</returns>
internal delegate Task<TResponse> RequestTerminal<TRequest, TResponse>(
    IServiceProvider serviceProvider,
    TRequest request,
    CancellationToken cancellationToken);