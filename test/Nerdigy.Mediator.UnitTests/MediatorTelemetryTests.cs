using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies the activities and metrics the mediator reports.
/// </summary>
/// <remarks>
/// Listeners see every mediator operation in the process, so each test filters by its own message types.
/// </remarks>
public sealed class MediatorTelemetryTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediatorTelemetryTests"/> class and starts listening.
    /// </summary>
    public MediatorTelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == MediatorTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == MediatorTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.Start();
    }

    /// <summary>
    /// Verifies a successful send produces an activity and a success duration measurement.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenListening_RecordsActivityAndDuration()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<TelemetryRequest, string>), new TelemetryHandler())));

        await mediator.Send(new TelemetryRequest(Fail: false), CancellationToken.None);

        var activity = Assert.Single(ActivitiesFor<TelemetryRequest>());
        Assert.Equal("Nerdigy.Mediator.Send", activity.OperationName);
        Assert.Equal("send TelemetryRequest", activity.DisplayName);
        Assert.Equal("send", activity.GetTagItem(MediatorTelemetry.OperationTag));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);

        var duration = Assert.Single(MeasurementsFor<TelemetryRequest>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("success", duration.Tags[MediatorTelemetry.OutcomeTag]);
        Assert.Equal(0, MeasurementsFor<TelemetryRequest>(MediatorTelemetry.ActiveOperationsInstrument).Sum(static measurement => measurement.Value));
    }

    /// <summary>
    /// Verifies a failed send marks the activity as an error and records the error type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenHandlerFails_RecordsError()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<TelemetryRequest, string>), new TelemetryHandler())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new TelemetryRequest(Fail: true), CancellationToken.None));

        var activity = Assert.Single(ActivitiesFor<TelemetryRequest>());
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, activity.GetTagItem("error.type"));

        var duration = Assert.Single(MeasurementsFor<TelemetryRequest>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("error", duration.Tags[MediatorTelemetry.OutcomeTag]);
        Assert.Equal(typeof(InvalidOperationException).FullName, duration.Tags["error.type"]);
    }

    /// <summary>
    /// Verifies an exception handler's recovery is marked on the activity.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenExceptionHandlerRecovers_MarksActivity()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<TelemetryRequest, string>), new TelemetryHandler()),
            (typeof(IEnumerable<IRequestExceptionHandler<TelemetryRequest, string, Exception>>),
                new IRequestExceptionHandler<TelemetryRequest, string, Exception>[] { new RecoveringHandler() })));

        var response = await mediator.Send(new TelemetryRequest(Fail: true), CancellationToken.None);

        Assert.Equal("recovered", response);
        var activity = Assert.Single(ActivitiesFor<TelemetryRequest>());
        Assert.Equal(true, activity.GetTagItem(MediatorTelemetry.ExceptionHandledTag));
        Assert.Contains(activity.Events, static activityEvent => activityEvent.Name == "nerdigy.mediator.exception_handled");
    }

    /// <summary>
    /// Verifies a canceled send records the canceled outcome rather than an error.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenCallerCancels_RecordsCanceledOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<TelemetryRequest, string>), new TelemetryHandler())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mediator.Send(new TelemetryRequest(Fail: false), cancellation.Token));

        var activity = Assert.Single(ActivitiesFor<TelemetryRequest>());
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        var duration = Assert.Single(MeasurementsFor<TelemetryRequest>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("canceled", duration.Tags[MediatorTelemetry.OutcomeTag]);
    }

    /// <summary>
    /// Verifies publishing records the number of handlers on the activity and in the handler count histogram.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenListening_RecordsHandlerCount()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IEnumerable<INotificationHandler<TelemetryNotification>>),
                new INotificationHandler<TelemetryNotification>[] { new TelemetryNotificationHandler(), new TelemetryNotificationHandler() })));

        await mediator.Publish(new TelemetryNotification(), CancellationToken.None);

        var activity = Assert.Single(ActivitiesFor<TelemetryNotification>());
        Assert.Equal("Nerdigy.Mediator.Publish", activity.OperationName);
        Assert.Equal(2, activity.GetTagItem(MediatorTelemetry.HandlerCountTag));
    }

    /// <summary>
    /// Verifies a stream's activity stays open for the whole enumeration and closes when the consumer stops early.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenListening_ActivityCoversEnumeration()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IStreamRequestHandler<TelemetryStreamRequest, int>), new TelemetryStreamHandler())));

        var stream = mediator.CreateStream(new TelemetryStreamRequest(), CancellationToken.None);
        Assert.Empty(ActivitiesFor<TelemetryStreamRequest>());

        await foreach (var value in stream)
        {
            Assert.Empty(ActivitiesFor<TelemetryStreamRequest>());

            if (value == 2)
            {
                break;
            }
        }

        var activity = Assert.Single(ActivitiesFor<TelemetryStreamRequest>());
        Assert.Equal("Nerdigy.Mediator.Stream", activity.OperationName);
        var duration = Assert.Single(MeasurementsFor<TelemetryStreamRequest>(MediatorTelemetry.OperationDurationInstrument));
        Assert.Equal("success", duration.Tags[MediatorTelemetry.OutcomeTag]);
    }

    /// <summary>
    /// Stops listening.
    /// </summary>
    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    /// <summary>
    /// Gets the stopped activities for a message type.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <returns>The activities.</returns>
    private List<Activity> ActivitiesFor<TMessage>()
    {
        return [.. _activities.Where(static activity => Equals(activity.GetTagItem(MediatorTelemetry.MessageTypeTag), typeof(TMessage).FullName))];
    }

    /// <summary>
    /// Gets the measurements of an instrument for a message type.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="instrument">The instrument name.</param>
    /// <returns>The measurements.</returns>
    private List<(string Instrument, double Value, Dictionary<string, object?> Tags)> MeasurementsFor<TMessage>(string instrument)
    {
        return
        [
            .. _measurements.Where(measurement =>
                measurement.Instrument == instrument
                && measurement.Tags.TryGetValue(MediatorTelemetry.MessageTypeTag, out var messageType)
                && Equals(messageType, typeof(TMessage).FullName))
        ];
    }

    /// <summary>
    /// Records a measurement.
    /// </summary>
    /// <typeparam name="T">The measurement value type.</typeparam>
    /// <param name="instrument">The instrument.</param>
    /// <param name="value">The measurement value.</param>
    /// <param name="tags">The measurement tags.</param>
    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        Dictionary<string, object?> tagDictionary = [];

        foreach (var tag in tags)
        {
            tagDictionary[tag.Key] = tag.Value;
        }

        _measurements.Enqueue((instrument.Name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), tagDictionary));
    }

    /// <summary>
    /// Represents a request for telemetry tests.
    /// </summary>
    /// <param name="Fail">Whether the handler fails.</param>
    private sealed record TelemetryRequest(bool Fail) : IRequest<string>;

    /// <summary>
    /// Represents a notification for telemetry tests.
    /// </summary>
    private sealed record TelemetryNotification : INotification;

    /// <summary>
    /// Represents a stream request for telemetry tests.
    /// </summary>
    private sealed record TelemetryStreamRequest : IStreamRequest<int>;

    /// <summary>
    /// Succeeds or fails as the request asks, and observes cancellation.
    /// </summary>
    private sealed class TelemetryHandler : IRequestHandler<TelemetryRequest, string>
    {
        /// <inheritdoc />
        public async Task<string> Handle(TelemetryRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            return request.Fail ? throw new InvalidOperationException("failure") : "done";
        }
    }

    /// <summary>
    /// Recovers from every exception.
    /// </summary>
    private sealed class RecoveringHandler : IRequestExceptionHandler<TelemetryRequest, string, Exception>
    {
        /// <inheritdoc />
        public Task Handle(TelemetryRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            state.SetHandled("recovered");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles telemetry notifications.
    /// </summary>
    private sealed class TelemetryNotificationHandler : INotificationHandler<TelemetryNotification>
    {
        /// <inheritdoc />
        public Task Handle(TelemetryNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Streams the numbers one to five.
    /// </summary>
    private sealed class TelemetryStreamHandler : IStreamRequestHandler<TelemetryStreamRequest, int>
    {
        /// <inheritdoc />
        public async IAsyncEnumerable<int> Handle(TelemetryStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var value = 1; value <= 5; value++)
            {
                await Task.Yield();

                yield return value;
            }
        }
    }
}