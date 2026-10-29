using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies the limits of stream exception recovery for create-stream operations.
/// </summary>
public sealed class MediatorStreamRecoveryTests
{
    /// <summary>
    /// Verifies a failing replacement stream propagates its exception instead of starting a second recovery.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenReplacementStreamFails_PropagatesWithoutSecondRecovery()
    {
        var replacementFailure = new InvalidOperationException("replacement failure");
        var exceptionHandler = new ReplacingStreamExceptionHandler(() => FailAfter([2], replacementFailure));
        var exceptionAction = new CountingStreamExceptionAction();
        var mediator = CreateMediator(
            () => FailAfter([1], new InvalidOperationException("original failure")),
            exceptionHandler,
            exceptionAction);

        List<int> values = [];
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(mediator.CreateStream(new StreamRecoveryRequest(), CancellationToken.None), values)
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(replacementFailure, exception);
        Assert.Equal([1, 2], values);
        Assert.Equal(1, exceptionHandler.Calls);
        Assert.Equal(1, exceptionAction.Calls);
    }

    /// <summary>
    /// Verifies a failing replacement stream supplied for a preprocessor failure propagates its exception.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenReplacementForPreProcessorFailureFails_PropagatesWithoutSecondRecovery()
    {
        var replacementFailure = new InvalidOperationException("replacement failure");
        var exceptionHandler = new ReplacingStreamExceptionHandler(() => FailAfter([], replacementFailure));
        var exceptionAction = new CountingStreamExceptionAction();
        var mediator = CreateMediator(
            () => FailAfter([1], new InvalidOperationException("unused")),
            exceptionHandler,
            exceptionAction,
            new ThrowingPreProcessor(new InvalidOperationException("preprocessor failure")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(mediator.CreateStream(new StreamRecoveryRequest(), CancellationToken.None), [])
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(replacementFailure, exception);
        Assert.Equal(1, exceptionHandler.Calls);
        Assert.Equal(1, exceptionAction.Calls);
    }

    /// <summary>
    /// Verifies caller cancellation is not passed to a catch-all stream exception handler, while exception actions still run.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenCallerCanceledWithCatchAllHandler_SkipsRecoveryAndRunsActions()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var exceptionHandler = new ReplacingStreamExceptionHandler(() => ObserveCancellation(cancellation.Token));
        var action = new CountingStreamExceptionAction();
        var mediator = CreateMediator(
            () => ObserveCancellation(cancellation.Token),
            exceptionHandler,
            action);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(mediator.CreateStream(new StreamRecoveryRequest(), cancellation.Token), [])
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(0, exceptionHandler.Calls);
        Assert.Equal(1, action.Calls);
    }

    /// <summary>
    /// Verifies an exception from the enumerator's current value goes through stream exception handlers.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenCurrentThrows_AppliesExceptionHandlers()
    {
        var exceptionHandler = new ReplacingStreamExceptionHandler(() => FailAfter([9], null));
        var mediator = CreateMediator(
            () => new FaultyStream(new InvalidOperationException("current failure"), null),
            exceptionHandler,
            new CountingStreamExceptionAction());

        List<int> values = [];
        await CollectAsync(mediator.CreateStream(new StreamRecoveryRequest(), CancellationToken.None), values);

        Assert.Equal([9], values);
        Assert.Equal(1, exceptionHandler.Calls);
    }

    /// <summary>
    /// Verifies an exception from enumerator disposal propagates without stream exception handlers or actions.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenDisposeThrows_PropagatesWithoutExceptionHandlers()
    {
        var disposeFailure = new InvalidOperationException("dispose failure");
        var exceptionHandler = new ReplacingStreamExceptionHandler(() => FailAfter([9], null));
        var exceptionAction = new CountingStreamExceptionAction();
        var mediator = CreateMediator(
            () => new FaultyStream(null, disposeFailure),
            exceptionHandler,
            exceptionAction);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectAsync(mediator.CreateStream(new StreamRecoveryRequest(), CancellationToken.None), []));

        Assert.Same(disposeFailure, exception);
        Assert.Equal(0, exceptionHandler.Calls);
        Assert.Equal(0, exceptionAction.Calls);
    }

