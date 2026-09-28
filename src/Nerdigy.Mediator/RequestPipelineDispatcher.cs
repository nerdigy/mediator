using System.Collections.Concurrent;
using System.Linq.Expressions;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Dispatches requests through the request pipeline for response type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal static class RequestPipelineDispatcher<TResponse>
{
    private static readonly ConcurrentDictionary<Type, RequestPipelineDispatchDelegate> s_dispatchers = new();

    /// <summary>
    /// Dispatches a request through the configured request pipeline.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public static Task<TResponse> Dispatch(
        IServiceProvider serviceProvider,
        IRequest<TResponse> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var dispatcher = s_dispatchers.GetOrAdd(requestType, static type => BuildDispatcher(type));

        return dispatcher(serviceProvider, request, cancellationToken);
    }

    /// <summary>
    /// Builds a dispatch delegate for a concrete request runtime type.
    /// </summary>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <returns>A compiled dispatch delegate.</returns>
    private static RequestPipelineDispatchDelegate BuildDispatcher(Type requestType)
    {
        var closedDispatchMethod = IsVoidRequest(requestType)
            ? VoidRequestPipelineDispatcher.GetDispatchMethod(requestType)
            : GetDispatchMethod(requestType);
        var serviceProviderParameter = Expression.Parameter(typeof(IServiceProvider), "serviceProvider");
        var requestParameter = Expression.Parameter(typeof(IRequest<TResponse>), "request");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var dispatchCall = Expression.Call(closedDispatchMethod, serviceProviderParameter, requestParameter, cancellationTokenParameter);

        return Expression.Lambda<RequestPipelineDispatchDelegate>(
            dispatchCall,
            serviceProviderParameter,
            requestParameter,
            cancellationTokenParameter).Compile();
    }

    /// <summary>
    /// Determines whether a request sent for <typeparamref name="TResponse"/> is a void-style request.
    /// </summary>
    /// <remarks>
    /// <see cref="IRequest"/> extends <c>IRequest&lt;Unit&gt;</c>, so a void-style request can reach this dispatcher through
    /// <c>Send&lt;Unit&gt;</c>. Its handler is registered as <c>IRequestHandler&lt;TRequest&gt;</c>, so it must be dispatched
    /// the same way as the non-generic <c>Send</c> overload.
    /// </remarks>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <returns><see langword="true"/> when the request must be dispatched to its void handler; otherwise <see langword="false"/>.</returns>
    private static bool IsVoidRequest(Type requestType)
    {
        return typeof(TResponse) == typeof(Unit) && typeof(IRequest).IsAssignableFrom(requestType);
    }

    /// <summary>
    /// Gets the dispatch method closed over a concrete request type and the response type it is dispatched with.
    /// </summary>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <returns>The closed dispatch method.</returns>
    private static System.Reflection.MethodInfo GetDispatchMethod(Type requestType)
    {
        var declaredResponseType = DeclaredResponseResolver.Resolve(requestType, typeof(IRequest<>), typeof(TResponse));
        var isCovariantView = declaredResponseType != typeof(TResponse);
        var dispatchMethodName = isCovariantView ? nameof(DispatchCovariant) : nameof(DispatchTyped);
        var dispatchMethod = typeof(RequestPipelineDispatcher<TResponse>)
            .GetMethod(dispatchMethodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (dispatchMethod is null)
        {
            throw new InvalidOperationException(
                MediatorDiagnostics.MissingDispatchMethod(typeof(RequestPipelineDispatcher<TResponse>), dispatchMethodName));
        }

        return isCovariantView
            ? dispatchMethod.MakeGenericMethod(requestType, declaredResponseType)
            : dispatchMethod.MakeGenericMethod(requestType);
    }

    /// <summary>
    /// Dispatches a request through the pipeline for a concrete request type.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    private static Task<TResponse> DispatchTyped<TRequest>(
        IServiceProvider serviceProvider,
        IRequest<TResponse> request,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var typedRequest = (TRequest)request;

        return RequestPipelineExecutor<TRequest, TResponse>.Execute(
            serviceProvider,
            typedRequest,
            cancellationToken,
            () => RequestDispatcher<TResponse>.Dispatch(serviceProvider, typedRequest, cancellationToken));
    }

    /// <summary>
    /// Dispatches a request sent through a covariant view of its declared response type.
    /// </summary>
    /// <remarks>
    /// The request runs through the pipeline and handler registered for its declared response type, and the response
    /// is returned as <typeparamref name="TResponse"/>.
    /// </remarks>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TDeclaredResponse">The response type declared by the request.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    private static async Task<TResponse> DispatchCovariant<TRequest, TDeclaredResponse>(
        IServiceProvider serviceProvider,
        IRequest<TResponse> request,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TDeclaredResponse>
        where TDeclaredResponse : TResponse
    {
        return await RequestPipelineDispatcher<TDeclaredResponse>.DispatchTyped<TRequest>(
            serviceProvider,
            (TRequest)request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Represents a cached request pipeline dispatch delegate.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    internal delegate Task<TResponse> RequestPipelineDispatchDelegate(
        IServiceProvider serviceProvider,
        IRequest<TResponse> request,
        CancellationToken cancellationToken);
}