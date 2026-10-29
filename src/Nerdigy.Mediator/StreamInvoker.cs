using System.Collections.Concurrent;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Dispatches stream requests whose runtime type is only known as <see cref="IStreamRequest{TResponse}"/>.
/// </summary>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
internal abstract class StreamInvoker<TResponse>
{
    private static readonly ConcurrentDictionary<Type, StreamInvoker<TResponse>> s_invokers = new();

    /// <summary>
    /// Creates the response stream for the request.
    /// </summary>
    /// <param name="request">The stream request to dispatch.</param>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while streaming.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    public abstract IAsyncEnumerable<TResponse> Invoke(
        IStreamRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the cached invoker for a concrete stream request type.
    /// </summary>
    /// <param name="requestType">The concrete stream request runtime type.</param>
    /// <returns>The invoker for the request type.</returns>
    public static StreamInvoker<TResponse> Get(Type requestType)
    {
        return s_invokers.TryGetValue(requestType, out var invoker)
            ? invoker
            : s_invokers.GetOrAdd(requestType, static type => Create(type));
    }

    /// <summary>
    /// Registers a statically created invoker for a concrete stream request type.
    /// </summary>
    /// <param name="requestType">The concrete stream request type.</param>
    /// <param name="invoker">The invoker for the request type.</param>
    public static void Register(Type requestType, StreamInvoker<TResponse> invoker)
    {
        s_invokers.TryAdd(requestType, invoker);
    }

    /// <summary>
    /// Creates an invoker for a stream request type that was not registered up front.
    /// </summary>
    /// <param name="requestType">The concrete stream request runtime type.</param>
    /// <returns>The created invoker.</returns>
    private static StreamInvoker<TResponse> Create(Type requestType)
    {
        return GenericInvokerFactory.CreateForMessage<StreamInvoker<TResponse>>(
            typeof(StreamInvoker<,>),
            requestType,
            $"options.AddStreamRequestHandler<{requestType.Name}, {typeof(TResponse).Name}, THandler>()",
            requestType,
            typeof(TResponse));
    }
}

/// <summary>
/// Dispatches stream requests of type <typeparamref name="TRequest"/> to <see cref="IStreamRequestHandler{TRequest, TResponse}"/>.
/// </summary>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
internal sealed class StreamInvoker<TRequest, TResponse> : StreamInvoker<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <inheritdoc />
    public override IAsyncEnumerable<TResponse> Invoke(
        IStreamRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        return StreamRequestPipeline<TRequest, TResponse>.Execute(serviceProvider, (TRequest)request, cancellationToken);
    }
}