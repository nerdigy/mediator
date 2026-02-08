using System.Collections.Concurrent;
using System.Linq.Expressions;
using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Provides cached request dispatch delegates for response type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal static class RequestDispatcher<TResponse>
{
    private const string HandleMethodName = "Handle";
    private static readonly ConcurrentDictionary<Type, RequestDispatchDelegate> s_dispatchers = new();

    /// <summary>
    /// Dispatches a request by resolving and invoking its handler.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
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
    /// <param name="requestType">The concrete request type.</param>
    /// <returns>A cached dispatch delegate for the request type.</returns>
    private static RequestDispatchDelegate BuildDispatcher(Type requestType)
    {
        var handlerType = typeof(IRequestHandler<,>).MakeGenericType(requestType, typeof(TResponse));
        var handleMethod = handlerType.GetMethod(HandleMethodName, [requestType, typeof(CancellationToken)]);

        if (handleMethod is null)
        {
            throw new InvalidOperationException(MediatorDiagnostics.MissingHandleMethod(handlerType, requestType));
        }

        var invokeHandler = CompileHandlerDelegate(handlerType, requestType, handleMethod);

        return (serviceProvider, request, cancellationToken) =>
        {
            var handler = serviceProvider.GetService(handlerType)
                ?? throw new InvalidOperationException(
                    MediatorDiagnostics.MissingRequestHandlerRegistration(requestType, typeof(TResponse)));

            return invokeHandler(handler, request, cancellationToken);
        };
    }

    /// <summary>
    /// Compiles a strongly typed invoker for a concrete request handler type.
    /// </summary>
    /// <param name="handlerType">The closed request handler type.</param>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <param name="handleMethod">The handler method info.</param>
    /// <returns>A compiled delegate that invokes the handler without reflection on each call.</returns>
    private static ClosedRequestHandlerInvoker CompileHandlerDelegate(
        Type handlerType,
        Type requestType,
        System.Reflection.MethodInfo handleMethod)
    {
        var handlerParameter = Expression.Parameter(typeof(object), "handler");
        var requestParameter = Expression.Parameter(typeof(IRequest<TResponse>), "request");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var callExpression = Expression.Call(
            Expression.Convert(handlerParameter, handlerType),
            handleMethod,
            Expression.Convert(requestParameter, requestType),
            cancellationTokenParameter);

        return Expression.Lambda<ClosedRequestHandlerInvoker>(
            callExpression,
            handlerParameter,
            requestParameter,
            cancellationTokenParameter).Compile();
    }

    /// <summary>
    /// Represents a cached request dispatch delegate.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    internal delegate Task<TResponse> RequestDispatchDelegate(
        IServiceProvider serviceProvider,
        IRequest<TResponse> request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Represents a compiled delegate that invokes a closed request handler.
    /// </summary>
    /// <param name="handler">The resolved handler instance.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while handling.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    private delegate Task<TResponse> ClosedRequestHandlerInvoker(
        object handler,
        IRequest<TResponse> request,
        CancellationToken cancellationToken);
}
