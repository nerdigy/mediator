using System.Collections.Concurrent;
using System.Linq.Expressions;

using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Provides cached request dispatch delegates for void-style requests.
/// </summary>
internal static class VoidRequestDispatcher
{
    private const string HandleMethodName = "Handle";
    private static readonly ConcurrentDictionary<Type, VoidRequestDispatchDelegate> s_dispatchers = new();

    /// <summary>
    /// Dispatches a void-style request by resolving and invoking its handler.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that completes when request handling finishes.</returns>
    public static Task Dispatch(
        IServiceProvider serviceProvider,
        IRequest request,
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
    private static VoidRequestDispatchDelegate BuildDispatcher(Type requestType)
    {
        var handlerType = typeof(IRequestHandler<>).MakeGenericType(requestType);
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
                    MediatorDiagnostics.MissingVoidRequestHandlerRegistration(requestType));

            return invokeHandler(handler, request, cancellationToken);
        };
    }

    /// <summary>
    /// Compiles a strongly typed invoker for a concrete void-style request handler type.
    /// </summary>
    /// <param name="handlerType">The closed request handler type.</param>
    /// <param name="requestType">The concrete request runtime type.</param>
    /// <param name="handleMethod">The handler method info.</param>
    /// <returns>A compiled delegate that invokes the handler without reflection on each call.</returns>
    private static ClosedVoidRequestHandlerInvoker CompileHandlerDelegate(
        Type handlerType,
        Type requestType,
        System.Reflection.MethodInfo handleMethod)
    {
        var handlerParameter = Expression.Parameter(typeof(object), "handler");
        var requestParameter = Expression.Parameter(typeof(IRequest), "request");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var callExpression = Expression.Call(
            Expression.Convert(handlerParameter, handlerType),
            handleMethod,
            Expression.Convert(requestParameter, requestType),
            cancellationTokenParameter);

        return Expression.Lambda<ClosedVoidRequestHandlerInvoker>(
            callExpression,
            handlerParameter,
            requestParameter,
            cancellationTokenParameter).Compile();
    }

    /// <summary>
    /// Represents a cached void-style request dispatch delegate.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while dispatching.</param>
    /// <returns>A task that completes when request handling finishes.</returns>
    internal delegate Task VoidRequestDispatchDelegate(
        IServiceProvider serviceProvider,
        IRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Represents a compiled delegate that invokes a closed void-style request handler.
    /// </summary>
    /// <param name="handler">The resolved handler instance.</param>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while handling.</param>
    /// <returns>A task that completes when request handling finishes.</returns>
    private delegate Task ClosedVoidRequestHandlerInvoker(
        object handler,
        IRequest request,
        CancellationToken cancellationToken);
}