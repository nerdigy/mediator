using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Executes stream request processing pipelines for a concrete request and response pair.
/// </summary>
/// <typeparam name="TRequest">The concrete stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
internal static class StreamRequestPipelineExecutor<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Executes preprocessors, stream pipeline behaviors, handler logic, and stream exception policies.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed during execution.</param>
    /// <param name="handlerFactory">The terminal stream handler delegate factory.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    public static IAsyncEnumerable<TResponse> Execute(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, IAsyncEnumerable<TResponse>> handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handlerFactory);

        return ExecuteCore(serviceProvider, request, cancellationToken, handlerFactory);
    }

    /// <summary>
    /// Executes the stream pipeline and materializes the final sequence.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The request-level cancellation token.</param>
    /// <param name="handlerFactory">The terminal stream handler delegate factory.</param>
    /// <param name="enumerationCancellationToken">The enumeration-level cancellation token.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    private static async IAsyncEnumerable<TResponse> ExecuteCore(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, IAsyncEnumerable<TResponse>> handlerFactory,
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

        try
        {
            var initialStream = await TryBuildInitialStream(
                serviceProvider,
                request,
                effectiveCancellationToken,
                handlerFactory).ConfigureAwait(false);

            await foreach (var item in EnumerateWithExceptionHandling(
                serviceProvider,
                request,
                initialStream.Stream,
                !initialStream.IsReplacement,
                effectiveCancellationToken))
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
    /// Builds the initial stream by running preprocessors and pipeline behaviors.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="handlerFactory">The terminal stream handler delegate factory.</param>
    /// <returns>
    /// A task that resolves to the initial stream, and a value that is <see langword="true"/> when an exception handler supplied that stream.
    /// </returns>
    private static async Task<(IAsyncEnumerable<TResponse> Stream, bool IsReplacement)> TryBuildInitialStream(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, IAsyncEnumerable<TResponse>> handlerFactory)
    {
        try
        {
            await ExecutePreProcessors(serviceProvider, request, cancellationToken).ConfigureAwait(false);
            var pipeline = BuildPipeline(serviceProvider, request, cancellationToken, handlerFactory);

            return (pipeline(), false);
        }
        catch (Exception exception)
        {
            var handlingResult = await StreamRequestExceptionProcessor<TRequest, TResponse>.TryHandle(
                serviceProvider,
                request,
                exception,
                cancellationToken).ConfigureAwait(false);

            if (handlingResult.Handled)
            {
                return (handlingResult.ResponseStream, true);
            }

            await StreamRequestExceptionProcessor<TRequest, TResponse>.ExecuteActions(
                serviceProvider,
                request,
                exception,
                cancellationToken).ConfigureAwait(false);
            StreamRequestExceptionProcessor<TRequest, TResponse>.Rethrow(exception);

            throw;
        }
    }

    /// <summary>
    /// Enumerates a stream and applies stream exception handling for enumeration failures.
    /// </summary>
    /// <remarks>
    /// Exception handlers can replace a stream one time only. When a replacement stream fails, exception actions run and
    /// the exception propagates. This rule prevents an infinite recovery loop when the replacement stream also fails.
    /// </remarks>
    /// <param name="serviceProvider">The service provider used to resolve stream exception services.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="initialStream">The initial stream to enumerate.</param>
    /// <param name="canRecover">Whether exception handlers can still replace the active stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of streamed response payloads.</returns>
    private static async IAsyncEnumerable<TResponse> EnumerateWithExceptionHandling(
        IServiceProvider serviceProvider,
        TRequest request,
        IAsyncEnumerable<TResponse> initialStream,
        bool canRecover,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var activeStream = initialStream;

        while (true)
        {
            var enumeratorResult = TryCreateEnumerator(activeStream, cancellationToken);

            if (enumeratorResult.Exception is not null)
            {
                activeStream = await HandleOrRethrow(
                    serviceProvider,
                    request,
                    enumeratorResult.Exception,
                    canRecover,
                    cancellationToken).ConfigureAwait(false);
                canRecover = false;

                continue;
            }

            await using var enumerator = enumeratorResult.Enumerator!;

            while (true)
            {
                var moveNextResult = await TryMoveNext(enumerator).ConfigureAwait(false);

                if (moveNextResult.Exception is not null)
                {
                    activeStream = await HandleOrRethrow(
                        serviceProvider,
                        request,
                        moveNextResult.Exception,
                        canRecover,
                        cancellationToken).ConfigureAwait(false);
                    canRecover = false;

                    break;
                }

                if (!moveNextResult.HasValue)
                {
                    yield break;
                }

                yield return moveNextResult.Value;
            }
        }
    }

    /// <summary>
    /// Applies stream exception handlers to a failure, or runs exception actions and rethrows the failure.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve stream exception services.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="exception">The exception to process.</param>
    /// <param name="canRecover">Whether exception handlers can replace the active stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the replacement stream supplied by an exception handler.</returns>
    private static async Task<IAsyncEnumerable<TResponse>> HandleOrRethrow(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        bool canRecover,
        CancellationToken cancellationToken)
    {
        if (canRecover)
        {
            var handlingResult = await StreamRequestExceptionProcessor<TRequest, TResponse>.TryHandle(
                serviceProvider,
                request,
                exception,
                cancellationToken).ConfigureAwait(false);

            if (handlingResult.Handled)
            {
                return handlingResult.ResponseStream;
            }
        }

        await StreamRequestExceptionProcessor<TRequest, TResponse>.ExecuteActions(
            serviceProvider,
            request,
            exception,
            cancellationToken).ConfigureAwait(false);
        StreamRequestExceptionProcessor<TRequest, TResponse>.Rethrow(exception);

        throw new InvalidOperationException("Exception rethrow unexpectedly returned control to the caller.");
    }

    /// <summary>
    /// Creates an async enumerator while capturing creation exceptions.
    /// </summary>
    /// <param name="stream">The stream to enumerate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The enumerator creation result.</returns>
    private static EnumeratorCreationResult TryCreateEnumerator(IAsyncEnumerable<TResponse> stream, CancellationToken cancellationToken)
    {
        try
        {
            var enumerator = stream.GetAsyncEnumerator(cancellationToken);

            return EnumeratorCreationResult.FromEnumerator(enumerator);
        }
        catch (Exception exception)
        {
            return EnumeratorCreationResult.FromException(exception);
        }
    }

    /// <summary>
    /// Moves an enumerator forward and reads the current value while capturing exceptions.
    /// </summary>
    /// <param name="enumerator">The enumerator to advance.</param>
    /// <returns>The move-next result.</returns>
    private static async ValueTask<MoveNextResult> TryMoveNext(IAsyncEnumerator<TResponse> enumerator)
    {
        try
        {
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                return MoveNextResult.Completed;
            }

            return MoveNextResult.FromValue(enumerator.Current);
        }
        catch (Exception exception)
        {
            return MoveNextResult.FromException(exception);
        }
    }

    /// <summary>
    /// Executes registered request preprocessors.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve preprocessors.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when all preprocessors have run.</returns>
    private static async Task ExecutePreProcessors(IServiceProvider serviceProvider, TRequest request, CancellationToken cancellationToken)
    {
        var preprocessors = ServiceProviderUtilities.GetServices<IRequestPreProcessor<TRequest>>(serviceProvider);

        foreach (var preprocessor in preprocessors)
        {
            await preprocessor.Process(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a stream processing delegate including stream pipeline behaviors.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve pipeline services.</param>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="handlerFactory">The terminal stream handler delegate factory.</param>
    /// <returns>The composed stream processing delegate.</returns>
    private static StreamHandlerDelegate<TResponse> BuildPipeline(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, IAsyncEnumerable<TResponse>> handlerFactory)
    {
        var behaviors = ServiceProviderUtilities.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>(serviceProvider);

        StreamHandlerDelegate<TResponse> current = () => handlerFactory(cancellationToken);

        if (behaviors is IList<IStreamPipelineBehavior<TRequest, TResponse>> behaviorList)
        {
            for (var index = behaviorList.Count - 1; index >= 0; index--)
            {
                var behavior = behaviorList[index];
                var next = current;
                current = () => behavior.Handle(request, next, cancellationToken);
            }

            return current;
        }

        var behaviorArray = behaviors.ToArray();

        for (var index = behaviorArray.Length - 1; index >= 0; index--)
        {
            var behavior = behaviorArray[index];
            var next = current;
            current = () => behavior.Handle(request, next, cancellationToken);
        }

        return current;
    }

    /// <summary>
    /// Represents the result of enumerator creation.
    /// </summary>
    private readonly record struct EnumeratorCreationResult
    {
        /// <summary>
        /// Gets the created enumerator instance.
        /// </summary>
        public IAsyncEnumerator<TResponse>? Enumerator { get; init; }

        /// <summary>
        /// Gets the exception thrown during enumerator creation.
        /// </summary>
        public Exception? Exception { get; init; }

        /// <summary>
        /// Creates a successful enumerator-creation result.
        /// </summary>
        /// <param name="enumerator">The created enumerator.</param>
        /// <returns>A successful result.</returns>
        public static EnumeratorCreationResult FromEnumerator(IAsyncEnumerator<TResponse> enumerator)
        {
            ArgumentNullException.ThrowIfNull(enumerator);

            return new EnumeratorCreationResult
            {
                Enumerator = enumerator,
                Exception = null
            };
        }

        /// <summary>
        /// Creates a failed enumerator-creation result.
        /// </summary>
        /// <param name="exception">The creation exception.</param>
        /// <returns>A failed result.</returns>
        public static EnumeratorCreationResult FromException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new EnumeratorCreationResult
            {
                Enumerator = null,
                Exception = exception
            };
        }
    }

    /// <summary>
    /// Represents the result of a move-next operation.
    /// </summary>
    private readonly record struct MoveNextResult
    {
        /// <summary>
        /// Gets a result that indicates the enumerator has no more values.
        /// </summary>
        public static MoveNextResult Completed => default;

        /// <summary>
        /// Gets a value indicating whether the enumerator advanced to a value.
        /// </summary>
        public bool HasValue { get; init; }

        /// <summary>
        /// Gets the current value when <see cref="HasValue"/> is <see langword="true"/>.
        /// </summary>
        public TResponse Value { get; init; }

        /// <summary>
        /// Gets the exception thrown during move-next.
        /// </summary>
        public Exception? Exception { get; init; }

        /// <summary>
        /// Creates a successful move-next result that carries a value.
        /// </summary>
        /// <param name="value">The current value.</param>
        /// <returns>A successful result.</returns>
        public static MoveNextResult FromValue(TResponse value)
        {
            return new MoveNextResult
            {
                HasValue = true,
                Value = value,
                Exception = null
            };
        }

        /// <summary>
        /// Creates a failed move-next result.
        /// </summary>
        /// <param name="exception">The move-next exception.</param>
        /// <returns>A failed result.</returns>
        public static MoveNextResult FromException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new MoveNextResult
            {
                HasValue = false,
                Value = default!,
                Exception = exception
            };
        }
    }
}