    /// <summary>
    /// Verifies a failed stream is disposed before an exception handler opens its replacement.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenRecovering_DisposesFailedStreamBeforeCreatingReplacement()
    {
        List<string> events = [];
        var exceptionHandler = new ReplacingStreamExceptionHandler(() =>
        {
            events.Add("recover");

            return FailAfter([9], null);
        });
        var mediator = CreateMediator(
            () => new FaultyStream(new InvalidOperationException("current failure"), null, () => events.Add("dispose")),
            exceptionHandler,
            new CountingStreamExceptionAction());

        List<int> values = [];
        await CollectAsync(mediator.CreateStream(new StreamRecoveryRequest(), CancellationToken.None), values);

        Assert.Equal([9], values);
        Assert.Equal(["dispose", "recover"], events);
    }

    /// <summary>
    /// Verifies a failure while disposing a failed stream does not hide the original failure or allow recovery.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenFailedStreamDisposeThrows_WrapsOriginalAndSkipsRecovery()
    {
        var original = new InvalidOperationException("current failure");
        var disposeFailure = new FormatException("dispose failure");
        var exceptionHandler = new ReplacingStreamExceptionHandler(() => FailAfter([9], null));
        var exceptionAction = new CountingStreamExceptionAction();
        var mediator = CreateMediator(
            () => new FaultyStream(original, disposeFailure),
            exceptionHandler,
            exceptionAction);

        var exception = await Assert.ThrowsAsync<MediatorExceptionProcessingException>(
            () => CollectAsync(mediator.CreateStream(new StreamRecoveryRequest(), CancellationToken.None), []));

        Assert.Same(original, exception.InnerException);
        Assert.Same(disposeFailure, Assert.Single(exception.ProcessingExceptions));
        Assert.Equal(0, exceptionHandler.Calls);
        Assert.Equal(1, exceptionAction.Calls);
    }

