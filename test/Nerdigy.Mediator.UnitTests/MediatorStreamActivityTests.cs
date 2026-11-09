using System.Diagnostics;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies that a stream's activity is current whenever the pipeline runs on behalf of the consumer, and that the
/// consumer's own current activity never changes.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class MediatorStreamActivityTests
{
    /// <summary>
    /// Verifies a recovery on item 2 marks the stream activity, and that the stream activity is current for the handler,
    /// the exception handler, and the disposal of the failed stream.
    /// </summary>
    /// <param name="suspendItem1">Whether advancing to item 1 suspends.</param>
    /// <param name="suspendItem2">Whether advancing to item 2 suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateStream_WhenRecoveringOnItem2_StreamActivityIsCurrentAndMarked(bool suspendItem1, bool suspendItem2)
    {
        using var recorder = new TelemetryRecorder();
        using var consumerActivity = new Activity("consumer").Start();
        var failed = new ScriptedStream(new Step(1, suspendItem1), new Step(0, suspendItem2, new InvalidOperationException("item 2")));
        var replacement = new ScriptedStream(new Step(9));
        var exceptionHandler = new ReplacingExceptionHandler(replacement);
        var mediator = CreateMediator(failed, exceptionHandler);

        var values = await CollectAsync(mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None));

        Assert.Equal([1, 9], values);
        Assert.Same(consumerActivity, Activity.Current);
        var stream = Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>());
        Assert.Same(consumerActivity, stream.Parent);
        AssertMarkedRecovered(stream);
        Assert.Same(stream, failed.AdvanceObservations[0]);
        Assert.Same(stream, failed.AdvanceObservations[1]);
        Assert.Same(stream, failed.DisposeObservation);
        Assert.Same(stream, exceptionHandler.ObservedActivity);
        Assert.Same(stream, replacement.AdvanceObservations[0]);
        Assert.Same(stream, replacement.DisposeObservation);
    }

    /// <summary>
    /// Verifies a failure on item 1 is recovered on the stream activity.
    /// </summary>
    /// <param name="suspend">Whether advancing suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStream_WhenRecoveringOnItem1_MarksStreamActivity(bool suspend)
    {
        using var recorder = new TelemetryRecorder();
        var failed = new ScriptedStream(new Step(0, suspend, new InvalidOperationException("item 1")));
        var mediator = CreateMediator(failed, new ReplacingExceptionHandler(new ScriptedStream(new Step(9))));

        var values = await CollectAsync(mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None));

        Assert.Equal([9], values);
        AssertMarkedRecovered(Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>()));
    }

    /// <summary>
    /// Verifies a failure while the stream is created is recovered on the stream activity.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenRecoveringWhileCreatingStream_MarksStreamActivity()
    {
        using var recorder = new TelemetryRecorder();
        var exceptionHandler = new ReplacingExceptionHandler(new ScriptedStream(new Step(9)));
        var mediator = CreateMediator(
            new ScriptedStream(new Step(1)),
            exceptionHandler,
            preProcessor: new ThrowingPreProcessor(new InvalidOperationException("creation")));

        var values = await CollectAsync(mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None));

        Assert.Equal([9], values);
        var stream = Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>());
        AssertMarkedRecovered(stream);
        Assert.Same(stream, exceptionHandler.ObservedActivity);
    }

    /// <summary>
    /// Verifies the stream activity is current for exception handling even when failing code left another activity current.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenFailingCodeLeavesAnotherActivityCurrent_ExceptionHandlerSeesStreamActivity()
    {
        using var recorder = new TelemetryRecorder();
        using var strayActivity = new Activity("stray");
        var exceptionHandler = new ReplacingExceptionHandler(new ScriptedStream(new Step(9)));
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamActivityRequest, int>),
                new DelegateStreamHandler(() => new StrayActivityStream(strayActivity))),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>>),
                new IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>[] { exceptionHandler })));

        var values = await CollectAsync(mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None));

        Assert.Equal([9], values);
        Assert.Same(Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>()), exceptionHandler.ObservedActivity);
    }

    /// <summary>
    /// Verifies a failing replacement stream records one recovery, ends the operation with an error, and leaves the
    /// consumer's activity current.
    /// </summary>
    /// <param name="suspend">Whether the replacement's advance suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStream_WhenReplacementFails_RecordsOneRecoveryAndError(bool suspend)
    {
        using var recorder = new TelemetryRecorder();
        using var consumerActivity = new Activity("consumer").Start();
        var failure = new FormatException("replacement");
        var replacement = new ScriptedStream(new Step(9), new Step(0, suspend, failure));
        var mediator = CreateMediator(
            new ScriptedStream(new Step(0, suspend, new InvalidOperationException("original"))),
            new ReplacingExceptionHandler(replacement));

        List<int> values = [];
        var thrown = await Assert.ThrowsAsync<FormatException>(
            () => CollectInto(mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None), values));

        Assert.Same(failure, thrown);
        Assert.Equal([9], values);
        Assert.Same(consumerActivity, Activity.Current);
        var stream = Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>());
        Assert.Single(stream.Events, static activityEvent => activityEvent.Name == "nerdigy.mediator.exception_handled");
        Assert.Equal(ActivityStatusCode.Error, stream.Status);
        Assert.Same(stream, replacement.AdvanceObservations[1]);
        var duration = Assert.Single(recorder.MeasurementsFor<StreamActivityRequest>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("error", duration.Tags[MediatorTelemetry.OutcomeTag]);
    }

    /// <summary>
    /// Verifies an activity that handler code starts while producing item 2 is a child of the stream activity.
    /// </summary>
    /// <param name="suspend">Whether advancing suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStream_WhenHandlerStartsActivityOnItem2_ParentIsStreamActivity(bool suspend)
    {
        using var recorder = new TelemetryRecorder();
        using var consumerActivity = new Activity("consumer").Start();
        Activity? child = null;
        var handlerStream = new ScriptedStream(
            new Step(1, suspend),
            new Step(2, suspend, OnAdvance: () => child = new Activity("handler-work").Start()));
        var mediator = CreateMediator(handlerStream);

        var values = await CollectAsync(mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None));

        Assert.Equal([1, 2], values);
        Assert.NotNull(child);
        var stream = Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>());
        Assert.Equal(stream.Id, child.ParentId);
        child.Dispose();
    }

    /// <summary>
    /// Verifies the consumer's current activity is unchanged after every <c>MoveNextAsync</c> and after <c>DisposeAsync</c>.
    /// </summary>
    /// <param name="suspend">Whether advancing suspends.</param>
    /// <param name="readToEnd">Whether the consumer reads every item before disposing.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task CreateStream_WhenEnumerating_ConsumerActivityIsUnchanged(bool suspend, bool readToEnd)
    {
        using var recorder = new TelemetryRecorder();
        using var consumerActivity = new Activity("consumer").Start();
        var handlerStream = new ScriptedStream(new Step(1, suspend), new Step(2, suspend), new Step(3, suspend));
        var mediator = CreateMediator(handlerStream);

        var enumerator = mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None).GetAsyncEnumerator();

        try
        {
            var items = 0;

            while (await enumerator.MoveNextAsync())
            {
                Assert.Same(consumerActivity, Activity.Current);
                items++;

                if (!readToEnd && items == 1)
                {
                    break;
                }
            }

            Assert.Same(consumerActivity, Activity.Current);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        Assert.Same(consumerActivity, Activity.Current);
        var stream = Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>());
        Assert.Same(stream, handlerStream.DisposeObservation);
    }

    /// <summary>
    /// Verifies the consumer's current activity is unchanged after an early <c>break</c> from <c>await foreach</c>.
    /// </summary>
    /// <param name="suspend">Whether advancing suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStream_WhenConsumerBreaks_ConsumerActivityIsUnchangedAndStreamActivityIsCurrentForDisposal(bool suspend)
    {
        using var recorder = new TelemetryRecorder();
        using var consumerActivity = new Activity("consumer").Start();
        var handlerStream = new ScriptedStream(new Step(1, suspend), new Step(2, suspend));
        var mediator = CreateMediator(handlerStream);

        await foreach (var value in mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None))
        {
            Assert.Same(consumerActivity, Activity.Current);

            break;
        }

        Assert.Same(consumerActivity, Activity.Current);
        Assert.Same(Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>()), handlerStream.DisposeObservation);
    }

    /// <summary>
    /// Verifies a stream consumed inside a mediator operation marks the stream activity and not the enclosing mediator activity.
    /// </summary>
    /// <param name="suspend">Whether advancing suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStream_WhenConsumedInsideSend_MarksOnlyTheStreamActivity(bool suspend)
    {
        using var recorder = new TelemetryRecorder();
        var failed = new ScriptedStream(new Step(1, suspend), new Step(0, suspend, new InvalidOperationException("item 2")));
        var provider = new DeferredServiceProvider();
        var mediator = new MediatorRuntime(provider);
        provider.Inner = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamActivityRequest, int>), new ScriptedStreamHandler(failed)),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>>),
                new IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>[]
                {
                    new ReplacingExceptionHandler(new ScriptedStream(new Step(9))),
                }),
            (typeof(IRequestHandler<ConsumeStreamRequest, int[]>), new ConsumingHandler(mediator)));

        var values = await mediator.Send(new ConsumeStreamRequest(), CancellationToken.None);

        Assert.Equal([1, 9], values);
        var send = Assert.Single(recorder.ActivitiesFor<ConsumeStreamRequest>());
        var stream = Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>());
        Assert.Same(send, stream.Parent);
        Assert.Null(send.GetTagItem(MediatorTelemetry.ExceptionHandledTag));
        Assert.Empty(send.Events);
        AssertMarkedRecovered(stream);
    }

    /// <summary>
    /// Verifies a recovery on a stream whose activity the sampler dropped does not tag the enclosing mediator activity.
    /// </summary>
    /// <param name="suspend">Whether advancing suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStream_WhenSamplerDropsStreamActivityInsideSend_DoesNotTagEnclosingActivity(bool suspend)
    {
        using var recorder = new TelemetryRecorder(sample: static (ref ActivityCreationOptions<ActivityContext> options) =>
            options.Name == "Nerdigy.Mediator.Stream" ? ActivitySamplingResult.None : ActivitySamplingResult.AllDataAndRecorded);
        var failed = new ScriptedStream(new Step(1, suspend), new Step(0, suspend, new InvalidOperationException("item 2")));
        var exceptionHandler = new ReplacingExceptionHandler(new ScriptedStream(new Step(9)));
        var provider = new DeferredServiceProvider();
        var mediator = new MediatorRuntime(provider);
        provider.Inner = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamActivityRequest, int>), new ScriptedStreamHandler(failed)),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>>),
                new IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>[] { exceptionHandler }),
            (typeof(IRequestHandler<ConsumeStreamRequest, int[]>), new ConsumingHandler(mediator)));

        var values = await mediator.Send(new ConsumeStreamRequest(), CancellationToken.None);

        Assert.Equal([1, 9], values);
        Assert.Empty(recorder.ActivitiesFor<StreamActivityRequest>());
        var send = Assert.Single(recorder.ActivitiesFor<ConsumeStreamRequest>());
        Assert.Null(send.GetTagItem(MediatorTelemetry.ExceptionHandledTag));
        Assert.Empty(send.Events);
        Assert.All(failed.AdvanceObservations, activity => Assert.Same(send, activity));
        Assert.Same(send, exceptionHandler.ObservedActivity);
    }

    /// <summary>
    /// Verifies a metrics-only listener leaves <c>Activity.Current</c> alone and that recovery does not throw.
    /// </summary>
    /// <param name="suspend">Whether advancing suspends.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStream_WhenOnlyMetricsAreListening_DoesNotTouchActivityCurrent(bool suspend)
    {
        using var recorder = new TelemetryRecorder(activities: false, metrics: true);
        using var consumerActivity = new Activity("consumer").Start();
        var failed = new ScriptedStream(new Step(1, suspend), new Step(0, suspend, new InvalidOperationException("item 2")));
        var replacement = new ScriptedStream(new Step(9, suspend));
        var exceptionHandler = new ReplacingExceptionHandler(replacement);
        var mediator = CreateMediator(failed, exceptionHandler);

        var values = await CollectAsync(mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None));

        Assert.Equal([1, 9], values);
        Assert.Same(consumerActivity, Activity.Current);
        Assert.All(failed.AdvanceObservations, activity => Assert.Same(consumerActivity, activity));
        Assert.Same(consumerActivity, failed.DisposeObservation);
        Assert.Same(consumerActivity, exceptionHandler.ObservedActivity);
        Assert.Same(consumerActivity, replacement.AdvanceObservations[0]);
        var duration = Assert.Single(recorder.MeasurementsFor<StreamActivityRequest>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("success", duration.Tags[MediatorTelemetry.OutcomeTag]);
        Assert.Equal(0, recorder.MeasurementsFor<StreamActivityRequest>(MediatorTelemetry.ActiveOperationsInstrument).Sum(static measurement => measurement.Value));
    }

    /// <summary>
    /// Verifies the telemetry scope ends exactly once and the active counter returns to its start for every way an enumeration can end.
    /// </summary>
    /// <param name="ending">How the enumeration ends.</param>
    /// <param name="outcome">The expected outcome tag.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(StreamEnding.ReadsToEnd, "success")]
    [InlineData(StreamEnding.ConsumerBreaks, "success")]
    [InlineData(StreamEnding.HandlerFails, "error")]
    [InlineData(StreamEnding.CallerCancels, "canceled")]
    [InlineData(StreamEnding.Recovers, "success")]
    [InlineData(StreamEnding.DisposalFails, "error")]
    public async Task CreateStream_EveryEnding_EndsScopeExactlyOnce(StreamEnding ending, string outcome)
    {
        using var recorder = new TelemetryRecorder();
        using var cancellation = new CancellationTokenSource();
        var mediator = CreateEndingMediator(ending, cancellation);

        var stream = mediator.CreateStream(new StreamActivityRequest(), cancellation.Token);

        try
        {
            await foreach (var _ in stream)
            {
                if (ending == StreamEnding.ConsumerBreaks)
                {
                    break;
                }

                if (ending == StreamEnding.CallerCancels)
                {
                    await cancellation.CancelAsync();
                }
            }
        }
#pragma warning disable CA1031 // The test asserts on the telemetry, whichever exception ended the stream.
        catch (Exception)
#pragma warning restore CA1031
        {
            Assert.NotEqual(StreamEnding.ReadsToEnd, ending);
            Assert.NotEqual(StreamEnding.ConsumerBreaks, ending);
            Assert.NotEqual(StreamEnding.Recovers, ending);
        }

        Assert.Single(recorder.ActivitiesFor<StreamActivityRequest>());
        var duration = Assert.Single(recorder.MeasurementsFor<StreamActivityRequest>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal(outcome, duration.Tags[MediatorTelemetry.OutcomeTag]);
        var active = recorder.MeasurementsFor<StreamActivityRequest>(MediatorTelemetry.ActiveOperationsInstrument);
        Assert.Equal(2, active.Count);
        Assert.Equal(0, active.Sum(static measurement => measurement.Value));
    }

    /// <summary>
    /// Verifies each enumeration of one returned stream produces its own activity.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenEnumeratedTwice_ProducesOneActivityPerEnumeration()
    {
        using var recorder = new TelemetryRecorder();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamActivityRequest, int>),
                new DelegateStreamHandler(() => new ScriptedStream(new Step(1))))));
        var stream = mediator.CreateStream(new StreamActivityRequest(), CancellationToken.None);

        await CollectAsync(stream);
        await CollectAsync(stream);

        Assert.Equal(2, recorder.ActivitiesFor<StreamActivityRequest>().Count);
    }

    /// <summary>
    /// Asserts that an activity is marked as recovered by an exception handler.
    /// </summary>
    /// <param name="activity">The activity.</param>
    private static void AssertMarkedRecovered(Activity activity)
    {
        Assert.Equal(true, activity.GetTagItem(MediatorTelemetry.ExceptionHandledTag));
        Assert.Contains(activity.Events, static activityEvent => activityEvent.Name == "nerdigy.mediator.exception_handled");
    }

    /// <summary>
    /// Creates a mediator with one scripted handler stream.
    /// </summary>
    /// <param name="handlerStream">The stream the handler returns.</param>
    /// <param name="exceptionHandler">An optional catch-all stream exception handler.</param>
    /// <param name="preProcessor">An optional request preprocessor.</param>
    /// <returns>The configured mediator.</returns>
    private static MediatorRuntime CreateMediator(
        ScriptedStream handlerStream,
        ReplacingExceptionHandler? exceptionHandler = null,
        ThrowingPreProcessor? preProcessor = null)
    {
        return new MediatorRuntime(new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamActivityRequest, int>), new ScriptedStreamHandler(handlerStream)),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>>),
                exceptionHandler is null
                    ? Array.Empty<IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>>()
                    : new IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>[] { exceptionHandler }),
            (typeof(IEnumerable<IRequestPreProcessor<StreamActivityRequest>>),
                preProcessor is null
                    ? Array.Empty<IRequestPreProcessor<StreamActivityRequest>>()
                    : new IRequestPreProcessor<StreamActivityRequest>[] { preProcessor })));
    }

    /// <summary>
    /// Creates a mediator whose stream ends the requested way.
    /// </summary>
    /// <param name="ending">How the enumeration ends.</param>
    /// <param name="cancellation">The caller's cancellation source.</param>
    /// <returns>The configured mediator.</returns>
    private static MediatorRuntime CreateEndingMediator(StreamEnding ending, CancellationTokenSource cancellation)
    {
        return ending switch
        {
            StreamEnding.HandlerFails => CreateMediator(new ScriptedStream(new Step(0, Failure: new InvalidOperationException("failure")))),
            StreamEnding.CallerCancels => CreateMediator(new ScriptedStream(new Step(1), new Step(0, Failure: new OperationCanceledException(cancellation.Token)))),
            StreamEnding.Recovers => CreateMediator(
                new ScriptedStream(new Step(0, Failure: new InvalidOperationException("failure"))),
                new ReplacingExceptionHandler(new ScriptedStream(new Step(9)))),
            StreamEnding.DisposalFails => CreateMediator(new ScriptedStream(new Step(1)) { DisposeFailure = new InvalidOperationException("dispose") }),
            _ => CreateMediator(new ScriptedStream(new Step(1), new Step(2))),
        };
    }

    /// <summary>
    /// Reads a stream to the end.
    /// </summary>
    /// <param name="source">The stream.</param>
    /// <returns>The stream's values.</returns>
    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> source)
    {
        List<int> values = [];
        await CollectInto(source, values);

        return values;
    }

    /// <summary>
    /// Reads a stream to the end into an existing list.
    /// </summary>
    /// <param name="source">The stream.</param>
    /// <param name="values">The list that receives the values.</param>
    /// <returns>A task that completes when enumeration ends.</returns>
    private static async Task CollectInto(IAsyncEnumerable<int> source, List<int> values)
    {
        await foreach (var value in source)
        {
            values.Add(value);
        }
    }

    /// <summary>
    /// Ways a stream enumeration can end.
    /// </summary>
    public enum StreamEnding
    {
        /// <summary>The consumer reads every item.</summary>
        ReadsToEnd,

        /// <summary>The consumer breaks after the first item.</summary>
        ConsumerBreaks,

        /// <summary>The handler fails and nothing recovers.</summary>
        HandlerFails,

        /// <summary>The caller cancels.</summary>
        CallerCancels,

        /// <summary>An exception handler replaces the stream.</summary>
        Recovers,

        /// <summary>Disposing the handler's enumerator fails.</summary>
        DisposalFails,
    }

    /// <summary>
    /// A stream request for activity tests.
    /// </summary>
    private sealed record StreamActivityRequest : IStreamRequest<int>;

    /// <summary>
    /// A request whose handler consumes a stream.
    /// </summary>
    private sealed record ConsumeStreamRequest : IRequest<int[]>;

    /// <summary>
    /// One scripted advance of a <see cref="ScriptedStream"/>.
    /// </summary>
    /// <param name="Value">The value to produce.</param>
    /// <param name="Suspend">Whether the advance suspends before it completes.</param>
    /// <param name="Failure">The exception to throw instead of producing a value.</param>
    /// <param name="OnAdvance">Runs as the advance happens.</param>
    private sealed record Step(int Value, bool Suspend = false, Exception? Failure = null, Action? OnAdvance = null);

    /// <summary>
    /// A stream that follows a script and records which activity was current at each advance and at disposal.
    /// </summary>
    /// <param name="steps">The script.</param>
    private sealed class ScriptedStream(params Step[] steps) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _position = -1;

        /// <summary>
        /// Gets the activity that was current during each advance.
        /// </summary>
        public List<Activity?> AdvanceObservations { get; } = [];

        /// <summary>
        /// Gets the activity that was current during disposal.
        /// </summary>
        public Activity? DisposeObservation { get; private set; }

        /// <summary>
        /// Gets the exception thrown by disposal, if any.
        /// </summary>
        public Exception? DisposeFailure { get; init; }

        /// <inheritdoc />
        public int Current => steps[_position].Value;

        /// <inheritdoc />
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return this;
        }

        /// <inheritdoc />
        public async ValueTask<bool> MoveNextAsync()
        {
            _position++;

            if (_position >= steps.Length)
            {
                return false;
            }

            var step = steps[_position];

            if (step.Suspend)
            {
                await Task.Yield();
            }

            AdvanceObservations.Add(Activity.Current);
            step.OnAdvance?.Invoke();

            return step.Failure is null ? true : throw step.Failure;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            DisposeObservation = Activity.Current;

            return DisposeFailure is null ? ValueTask.CompletedTask : throw DisposeFailure;
        }
    }

    /// <summary>
    /// A stream whose <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator"/> leaves another activity current and throws.
    /// </summary>
    /// <param name="strayActivity">The activity to leave current.</param>
    private sealed class StrayActivityStream(Activity strayActivity) : IAsyncEnumerable<int>
    {
        /// <inheritdoc />
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            strayActivity.Start();

            throw new InvalidOperationException("enumerator failure");
        }
    }

    /// <summary>
    /// Returns a scripted stream.
    /// </summary>
    /// <param name="stream">The stream to return.</param>
    private sealed class ScriptedStreamHandler(ScriptedStream stream) : IStreamRequestHandler<StreamActivityRequest, int>
    {
        /// <inheritdoc />
        public IAsyncEnumerable<int> Handle(StreamActivityRequest request, CancellationToken cancellationToken)
        {
            return stream;
        }
    }

    /// <summary>
    /// Returns a new stream from a factory for each call.
    /// </summary>
    /// <param name="factory">The stream factory.</param>
    private sealed class DelegateStreamHandler(Func<IAsyncEnumerable<int>> factory) : IStreamRequestHandler<StreamActivityRequest, int>
    {
        /// <inheritdoc />
        public IAsyncEnumerable<int> Handle(StreamActivityRequest request, CancellationToken cancellationToken)
        {
            return factory();
        }
    }

    /// <summary>
    /// Replaces the failed stream with a supplied one and records the activity that was current.
    /// </summary>
    /// <param name="replacement">The replacement stream.</param>
    private sealed class ReplacingExceptionHandler(ScriptedStream replacement)
        : IStreamRequestExceptionHandler<StreamActivityRequest, int, Exception>
    {
        /// <summary>
        /// Gets the activity that was current when the handler ran.
        /// </summary>
        public Activity? ObservedActivity { get; private set; }

        /// <inheritdoc />
        public Task Handle(
            StreamActivityRequest request,
            Exception exception,
            StreamRequestExceptionHandlerState<int> state,
            CancellationToken cancellationToken)
        {
            ObservedActivity = Activity.Current;
            state.SetHandled(replacement);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws a supplied exception before the stream is created.
    /// </summary>
    /// <param name="exception">The exception to throw.</param>
    private sealed class ThrowingPreProcessor(Exception exception) : IRequestPreProcessor<StreamActivityRequest>
    {
        /// <inheritdoc />
        public Task Process(StreamActivityRequest request, CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    /// <summary>
    /// Consumes a stream inside a <c>Send</c> handler.
    /// </summary>
    /// <param name="mediator">The mediator that creates the stream.</param>
    private sealed class ConsumingHandler(IMediator mediator) : IRequestHandler<ConsumeStreamRequest, int[]>
    {
        /// <inheritdoc />
        public async Task<int[]> Handle(ConsumeStreamRequest request, CancellationToken cancellationToken)
        {
            var values = await CollectAsync(mediator.CreateStream(new StreamActivityRequest(), cancellationToken));

            return [.. values];
        }
    }

    /// <summary>
    /// Forwards to a provider that is assigned after the mediator is created.
    /// </summary>
    private sealed class DeferredServiceProvider : IServiceProvider
    {
        /// <summary>
        /// Gets or sets the provider to forward to.
        /// </summary>
        public IServiceProvider? Inner { get; set; }

        /// <inheritdoc />
        public object? GetService(Type serviceType)
        {
            return Inner!.GetService(serviceType);
        }
    }
}