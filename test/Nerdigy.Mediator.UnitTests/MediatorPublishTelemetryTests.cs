using System.Diagnostics;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies that telemetry does not change what the caller of <c>Publish</c> observes, and that telemetry reports the outcome.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class MediatorPublishTelemetryTests
{
    /// <summary>
    /// Verifies parallel publish reports every handler failure, with and without a listener.
    /// </summary>
    /// <param name="listening">Whether a listener is attached.</param>
    /// <param name="failingHandlers">The number of handlers that fail.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Publish_Parallel_ReportsEveryFailure(bool listening, int failingHandlers)
    {
        using var recorder = listening ? new TelemetryRecorder() : null;
        Exception[] failures = [.. Enumerable.Range(0, failingHandlers).Select(static index => new InvalidOperationException($"failure {index}"))];
        var mediator = CreateParallelMediator(failures);

        var published = mediator.Publish(new PublishNotification(), CancellationToken.None);

        if (failingHandlers == 0)
        {
            await published;
            Assert.True(published.IsCompletedSuccessfully);
        }
        else
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => published);

            Assert.Same(failures[0], thrown);
            Assert.Equal(failures, published.Exception!.InnerExceptions);
        }

        if (recorder is not null)
        {
            var activity = Assert.Single(recorder.ActivitiesFor<PublishNotification>());
            var duration = Assert.Single(recorder.MeasurementsFor<PublishNotification>(MediatorTelemetry.OperationDurationInstrument));

            Assert.Equal(failingHandlers == 0 ? "success" : "error", duration.Tags[MediatorTelemetry.OutcomeTag]);
            Assert.Equal(failingHandlers == 0 ? ActivityStatusCode.Unset : ActivityStatusCode.Error, activity.Status);
            Assert.Equal(failingHandlers == 0 ? null : typeof(InvalidOperationException).FullName, activity.GetTagItem("error.type"));
            Assert.Equal(0, recorder.MeasurementsFor<PublishNotification>(MediatorTelemetry.ActiveOperationsInstrument).Sum(static measurement => measurement.Value));
        }
    }

    /// <summary>
    /// Verifies the duration measurement and the stopped activity exist at the moment the returned task completes.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenReturnedTaskCompletes_TelemetryIsAlreadyRecorded()
    {
        using var recorder = new TelemetryRecorder();
        var mediator = CreateParallelMediator(new InvalidOperationException("first"), new InvalidOperationException("second"));

        var published = mediator.Publish(new PublishNotification(), CancellationToken.None);
        var (activities, durations) = await published.ContinueWith(
            _ => (recorder.ActivitiesFor<PublishNotification>(), recorder.MeasurementsFor<PublishNotification>(MediatorTelemetry.OperationDurationInstrument)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Assert.Single(activities);
        Assert.Equal("error", Assert.Single(durations).Tags[MediatorTelemetry.OutcomeTag]);
    }

    /// <summary>
    /// Verifies a canceled dispatch gives a canceled returned task and the canceled outcome.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenDispatchIsCanceled_ReturnsCanceledTaskAndRecordsCanceled()
    {
        using var recorder = new TelemetryRecorder();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var mediator = new MediatorRuntime(
            new TestServiceProvider(
                (typeof(IEnumerable<INotificationHandler<PublishNotification>>),
                    new INotificationHandler<PublishNotification>[] { new ObservingCancellationHandler(), new ObservingCancellationHandler() })),
            new TaskWhenAllPublisher());

        var published = mediator.Publish(new PublishNotification(), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => published);

        Assert.True(published.IsCanceled);
        var activity = Assert.Single(recorder.ActivitiesFor<PublishNotification>());
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        var duration = Assert.Single(recorder.MeasurementsFor<PublishNotification>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("canceled", duration.Tags[MediatorTelemetry.OutcomeTag]);
    }

    /// <summary>
    /// Verifies a cancellation the caller did not request is recorded as the handler's own exception, not a substitute.
    /// </summary>
    /// <param name="parallel">Whether to publish with <see cref="TaskWhenAllPublisher"/> instead of <see cref="ForeachAwaitPublisher"/>.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WhenHandlerCancelsItsOwnToken_RecordsTheOriginalException(bool parallel)
    {
        using var recorder = new TelemetryRecorder();
        INotificationPublisher publisher = parallel ? new TaskWhenAllPublisher() : new ForeachAwaitPublisher();
        var mediator = new MediatorRuntime(
            new TestServiceProvider(
                (typeof(IEnumerable<INotificationHandler<PublishNotification>>), new INotificationHandler<PublishNotification>[] { new OwnTimeoutHandler() })),
            publisher);

        var published = mediator.Publish(new PublishNotification(), CancellationToken.None);
        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => published);

        var activity = Assert.Single(recorder.ActivitiesFor<PublishNotification>());
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(typeof(OperationCanceledException).FullName, activity.GetTagItem("error.type"));
        var exceptionEvent = Assert.Single(activity.Events, static activityEvent => activityEvent.Name == "exception");
        var eventTags = exceptionEvent.Tags.ToDictionary(static tag => tag.Key, static tag => tag.Value);
        Assert.Equal(thrown.Message, eventTags["exception.message"]);
        Assert.Contains(nameof(OwnTimeoutHandler), Assert.IsType<string>(eventTags["exception.stacktrace"]));
        var duration = Assert.Single(recorder.MeasurementsFor<PublishNotification>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("error", duration.Tags[MediatorTelemetry.OutcomeTag]);
        Assert.Equal(typeof(OperationCanceledException).FullName, duration.Tags["error.type"]);
    }

    /// <summary>
    /// Verifies a publisher that throws synchronously and one that returns a faulted task are both reported.
    /// </summary>
    /// <param name="throwSynchronously">Whether the publisher throws instead of returning a faulted task.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publish_WhenDispatchFails_ReportsFailure(bool throwSynchronously)
    {
        using var recorder = new TelemetryRecorder();
        var failure = new InvalidOperationException("dispatch failure");
        var mediator = new MediatorRuntime(new TestServiceProvider(), new FailingPublisher(failure, throwSynchronously));

        var published = mediator.Publish(new PublishNotification(), CancellationToken.None);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => published);

        Assert.Same(failure, thrown);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(recorder.ActivitiesFor<PublishNotification>()).Status);
        var duration = Assert.Single(recorder.MeasurementsFor<PublishNotification>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("error", duration.Tags[MediatorTelemetry.OutcomeTag]);
    }

    /// <summary>
    /// Verifies the caller's current activity is unchanged when <c>Publish</c> returns and when its task completes.
    /// </summary>
    /// <param name="handlerSuspends">Whether the handler suspends, so that <c>Publish</c> returns a pending task.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publish_WhenListening_LeavesCallerActivityCurrent(bool handlerSuspends)
    {
        using var recorder = new TelemetryRecorder();
        using var callerActivity = new Activity("caller").Start();
        var handler = new CapturingHandler(handlerSuspends);
        var mediator = new MediatorRuntime(
            new TestServiceProvider(
                (typeof(IEnumerable<INotificationHandler<PublishNotification>>), new INotificationHandler<PublishNotification>[] { handler })),
            new TaskWhenAllPublisher());

        var published = mediator.Publish(new PublishNotification(), CancellationToken.None);

        Assert.Same(callerActivity, Activity.Current);
        await published;
        Assert.Same(callerActivity, Activity.Current);
        Assert.Same(Assert.Single(recorder.ActivitiesFor<PublishNotification>()), handler.ObservedActivity);
    }

    /// <summary>
    /// Creates a mediator that publishes in parallel to one handler per failure.
    /// </summary>
    /// <param name="failures">The exception each handler fails with.</param>
    /// <returns>The configured mediator.</returns>
    private static MediatorRuntime CreateParallelMediator(params Exception[] failures)
    {
        INotificationHandler<PublishNotification>[] handlers = [.. failures.Select(static failure => new FailingHandler(failure))];

        return new MediatorRuntime(
            new TestServiceProvider((typeof(IEnumerable<INotificationHandler<PublishNotification>>), handlers)),
            new TaskWhenAllPublisher());
    }

    /// <summary>
    /// Represents a notification for publish telemetry tests.
    /// </summary>
    private sealed record PublishNotification : INotification;

    /// <summary>
    /// Fails asynchronously with a supplied exception.
    /// </summary>
    /// <param name="failure">The exception to fail with.</param>
    private sealed class FailingHandler(Exception failure) : INotificationHandler<PublishNotification>
    {
        /// <inheritdoc />
        public async Task Handle(PublishNotification notification, CancellationToken cancellationToken)
        {
            await Task.Yield();

            throw failure;
        }
    }

    /// <summary>
    /// Observes cancellation after suspending.
    /// </summary>
    private sealed class ObservingCancellationHandler : INotificationHandler<PublishNotification>
    {
        /// <inheritdoc />
        public async Task Handle(PublishNotification notification, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Cancels a token of its own and throws for it, as a handler with an internal timeout does.
    /// </summary>
    private sealed class OwnTimeoutHandler : INotificationHandler<PublishNotification>
    {
        /// <inheritdoc />
        public async Task Handle(PublishNotification notification, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource();
            await timeout.CancelAsync();
            await Task.Yield();
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Captures the current activity, optionally after suspending.
    /// </summary>
    /// <param name="suspend">Whether to suspend first.</param>
    private sealed class CapturingHandler(bool suspend) : INotificationHandler<PublishNotification>
    {
        /// <summary>
        /// Gets the activity that was current when the handler ran.
        /// </summary>
        public Activity? ObservedActivity { get; private set; }

        /// <inheritdoc />
        public async Task Handle(PublishNotification notification, CancellationToken cancellationToken)
        {
            if (suspend)
            {
                await Task.Yield();
            }

            ObservedActivity = Activity.Current;
        }
    }

    /// <summary>
    /// Fails either by throwing or by returning a faulted task.
    /// </summary>
    /// <param name="failure">The exception to fail with.</param>
    /// <param name="throwSynchronously">Whether to throw instead of returning a faulted task.</param>
    private sealed class FailingPublisher(Exception failure, bool throwSynchronously) : INotificationPublisher
    {
        /// <inheritdoc />
        public Task Publish<TNotification>(
            IEnumerable<INotificationHandler<TNotification>> handlers,
            TNotification notification,
            CancellationToken cancellationToken)
            where TNotification : INotification
        {
            return throwSynchronously ? throw failure : Task.FromException(failure);
        }
    }
}