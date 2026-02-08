using System.Collections.Concurrent;
using System.Linq.Expressions;
using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Dispatches stream requests through the stream request pipeline for response type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
internal static class StreamRequestPipelineDispatcher<TResponse>
{
    private static readonly ConcurrentDictionary<Type, StreamRequestPipelineDispatchDelegate> s_dispatchers = new();

    /// <summary>
    /// Dispatches a stream request through the configured stream request pipeline.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The stream request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    public static IAsyncEnumerable<TResponse> Dispatch(
        IServiceProvider serviceProvider,
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var dispatcher = s_dispatchers.GetOrAdd(requestType, static type => BuildDispatcher(type));

        return dispatcher(serviceProvider, request, cancellationToken);
    }

    /// <summary>
    /// Builds a dispatch delegate for a concrete stream request runtime type.
    /// </summary>
    /// <param name="requestType">The concrete stream request runtime type.</param>
    /// <returns>A compiled dispatch delegate.</returns>
    private static StreamRequestPipelineDispatchDelegate BuildDispatcher(Type requestType)
    {
        var dispatchMethod = typeof(StreamRequestPipelineDispatcher<TResponse>)
            .GetMethod(nameof(DispatchTyped), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (dispatchMethod is null)
        {
            throw new InvalidOperationException(
                MediatorDiagnostics.MissingDispatchMethod(typeof(StreamRequestPipelineDispatcher<TResponse>), nameof(DispatchTyped)));
        }

        var closedDispatchMethod = dispatchMethod.MakeGenericMethod(requestType);
        var serviceProviderParameter = Expression.Parameter(typeof(IServiceProvider), "serviceProvider");
        var requestParameter = Expression.Parameter(typeof(IStreamRequest<TResponse>), "request");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var dispatchCall = Expression.Call(closedDispatchMethod, serviceProviderParameter, requestParameter, cancellationTokenParameter);

        return Expression.Lambda<StreamRequestPipelineDispatchDelegate>(
            dispatchCall,
            serviceProviderParameter,
            requestParameter,
            cancellationTokenParameter).Compile();
    }

    /// <summary>
    /// Dispatches a stream request through the pipeline for a concrete request type.
    /// </summary>
    /// <typeparam name="TRequest">The concrete stream request type.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The stream request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    private static IAsyncEnumerable<TResponse> DispatchTyped<TRequest>(
        IServiceProvider serviceProvider,
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken)
        where TRequest : IStreamRequest<TResponse>
    {
        var typedRequest = (TRequest)request;

        return StreamRequestPipelineExecutor<TRequest, TResponse>.Execute(
            serviceProvider,
            typedRequest,
            cancellationToken,
            dispatchCancellationToken => StreamRequestDispatcher<TResponse>.Dispatch(
                serviceProvider,
                typedRequest,
                dispatchCancellationToken));
    }

    /// <summary>
    /// Represents a cached stream request pipeline dispatch delegate.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The stream request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    internal delegate IAsyncEnumerable<TResponse> StreamRequestPipelineDispatchDelegate(
        IServiceProvider serviceProvider,
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken);
}