    /// <summary>
    /// Creates a mediator with one stream handler and the supplied exception components.
    /// </summary>
    /// <param name="streamFactory">The factory that creates the handler stream.</param>
    /// <param name="exceptionHandler">The catch-all stream exception handler.</param>
    /// <param name="exceptionAction">The catch-all exception action.</param>
    /// <param name="preProcessor">An optional request preprocessor.</param>
    /// <returns>The configured mediator.</returns>
    private static MediatorRuntime CreateMediator(
        Func<IAsyncEnumerable<int>> streamFactory,
        ReplacingStreamExceptionHandler exceptionHandler,
        CountingStreamExceptionAction exceptionAction,
        IRequestPreProcessor<StreamRecoveryRequest>? preProcessor = null)
    {
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamRecoveryRequest, int>), new DelegateStreamHandler(streamFactory)),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<StreamRecoveryRequest, int, Exception>>), new IStreamRequestExceptionHandler<StreamRecoveryRequest, int, Exception>[] { exceptionHandler }),
            (typeof(IEnumerable<IRequestExceptionAction<StreamRecoveryRequest, Exception>>), new IRequestExceptionAction<StreamRecoveryRequest, Exception>[] { exceptionAction }),
            (typeof(IEnumerable<IRequestPreProcessor<StreamRecoveryRequest>>), preProcessor is null ? Array.Empty<IRequestPreProcessor<StreamRecoveryRequest>>() : new[] { preProcessor }));

        return new MediatorRuntime(provider);
    }

    /// <summary>
    /// Enumerates a stream and adds each value to a list.
    /// </summary>
    /// <param name="source">The stream to enumerate.</param>
    /// <param name="values">The list that receives the values.</param>
    /// <returns>A task that completes when enumeration ends.</returns>
    private static async Task CollectAsync(IAsyncEnumerable<int> source, List<int> values)
    {
        await foreach (var value in source)
        {
            values.Add(value);
        }
    }

    /// <summary>
    /// Yields the supplied values, then throws the supplied exception when one is given.
    /// </summary>
    /// <param name="values">The values to yield.</param>
    /// <param name="exception">The exception to throw after the values, or <see langword="null"/> to complete.</param>
    /// <returns>An asynchronous sequence of values.</returns>
    private static async IAsyncEnumerable<int> FailAfter(int[] values, Exception? exception)
    {
        foreach (var value in values)
        {
            await Task.Yield();

            yield return value;
        }

        if (exception is not null)
        {
            throw exception;
        }
    }

    /// <summary>
    /// Produces a stream that throws when the supplied token is canceled.
    /// </summary>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>An asynchronous sequence that throws on cancellation.</returns>
    private static async IAsyncEnumerable<int> ObserveCancellation([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return 1;
    }

    /// <summary>
    /// Represents a sample stream request for recovery tests.
    /// </summary>
    private sealed record StreamRecoveryRequest : IStreamRequest<int>;

    /// <summary>
    /// Returns the stream produced by a factory.
    /// </summary>
    private sealed class DelegateStreamHandler : IStreamRequestHandler<StreamRecoveryRequest, int>
    {
        private readonly Func<IAsyncEnumerable<int>> _streamFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="DelegateStreamHandler"/> class.
        /// </summary>
        /// <param name="streamFactory">The factory that creates the stream.</param>
        public DelegateStreamHandler(Func<IAsyncEnumerable<int>> streamFactory)
        {
            _streamFactory = streamFactory;
        }

        /// <summary>
        /// Handles a stream request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The stream produced by the factory.</returns>
        public IAsyncEnumerable<int> Handle(StreamRecoveryRequest request, CancellationToken cancellationToken)
        {
            return _streamFactory();
        }
    }

    /// <summary>
    /// Handles every exception with a replacement stream and counts its calls.
    /// </summary>
    private sealed class ReplacingStreamExceptionHandler : IStreamRequestExceptionHandler<StreamRecoveryRequest, int, Exception>
    {
        private readonly Func<IAsyncEnumerable<int>> _replacementFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReplacingStreamExceptionHandler"/> class.
        /// </summary>
        /// <param name="replacementFactory">The factory that creates each replacement stream.</param>
        public ReplacingStreamExceptionHandler(Func<IAsyncEnumerable<int>> replacementFactory)
        {
            _replacementFactory = replacementFactory;
        }

        /// <summary>
        /// Gets the number of times this handler ran.
        /// </summary>
        public int Calls { get; private set; }

        /// <summary>
        /// Handles an exception and supplies a replacement stream.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="state">The mutable exception handling state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            StreamRecoveryRequest request,
            Exception exception,
            StreamRequestExceptionHandlerState<int> state,
            CancellationToken cancellationToken)
        {
            Calls++;
            state.SetHandled(_replacementFactory());

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Counts exception action calls for every exception type.
    /// </summary>
    private sealed class CountingStreamExceptionAction : IRequestExceptionAction<StreamRecoveryRequest, Exception>
    {
        /// <summary>
        /// Gets the number of times this action ran.
        /// </summary>
        public int Calls { get; private set; }

        /// <summary>
        /// Executes action logic for an unhandled stream exception.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Execute(StreamRecoveryRequest request, Exception exception, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws a supplied exception before the stream is created.
    /// </summary>
    private sealed class ThrowingPreProcessor : IRequestPreProcessor<StreamRecoveryRequest>
    {
        private readonly Exception _exception;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThrowingPreProcessor"/> class.
        /// </summary>
        /// <param name="exception">The exception to throw.</param>
        public ThrowingPreProcessor(Exception exception)
        {
            _exception = exception;
        }

        /// <summary>
        /// Throws the configured exception.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>This method does not return.</returns>
        public Task Process(StreamRecoveryRequest request, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    /// <summary>
    /// Produces one value through an enumerator that can throw from <see cref="IAsyncEnumerator{T}.Current"/> or disposal.
    /// </summary>
    private sealed class FaultyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly Exception? _currentException;
        private readonly Exception? _disposeException;
        private readonly Action? _onDispose;
        private bool _hasMoved;

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultyStream"/> class.
        /// </summary>
        /// <param name="currentException">The exception thrown when the current value is read, or <see langword="null"/>.</param>
        /// <param name="disposeException">The exception thrown on disposal, or <see langword="null"/>.</param>
        /// <param name="onDispose">Called when the stream is disposed, or <see langword="null"/>.</param>
        public FaultyStream(Exception? currentException, Exception? disposeException, Action? onDispose = null)
        {
            _currentException = currentException;
            _disposeException = disposeException;
            _onDispose = onDispose;
        }

        /// <summary>
        /// Gets the current value, or throws the configured exception.
        /// </summary>
        public int Current => _currentException is null ? 1 : throw _currentException;

        /// <summary>
        /// Returns this instance as the enumerator.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>This instance.</returns>
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return this;
        }

        /// <summary>
        /// Advances to the single value, then completes.
        /// </summary>
        /// <returns><see langword="true"/> on the first call; otherwise, <see langword="false"/>.</returns>
        public ValueTask<bool> MoveNextAsync()
        {
            var hasValue = !_hasMoved;
            _hasMoved = true;

            return ValueTask.FromResult(hasValue);
        }

        /// <summary>
        /// Disposes the enumerator, or throws the configured exception.
        /// </summary>
        /// <returns>A completed task.</returns>
        public ValueTask DisposeAsync()
        {
            _onDispose?.Invoke();

            if (_disposeException is not null)
            {
                throw _disposeException;
            }

            return ValueTask.CompletedTask;
        }
    }
}