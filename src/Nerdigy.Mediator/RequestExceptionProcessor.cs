using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Processes request exceptions by executing exception handlers and actions.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal static class RequestExceptionProcessor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly ConcurrentDictionary<Type, ExceptionActionInvoker[]> s_actionInvokers = new();
    private static readonly ConcurrentDictionary<Type, ExceptionHandlerInvoker[]> s_handlerInvokers = new();

    /// <summary>
    /// Attempts to handle an exception through registered exception handlers.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The request being processed when the exception occurred.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while processing handlers.</param>
    /// <returns>The exception handling result.</returns>
    public static async Task<ExceptionHandlingResult<TResponse>> TryHandle(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);

        var invokers = s_handlerInvokers.GetOrAdd(exception.GetType(), static exceptionType => BuildHandlerInvokers(exceptionType));
        var state = new RequestExceptionHandlerState<TResponse>();

        foreach (var invoker in invokers)
        {
            var handlers = ServiceProviderUtilities.GetServices(serviceProvider, invoker.ServiceType);

            foreach (var handler in handlers)
            {
                await invoker.Invoke(handler, request, exception, state, cancellationToken).ConfigureAwait(false);

                if (!state.Handled)
                {
                    continue;
                }

                return ExceptionHandlingResult<TResponse>.FromHandled(state.Response!);
            }
        }

        return ExceptionHandlingResult<TResponse>.NotHandled();
    }

    /// <summary>
    /// Executes registered exception actions for the thrown exception.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve actions.</param>
    /// <param name="request">The request being processed when the exception occurred.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while executing actions.</param>
    /// <returns>A task that completes when all actions have executed.</returns>
    public static async Task ExecuteActions(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);

        var invokers = s_actionInvokers.GetOrAdd(exception.GetType(), static exceptionType => BuildActionInvokers(exceptionType));

        foreach (var invoker in invokers)
        {
            var actions = ServiceProviderUtilities.GetServices(serviceProvider, invoker.ServiceType);

            foreach (var action in actions)
            {
                await invoker.Invoke(action, request, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Rethrows an exception preserving the original stack trace.
    /// </summary>
    /// <param name="exception">The exception to rethrow.</param>
    [DoesNotReturn]
    public static void Rethrow(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    /// <summary>
    /// Builds handler invokers for the exception type hierarchy.
    /// </summary>
    /// <param name="exceptionType">The concrete thrown exception type.</param>
    /// <returns>Ordered handler invokers from most specific to least specific exception type.</returns>
    private static ExceptionHandlerInvoker[] BuildHandlerInvokers(Type exceptionType)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);

        List<ExceptionHandlerInvoker> invokers = [];

        foreach (var currentExceptionType in EnumerateExceptionTypeHierarchy(exceptionType))
        {
            var serviceType = typeof(IRequestExceptionHandler<,,>).MakeGenericType(
                typeof(TRequest),
                typeof(TResponse),
                currentExceptionType);
            var method = serviceType.GetMethod(
                nameof(IRequestExceptionHandler<TRequest, TResponse, Exception>.Handle),
                [typeof(TRequest), currentExceptionType, typeof(RequestExceptionHandlerState<TResponse>), typeof(CancellationToken)]);

            if (method is null)
            {
                throw new InvalidOperationException(
                    $"Exception handler type '{serviceType.FullName}' does not expose a matching Handle method.");
            }

            invokers.Add(new ExceptionHandlerInvoker(serviceType, CompileHandlerInvoker(serviceType, currentExceptionType, method)));
        }

        return [.. invokers];
    }

    /// <summary>
    /// Builds action invokers for the exception type hierarchy.
    /// </summary>
    /// <param name="exceptionType">The concrete thrown exception type.</param>
    /// <returns>Ordered action invokers from most specific to least specific exception type.</returns>
    private static ExceptionActionInvoker[] BuildActionInvokers(Type exceptionType)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);

        List<ExceptionActionInvoker> invokers = [];

        foreach (var currentExceptionType in EnumerateExceptionTypeHierarchy(exceptionType))
        {
            var serviceType = typeof(IRequestExceptionAction<,>).MakeGenericType(typeof(TRequest), currentExceptionType);
            var method = serviceType.GetMethod(
                nameof(IRequestExceptionAction<TRequest, Exception>.Execute),
                [typeof(TRequest), currentExceptionType, typeof(CancellationToken)]);

            if (method is null)
            {
                throw new InvalidOperationException(
                    $"Exception action type '{serviceType.FullName}' does not expose a matching Execute method.");
            }

            invokers.Add(new ExceptionActionInvoker(serviceType, CompileActionInvoker(serviceType, currentExceptionType, method)));
        }

        return [.. invokers];
    }

    /// <summary>
    /// Compiles an exception handler invoker for a concrete exception type.
    /// </summary>
    /// <param name="serviceType">The closed handler service type.</param>
    /// <param name="exceptionType">The closed exception type.</param>
    /// <param name="method">The handler method info.</param>
    /// <returns>A compiled handler invoker delegate.</returns>
    private static ExceptionHandlerInvokerDelegate CompileHandlerInvoker(Type serviceType, Type exceptionType, MethodInfo method)
    {
        var handlerParameter = Expression.Parameter(typeof(object), "handler");
        var requestParameter = Expression.Parameter(typeof(TRequest), "request");
        var exceptionParameter = Expression.Parameter(typeof(Exception), "exception");
        var stateParameter = Expression.Parameter(typeof(RequestExceptionHandlerState<TResponse>), "state");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var callExpression = Expression.Call(
            Expression.Convert(handlerParameter, serviceType),
            method,
            requestParameter,
            Expression.Convert(exceptionParameter, exceptionType),
            stateParameter,
            cancellationTokenParameter);

        return Expression.Lambda<ExceptionHandlerInvokerDelegate>(
            callExpression,
            handlerParameter,
            requestParameter,
            exceptionParameter,
            stateParameter,
            cancellationTokenParameter).Compile();
    }

    /// <summary>
    /// Compiles an exception action invoker for a concrete exception type.
    /// </summary>
    /// <param name="serviceType">The closed action service type.</param>
    /// <param name="exceptionType">The closed exception type.</param>
    /// <param name="method">The action method info.</param>
    /// <returns>A compiled action invoker delegate.</returns>
    private static ExceptionActionInvokerDelegate CompileActionInvoker(Type serviceType, Type exceptionType, MethodInfo method)
    {
        var actionParameter = Expression.Parameter(typeof(object), "action");
        var requestParameter = Expression.Parameter(typeof(TRequest), "request");
        var exceptionParameter = Expression.Parameter(typeof(Exception), "exception");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var callExpression = Expression.Call(
            Expression.Convert(actionParameter, serviceType),
            method,
            requestParameter,
            Expression.Convert(exceptionParameter, exceptionType),
            cancellationTokenParameter);

        return Expression.Lambda<ExceptionActionInvokerDelegate>(
            callExpression,
            actionParameter,
            requestParameter,
            exceptionParameter,
            cancellationTokenParameter).Compile();
    }

    /// <summary>
    /// Enumerates an exception type and its base exception types from most specific to least specific.
    /// </summary>
    /// <param name="exceptionType">The concrete thrown exception type.</param>
    /// <returns>An ordered sequence of compatible exception types.</returns>
    private static IEnumerable<Type> EnumerateExceptionTypeHierarchy(Type exceptionType)
    {
        for (var currentType = exceptionType; currentType is not null && typeof(Exception).IsAssignableFrom(currentType); currentType = currentType.BaseType)
        {
            yield return currentType;
        }
    }

    /// <summary>
    /// Represents a compiled exception action invoker.
    /// </summary>
    /// <param name="action">The resolved action instance.</param>
    /// <param name="request">The request associated with the exception.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when execution finishes.</returns>
    private delegate Task ExceptionActionInvokerDelegate(
        object action,
        TRequest request,
        Exception exception,
        CancellationToken cancellationToken);

    /// <summary>
    /// Represents a compiled exception handler invoker.
    /// </summary>
    /// <param name="handler">The resolved handler instance.</param>
    /// <param name="request">The request associated with the exception.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="state">The mutable exception handling state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when execution finishes.</returns>
    private delegate Task ExceptionHandlerInvokerDelegate(
        object handler,
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);

    /// <summary>
    /// Represents a cached exception action invoker and its service type.
    /// </summary>
    /// <param name="ServiceType">The service type to resolve.</param>
    /// <param name="Invoke">The compiled invoker delegate.</param>
    private readonly record struct ExceptionActionInvoker(Type ServiceType, ExceptionActionInvokerDelegate Invoke);

    /// <summary>
    /// Represents a cached exception handler invoker and its service type.
    /// </summary>
    /// <param name="ServiceType">The service type to resolve.</param>
    /// <param name="Invoke">The compiled invoker delegate.</param>
    private readonly record struct ExceptionHandlerInvoker(Type ServiceType, ExceptionHandlerInvokerDelegate Invoke);
}

/// <summary>
/// Represents the outcome of exception handler execution.
/// </summary>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal readonly record struct ExceptionHandlingResult<TResponse>
{
    /// <summary>
    /// Gets a value indicating whether an exception was handled.
    /// </summary>
    public bool Handled { get; init; }

    /// <summary>
    /// Gets the handled response value.
    /// </summary>
    public TResponse Response { get; init; }

    /// <summary>
    /// Creates a handled result.
    /// </summary>
    /// <param name="response">The handled response payload.</param>
    /// <returns>A handled result instance.</returns>
    public static ExceptionHandlingResult<TResponse> FromHandled(TResponse response)
    {
        return new ExceptionHandlingResult<TResponse>
        {
            Handled = true,
            Response = response
        };
    }

    /// <summary>
    /// Creates a non-handled result.
    /// </summary>
    /// <returns>A non-handled result instance.</returns>
    public static ExceptionHandlingResult<TResponse> NotHandled()
    {
        return new ExceptionHandlingResult<TResponse>
        {
            Handled = false,
            Response = default!
        };
    }
}