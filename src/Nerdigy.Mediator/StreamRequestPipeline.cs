using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Executes the stream pipeline for a concrete stream request and response pair.
/// </summary>
/// <remarks>
/// Preprocessors and stream behaviors run when enumeration starts. A failure while building or enumerating the stream
/// goes to exception processing, which may replace the stream once per enumeration.
/// </remarks>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
internal static class StreamRequestPipeline<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Creates the response stream for a request.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The stream request to process.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    public static IAsyncEnumerable<TResponse> Execute(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteCore(serviceProvider, request, cancellationToken);
    }

    /// <summary>
    /// Runs one enumeration of the stream pipeline.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The stream request to process.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="enumerationCancellationToken">The token passed to <c>GetAsyncEnumerator</c> or <c>WithCancellation</c>.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    private static async IAsyncEnumerable<TResponse> ExecuteCore(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken,
        [EnumeratorCancellation] CancellationToken enumerationCancellationToken = default)
    {
        var effectiveCancellationToken = cancellationToken;
        CancellationTokenSource? linkedCancellation = null;

        if (cancellationToken.CanBeCanceled && enumerationCancellationToken.CanBeCanceled)
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enumerationCancellationToken);
            effectiveCancellationToken = linkedCancellation.Token;
        }
        else if (enumerationCancellationToken.CanBeCanceled)
        {
            effectiveCancellationToken = enumerationCancellationToken;
        }

        // The telemetry scope covers the whole enumeration, not just creating the stream.
        using var telemetry = MediatorInstrumentation.StartStream(typeof(TRequest));

        try
        {
            var initialStream = await CreateInitialStream(
                serviceProvider,
                request,
                telemetry,
                effectiveCancellationToken).ConfigureAwait(false);

            await foreach (var item in Enumerate(
                serviceProvider,
                request,
                initialStream.Stream,
                !initialStream.IsReplacement,
                telemetry,
                effectiveCancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            linkedCancellation?.Dispose();
        }
    }

    /// <summary>
    /// Runs preprocessors and builds the behavior chain, recovering from a failure if an exception handler allows it.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The stream request to process.</param>
    /// <param name="telemetry">The telemetry scope for this enumeration, if telemetry is enabled.</param>
    /// <param name="cancellationToken">The enumeration's cancellation token.</param>
    /// <returns>The stream to enumerate, and whether it is already a replacement.</returns>
    private static async Task<(IAsyncEnumerable<TResponse> Stream, bool IsReplacement)> CreateInitialStream(
        IServiceProvider serviceProvider,
        TRequest request,
        MediatorInstrumentation.OperationScope? telemetry,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var preProcessor in ServiceProviderUtilities.GetServices<IRequestPreProcessor<TRequest>>(serviceProvider))
            {
                await preProcessor.Process(request, cancellationToken).ConfigureAwait(false);
            }

            var behaviors = ServiceProviderUtilities.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>(serviceProvider);
            var stream = behaviors.Length == 0
                ? InvokeHandler(serviceProvider, request, cancellationToken)
                : new PipelineContext(serviceProvider, request, behaviors).InvokeAt(0, cancellationToken);

            return (stream, false);
        }
        catch (Exception exception)
        {
            var replacement = await ProcessFailure(
                serviceProvider,
                request,
                exception,
                canRecover: true,
                processingFailures: null,
                telemetry,
                cancellationToken).ConfigureAwait(false);

            return (replacement, true);
        }
    }

    /// <summary>
    /// Enumerates the stream, replacing it at most once when an exception handler recovers from a failure.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve exception components.</param>
    /// <param name="request">The stream request being processed.</param>
    /// <param name="initialStream">The stream to enumerate first.</param>
    /// <param name="canRecover">Whether an exception handler may still replace the stream.</param>
    /// <param name="telemetry">The telemetry scope for this enumeration, if telemetry is enabled.</param>
    /// <param name="cancellationToken">The enumeration's cancellation token.</param>
    /// <returns>The streamed response payloads.</returns>
    private static async IAsyncEnumerable<TResponse> Enumerate(
        IServiceProvider serviceProvider,
        TRequest request,
        IAsyncEnumerable<TResponse> initialStream,
        bool canRecover,
        MediatorInstrumentation.OperationScope? telemetry,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var activeStream = initialStream;

        while (true)
        {
            Exception? failure;
            List<Exception>? cleanupFailures = null;
            IAsyncEnumerator<TResponse>? enumerator;

            try
            {
                enumerator = activeStream.GetAsyncEnumerator(cancellationToken);
                failure = null;
            }
            catch (Exception exception)
            {
                enumerator = null;
                failure = exception;
            }

            if (enumerator is not null)
            {
                var completed = false;

                try
                {
                    while (true)
                    {
                        var moveNext = await TryMoveNext(enumerator).ConfigureAwait(false);

                        if (moveNext.Exception is not null)
                        {
                            failure = moveNext.Exception;

                            break;
                        }

                        if (!moveNext.HasValue)
                        {
                            completed = true;

                            break;
                        }

                        yield return moveNext.Value;
                    }
                }
                finally
                {
                    // A stream that completed or that the consumer stopped early is disposed here, and a disposal
                    // failure propagates. A failed stream is disposed below, before recovery opens a replacement.
                    if (failure is null)
                    {
                        try
                        {
                            await enumerator.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception disposeFailure) when (RecordFailure(telemetry, disposeFailure, cancellationToken))
                        {
                            // Not reached: the filter records the failure and lets it propagate.
                        }
                    }
                }

                if (completed)
                {
                    yield break;
                }

                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeFailure)
                {
                    cleanupFailures = [disposeFailure];
                }
            }

            activeStream = await ProcessFailure(
                serviceProvider,
                request,
                failure!,
                canRecover,
                cleanupFailures,
                telemetry,
                cancellationToken).ConfigureAwait(false);
            canRecover = false;
        }
    }

    /// <summary>
    /// Processes a stream failure and records it on the telemetry scope when it is rethrown.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve exception components.</param>
    /// <param name="request">The stream request that failed.</param>
    /// <param name="exception">The exception the stream failed with.</param>
    /// <param name="canRecover">Whether an exception handler may still replace the stream.</param>
    /// <param name="processingFailures">Failures that already happened while cleaning up the failed stream, if any.</param>
    /// <param name="telemetry">The telemetry scope for this enumeration, if telemetry is enabled.</param>
    /// <param name="cancellationToken">The enumeration's cancellation token.</param>
    /// <returns>A task that resolves to the replacement stream.</returns>
    private static async Task<IAsyncEnumerable<TResponse>> ProcessFailure(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        bool canRecover,
        List<Exception>? processingFailures,
        MediatorInstrumentation.OperationScope? telemetry,
        CancellationToken cancellationToken)
    {
        try
        {
            return await StreamRequestExceptionProcessor<TRequest, TResponse>.Process(
                serviceProvider,
                request,
                exception,
                canRecover,
                processingFailures,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception rethrown)
        {
            telemetry?.Fail(rethrown, cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Records a failure on the telemetry scope from an exception filter, without catching it.
    /// </summary>
    /// <param name="telemetry">The telemetry scope for this enumeration, if telemetry is enabled.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="cancellationToken">The enumeration's cancellation token.</param>
    /// <returns>Always <see langword="false"/>, so the exception keeps propagating.</returns>
    private static bool RecordFailure(
        MediatorInstrumentation.OperationScope? telemetry,
        Exception exception,
        CancellationToken cancellationToken)
    {
        telemetry?.Fail(exception, cancellationToken);

        return false;
    }

    /// <summary>
    /// Advances an enumerator and captures any failure instead of throwing it.
    /// </summary>
    /// <param name="enumerator">The enumerator to advance.</param>
    /// <returns>The next value, completion, or the failure.</returns>
    private static async ValueTask<MoveNextResult> TryMoveNext(IAsyncEnumerator<TResponse> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false)
                ? new MoveNextResult(true, enumerator.Current, null)
                : default;
        }
        catch (Exception exception)
        {
            return new MoveNextResult(false, default!, exception);
        }
    }

    /// <summary>
    /// Resolves the stream handler and creates its stream.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve the handler.</param>
    /// <param name="request">The stream request to handle.</param>
    /// <param name="cancellationToken">The cancellation token passed to the handler.</param>
    /// <returns>The handler's response stream.</returns>
    private static IAsyncEnumerable<TResponse> InvokeHandler(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetService(typeof(IStreamRequestHandler<TRequest, TResponse>)) as IStreamRequestHandler<TRequest, TResponse>
            ?? throw new InvalidOperationException(
                MediatorErrors.MissingStreamRequestHandlerRegistration(typeof(TRequest), typeof(TResponse)));

        return handler.Handle(request, cancellationToken);
    }

    /// <summary>
    /// Represents the result of advancing an enumerator.
    /// </summary>
    /// <param name="HasValue">Whether the enumerator produced a value.</param>
    /// <param name="Value">The produced value.</param>
    /// <param name="Exception">The failure, if advancing threw.</param>
    private readonly record struct MoveNextResult(bool HasValue, TResponse Value, Exception? Exception);

    /// <summary>
    /// Holds the state shared by every link in one enumeration's stream behavior chain.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve the handler.</param>
    /// <param name="request">The stream request being processed.</param>
    /// <param name="behaviors">The resolved stream behaviors, outermost first.</param>
    private sealed class PipelineContext(
        IServiceProvider serviceProvider,
        TRequest request,
        IStreamPipelineBehavior<TRequest, TResponse>[] behaviors)
    {
        /// <summary>
        /// Invokes the behavior at <paramref name="index"/>, or the handler once every behavior has run.
        /// </summary>
        /// <param name="index">The index of the behavior to invoke.</param>
        /// <param name="cancellationToken">The cancellation token for this part of the pipeline.</param>
        /// <returns>The response stream.</returns>
        public IAsyncEnumerable<TResponse> InvokeAt(int index, CancellationToken cancellationToken)
        {
            if (index == behaviors.Length)
            {
                return InvokeHandler(serviceProvider, request, cancellationToken);
            }

            var next = new PipelineLink(this, index + 1, cancellationToken);

            return behaviors[index].Handle(request, next.Invoke, cancellationToken);
        }
    }

    /// <summary>
    /// The <c>next</c> delegate target passed to one stream behavior.
    /// </summary>
    /// <param name="context">The pipeline state.</param>
    /// <param name="index">The index of the behavior or handler that <c>next</c> invokes.</param>
    /// <param name="inheritedCancellationToken">The token the calling behavior received.</param>
    private sealed class PipelineLink(PipelineContext context, int index, CancellationToken inheritedCancellationToken)
    {
        /// <summary>
        /// Invokes the rest of the pipeline.
        /// </summary>
        /// <param name="cancellationToken">
        /// The token for the rest of the pipeline, or <see langword="default"/> to keep the calling behavior's token.
        /// </param>
        /// <returns>The response stream.</returns>
        public IAsyncEnumerable<TResponse> Invoke(CancellationToken cancellationToken)
        {
            return context.InvokeAt(
                index,
                cancellationToken == default ? inheritedCancellationToken : cancellationToken);
        }
    }
}