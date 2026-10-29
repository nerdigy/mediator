using System.Collections.Concurrent;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Processes stream request exceptions by running stream exception handlers and exception actions.
/// </summary>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
internal static class StreamRequestExceptionProcessor<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    private static readonly ConcurrentDictionary<Type, StreamRequestExceptionHandlerInvoker<TRequest, TResponse>[]> s_handlerInvokers = new();

    /// <summary>
    /// Processes an exception thrown while creating or enumerating a stream.
    /// </summary>
    /// <remarks>
    /// When <paramref name="canRecover"/> is set, stream exception handlers run first and may supply a replacement
    /// stream. Handlers are skipped when the enumeration token was canceled or when cleanup of the failed stream already
    /// failed. Otherwise every exception action runs and the exception is rethrown, wrapped in a
    /// <see cref="MediatorExceptionProcessingException"/> when any processing step failed.
    /// </remarks>
    /// <param name="serviceProvider">The service provider used to resolve exception components.</param>
    /// <param name="request">The stream request that failed.</param>
    /// <param name="exception">The exception the stream failed with.</param>
    /// <param name="canRecover">Whether a stream exception handler may still replace the stream.</param>
    /// <param name="processingFailures">Failures that already happened while cleaning up the failed stream, if any.</param>
    /// <param name="cancellationToken">The enumeration's cancellation token.</param>
    /// <returns>A task that resolves to a replacement stream supplied by an exception handler.</returns>
    public static async Task<IAsyncEnumerable<TResponse>> Process(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        bool canRecover,
        List<Exception>? processingFailures,
        CancellationToken cancellationToken)
    {
        if (canRecover && processingFailures is null && !ExceptionProcessing.IsCallerCancellation(exception, cancellationToken))
        {
            try
            {
                var state = new StreamRequestExceptionHandlerState<TResponse>();

                if (await TryHandle(serviceProvider, request, exception, state, cancellationToken).ConfigureAwait(false))
                {
                    MediatorInstrumentation.RecordHandledException(exception);

                    return state.ResponseStream!;
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

        return null!;
    }

    /// <summary>
    /// Runs stream exception handlers from the most specific exception type to the least specific until one handles the exception.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The stream request that failed.</param>
    /// <param name="exception">The exception the stream failed with.</param>
    /// <param name="state">The handling state that a handler marks as handled.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running handlers.</param>
    /// <returns><see langword="true"/> when a handler handled the exception.</returns>
    private static async Task<bool> TryHandle(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        StreamRequestExceptionHandlerState<TResponse> state,
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
    /// Builds stream exception handler invokers for the exception type hierarchy.
    /// </summary>
    /// <param name="exceptionType">The concrete thrown exception type.</param>
    /// <returns>Handler invokers ordered from most specific to least specific exception type.</returns>
    private static StreamRequestExceptionHandlerInvoker<TRequest, TResponse>[] BuildInvokers(Type exceptionType)
    {
        return
        [
            .. ExceptionProcessing.EnumerateExceptionTypeHierarchy(exceptionType)
                .Select(static type => type == typeof(Exception)
                    ? new StreamRequestExceptionHandlerInvoker<TRequest, TResponse, Exception>()
                    : GenericInvokerFactory.Create<StreamRequestExceptionHandlerInvoker<TRequest, TResponse>>(
                        typeof(StreamRequestExceptionHandlerInvoker<,,>),
                        typeof(TRequest),
                        typeof(TResponse),
                        type))
        ];
    }
}

/// <summary>
/// Runs the stream exception handlers registered for one exception type.
/// </summary>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
internal abstract class StreamRequestExceptionHandlerInvoker<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Runs the handlers registered for this exception type until one handles the exception.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="request">The stream request that failed.</param>
    /// <param name="exception">The exception the stream failed with.</param>
    /// <param name="state">The handling state that a handler marks as handled.</param>
    /// <param name="invoked">The implementations that already ran for this exception.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running handlers.</param>
    /// <returns><see langword="true"/> when a handler handled the exception.</returns>
    public abstract Task<bool> TryHandle(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        StreamRequestExceptionHandlerState<TResponse> state,
        InvokedComponentSet invoked,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the stream exception handlers registered for <typeparamref name="TException"/>.
/// </summary>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
/// <typeparam name="TException">The exception type at this level of the hierarchy.</typeparam>
internal sealed class StreamRequestExceptionHandlerInvoker<TRequest, TResponse, TException> : StreamRequestExceptionHandlerInvoker<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
    where TException : Exception
{
    private static readonly Task<bool> s_notHandled = Task.FromResult(false);

    /// <inheritdoc />
    public override Task<bool> TryHandle(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        StreamRequestExceptionHandlerState<TResponse> state,
        InvokedComponentSet invoked,
        CancellationToken cancellationToken)
    {
        var handlers = ServiceProviderUtilities.GetServices<IStreamRequestExceptionHandler<TRequest, TResponse, TException>>(serviceProvider);

        return handlers.Length == 0
            ? s_notHandled
            : TryHandleCore(handlers, request, (TException)exception, state, invoked, cancellationToken);
    }

    /// <summary>
    /// Runs resolved handlers until one handles the exception.
    /// </summary>
    /// <param name="handlers">The resolved handlers.</param>
    /// <param name="request">The stream request that failed.</param>
    /// <param name="exception">The exception the stream failed with.</param>
    /// <param name="state">The handling state that a handler marks as handled.</param>
    /// <param name="invoked">The implementations that already ran for this exception.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running handlers.</param>
    /// <returns><see langword="true"/> when a handler handled the exception.</returns>
    private static async Task<bool> TryHandleCore(
        IStreamRequestExceptionHandler<TRequest, TResponse, TException>[] handlers,
        TRequest request,
        TException exception,
        StreamRequestExceptionHandlerState<TResponse> state,
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