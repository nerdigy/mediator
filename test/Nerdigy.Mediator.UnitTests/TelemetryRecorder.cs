using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Records the mediator's activities and metrics. Tests that use it belong to <see cref="TelemetryCollection"/>.
/// </summary>
internal sealed class TelemetryRecorder : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ConcurrentQueue<RecordedMeasurement> _measurements = new();
    private readonly ActivityListener? _activityListener;
    private readonly MeterListener? _meterListener;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryRecorder"/> class and starts listening.
    /// </summary>
    /// <param name="activities">Whether to attach an activity listener.</param>
    /// <param name="metrics">Whether to attach a meter listener.</param>
    public TelemetryRecorder(bool activities = true, bool metrics = true)
    {
        if (activities)
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == MediatorTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _activities.Enqueue,
            };
            ActivitySource.AddActivityListener(_activityListener);
        }

        if (metrics)
        {
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
    }

    /// <summary>
    /// Gets the stopped activities for a message type.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <returns>The activities.</returns>
    public List<Activity> ActivitiesFor<TMessage>()
    {
        return [.. _activities.Where(static activity => Equals(activity.GetTagItem(MediatorTelemetry.MessageTypeTag), typeof(TMessage).FullName))];
    }

    /// <summary>
    /// Gets the measurements of an instrument for a message type.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="instrument">The instrument name.</param>
    /// <returns>The measurements.</returns>
    public List<RecordedMeasurement> MeasurementsFor<TMessage>(string instrument)
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
    /// Stops listening.
    /// </summary>
    public void Dispose()
    {
        _activityListener?.Dispose();
        _meterListener?.Dispose();
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

        _measurements.Enqueue(new RecordedMeasurement(instrument.Name, Convert.ToDouble(value, CultureInfo.InvariantCulture), tagDictionary));
    }

    /// <summary>
    /// One recorded measurement.
    /// </summary>
    /// <param name="Instrument">The instrument name.</param>
    /// <param name="Value">The measurement value.</param>
    /// <param name="Tags">The measurement tags.</param>
    internal sealed record RecordedMeasurement(string Instrument, double Value, Dictionary<string, object?> Tags);
}