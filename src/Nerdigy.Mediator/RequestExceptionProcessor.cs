using System.Collections.Concurrent;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Processes request exceptions by running exception handlers and exception actions.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal static class RequestExceptionProcessor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly ConcurrentDictionary<Type, RequestExceptionHandlerInvoker<TRequest, TResponse>[]> s_handlerInvokers = new();

    /// <summary>
    /// Processes an exception thrown while handling a request.
    /// </summary>
    /// <remarks>
    /// Exception handlers run first and may supply a response. If none does, every exception action runs and the
    /// original exception is rethrown. Handlers are skipped when the caller's token was canceled, so cancellation is never
    /// turned into a response. If a handler or action fails, a <see cref="MediatorExceptionProcessingException"/> that
    /// wraps the original exception is thrown instead.
    /// </remarks>
    /// <param name="serviceProvider">The service provider used to resolve exception components.</param>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception the request failed with.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task that resolves to a response supplied by an exception handler.</returns>
    public static async Task<TResponse> Process(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        CancellationToken cancellationToken)
    {
        List<Exception>? processingFailures = null;

        if (!ExceptionProcessing.IsCallerCancellation(exception, cancellationToken))
        {
            try
            {
                var state = new RequestExceptionHandlerState<TResponse>();

                if (await TryHandle(serviceProvider, request, exception, state, cancellationToken).ConfigureAwait(false))
                {
                    MediatorInstrumentation.RecordHandledException(exception);

                    return state.Response!;
                }
            }
            catch (Exception handlerFailure)
            {
                processingFailures = [handlerFailure];
            }
        }

        processingFailures = await ExceptionActionProcessor<TRequest>.Execute(
            serviceProvider,
            request,
            exception,
            processingFailures,
            cancellationToken).ConfigureAwait(false);
        ExceptionProcessing.Throw(exception, processingFailures);

        return default!;
    }

    /// <summary>
    /// Runs exception handlers from the most specific exception type to the least specific until one handles the exception.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception the request failed with.</param>
    /// <param name="state">The handling state that a handler marks as handled.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running handlers.</param>
    /// <returns><see langword="true"/> when a handler handled the exception.</returns>
    private static async Task<bool> TryHandle(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken)
    {
        var invokers = s_handlerInvokers.GetOrAdd(exception.GetType(), static exceptionType => BuildInvokers(exceptionType));
        var invoked = new InvokedComponentSet();

        foreach (var invoker in invokers)
        {
            if (await invoker.TryHandle(serviceProvider, request, exception, state, invoked, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds handler invokers for the exception type hierarchy.
    /// </summary>
    /// <param name="exceptionType">The concrete thrown exception type.</param>
    /// <returns>Handler invokers ordered from most specific to least specific exception type.</returns>
    private static RequestExceptionHandlerInvoker<TRequest, TResponse>[] BuildInvokers(Type exceptionType)
    {
        return
        [
            .. ExceptionProcessing.EnumerateExceptionTypeHierarchy(exceptionType)
                .Select(static type => type == typeof(Exception)
                    ? new RequestExceptionHandlerInvoker<TRequest, TResponse, Exception>()
                    : GenericInvokerFactory.Create<RequestExceptionHandlerInvoker<TRequest, TResponse>>(
                        typeof(RequestExceptionHandlerInvoker<,,>),
                        typeof(TRequest),
                        typeof(TResponse),
                        type))
        ];
    }
}

/// <summary>
/// Runs the request exception handlers registered for one exception type.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
internal abstract class RequestExceptionHandlerInvoker<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Runs the handlers registered for this exception type until one handles the exception.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception the request failed with.</param>
    /// <param name="state">The handling state that a handler marks as handled.</param>
    /// <param name="invoked">The implementations that already ran for this exception.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running handlers.</param>
    /// <returns><see langword="true"/> when a handler handled the exception.</returns>
    public abstract Task<bool> TryHandle(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        InvokedComponentSet invoked,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the request exception handlers registered for <typeparamref name="TException"/>.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
/// <typeparam name="TException">The exception type at this level of the hierarchy.</typeparam>
internal sealed class RequestExceptionHandlerInvoker<TRequest, TResponse, TException> : RequestExceptionHandlerInvoker<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TException : Exception
{
    private static readonly Task<bool> s_notHandled = Task.FromResult(false);

    /// <inheritdoc />
    public override Task<bool> TryHandle(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        InvokedComponentSet invoked,
        CancellationToken cancellationToken)
    {
        var handlers = ServiceProviderUtilities.GetServices<IRequestExceptionHandler<TRequest, TResponse, TException>>(serviceProvider);

        return handlers.Length == 0
            ? s_notHandled
            : TryHandleCore(handlers, request, (TException)exception, state, invoked, cancellationToken);
    }

    /// <summary>
    /// Runs resolved handlers until one handles the exception.
    /// </summary>
    /// <param name="handlers">The resolved handlers.</param>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception the request failed with.</param>
    /// <param name="state">The handling state that a handler marks as handled.</param>
    /// <param name="invoked">The implementations that already ran for this exception.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running handlers.</param>
    /// <returns><see langword="true"/> when a handler handled the exception.</returns>
    private static async Task<bool> TryHandleCore(
        IRequestExceptionHandler<TRequest, TResponse, TException>[] handlers,
        TRequest request,
        TException exception,
        RequestExceptionHandlerState<TResponse> state,
        InvokedComponentSet invoked,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            if (!invoked.Add(handler))
            {
                continue;
            }

            await handler.Handle(request, exception, state, cancellationToken).ConfigureAwait(false);

            if (state.Handled)
            {
                return true;
            }
        }

        return false;
    }
}