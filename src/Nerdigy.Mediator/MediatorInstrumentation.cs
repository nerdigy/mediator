using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nerdigy.Mediator;

/// <summary>
/// Reports mediator traces and metrics. Every method returns immediately when nothing is listening.
/// </summary>
internal static class MediatorInstrumentation
{
    private static readonly string? s_version = typeof(MediatorInstrumentation).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource s_source = new(MediatorTelemetry.ActivitySourceName, s_version);

    private static readonly Meter s_meter = new(MediatorTelemetry.MeterName, s_version);

    private static readonly Histogram<double> s_duration = s_meter.CreateHistogram(
        MediatorTelemetry.OperationDurationInstrument,
        unit: "s",
        description: "Duration of mediator send, publish, and stream operations.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.0001, 0.0005, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10],
        });

    private static readonly UpDownCounter<long> s_active = s_meter.CreateUpDownCounter<long>(
        MediatorTelemetry.ActiveOperationsInstrument,
        unit: "{operation}",
        description: "Mediator send, publish, and stream operations in progress.");

    private static readonly Histogram<int> s_handlerCount = s_meter.CreateHistogram(
        MediatorTelemetry.PublishHandlerCountInstrument,
        unit: "{handler}",
        description: "Number of handlers each published notification reached.",
        advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = [0, 1, 2, 5, 10, 20, 50] });

    /// <summary>
    /// Gets a value indicating whether anything listens to mediator traces or operation metrics.
    /// </summary>
    public static bool IsEnabled => s_source.HasListeners() || s_duration.Enabled || s_active.Enabled;

    /// <summary>
    /// Starts telemetry for a send or publish operation.
    /// </summary>
    /// <param name="operation">The operation name: <c>send</c> or <c>publish</c>.</param>
    /// <param name="messageType">The request or notification runtime type.</param>
    /// <returns>The operation scope, or <see langword="null"/> when nothing is listening.</returns>
    public static OperationScope? Start(string operation, Type messageType)
    {
        return IsEnabled ? new OperationScope(operation, messageType) : null;
    }

    /// <summary>
    /// Starts telemetry for one stream enumeration.
    /// </summary>
    /// <param name="requestType">The stream request runtime type.</param>
    /// <returns>The operation scope, or <see langword="null"/> when nothing is listening.</returns>
    public static OperationScope? StartStream(Type requestType)
    {
        return Start("stream", requestType);
    }

    /// <summary>
    /// Marks the current mediator activity as recovered by an exception handler.
    /// </summary>
    /// <param name="exception">The exception the handler recovered from.</param>
    public static void RecordHandledException(Exception exception)
    {
        var activity = Activity.Current;

        if (activity is null || activity.Source != s_source)
        {
            return;
        }

        activity.SetTag(MediatorTelemetry.ExceptionHandledTag, true);
        activity.AddEvent(new ActivityEvent(
            "nerdigy.mediator.exception_handled",
            tags: new ActivityTagsCollection { ["exception.type"] = exception.GetType().FullName }));
    }

    /// <summary>
    /// Records how many handlers a published notification reached.
    /// </summary>
    /// <param name="handlerCount">The number of resolved handlers.</param>
    public static void RecordHandlerCount(int handlerCount)
    {
        if (s_handlerCount.Enabled)
        {
            s_handlerCount.Record(handlerCount);
        }

        var activity = Activity.Current;

        if (activity is not null && activity.Source == s_source)
        {
            activity.SetTag(MediatorTelemetry.HandlerCountTag, handlerCount);
        }
    }

    /// <summary>
    /// Tracks the activity, duration, and active count of one mediator operation.
    /// </summary>
    internal sealed class OperationScope : IDisposable
    {
        private readonly string _messageTypeName;
        private readonly string _operation;
        private readonly long _startTimestamp;
        private readonly Activity? _activity;
        private string _outcome = "success";
        private string? _errorType;

        /// <summary>
        /// Initializes a new instance of the <see cref="OperationScope"/> class and starts its activity.
        /// </summary>
        /// <param name="operation">The operation name.</param>
        /// <param name="messageType">The request or notification runtime type.</param>
        public OperationScope(string operation, Type messageType)
        {
            _operation = operation;
            _messageTypeName = messageType.FullName ?? messageType.Name;
            _startTimestamp = Stopwatch.GetTimestamp();

            _activity = s_source.StartActivity(OperationName(operation), ActivityKind.Internal);

            if (_activity is not null)
            {
                _activity.DisplayName = $"{operation} {messageType.Name}";
                _activity.SetTag(MediatorTelemetry.OperationTag, operation);
                _activity.SetTag(MediatorTelemetry.MessageTypeTag, _messageTypeName);
            }

            if (s_active.Enabled)
            {
                s_active.Add(1, CreateTags(includeOutcome: false));
            }
        }

        /// <summary>
        /// Records that the operation failed.
        /// </summary>
        /// <param name="exception">The exception the operation failed with.</param>
        /// <param name="cancellationToken">The operation's cancellation token, used to tell cancellation from failure.</param>
        public void Fail(Exception exception, CancellationToken cancellationToken)
        {
            if (ExceptionProcessing.IsCallerCancellation(exception, cancellationToken))
            {
                _outcome = "canceled";
                _activity?.SetTag(MediatorTelemetry.OutcomeTag, _outcome);

                return;
            }

            _outcome = "error";
            _errorType = exception.GetType().FullName;
            _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            _activity?.SetTag("error.type", _errorType);
            _activity?.AddException(exception);
        }

        /// <summary>
        /// Stops the activity and records the operation's duration and outcome.
        /// </summary>
        public void Dispose()
        {
            if (s_active.Enabled)
            {
                s_active.Add(-1, CreateTags(includeOutcome: false));
            }

            if (s_duration.Enabled)
            {
                s_duration.Record(Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds, CreateTags(includeOutcome: true));
            }

            _activity?.Dispose();
        }

        /// <summary>
        /// Gets the activity name for an operation.
        /// </summary>
        /// <param name="operation">The operation name.</param>
        /// <returns>The activity name.</returns>
        private static string OperationName(string operation)
        {
            return operation switch
            {
                "send" => "Nerdigy.Mediator.Send",
                "publish" => "Nerdigy.Mediator.Publish",
                _ => "Nerdigy.Mediator.Stream",
            };
        }

        /// <summary>
        /// Creates the metric tags for this operation.
        /// </summary>
        /// <param name="includeOutcome">Whether to include the outcome and error type.</param>
        /// <returns>The metric tags.</returns>
        private TagList CreateTags(bool includeOutcome)
        {
            var tags = new TagList
            {
                { MediatorTelemetry.OperationTag, _operation },
                { MediatorTelemetry.MessageTypeTag, _messageTypeName },
            };

            if (includeOutcome)
            {
                tags.Add(MediatorTelemetry.OutcomeTag, _outcome);

                if (_errorType is not null)
                {
                    tags.Add("error.type", _errorType);
                }
            }

            return tags;
        }
    }
}