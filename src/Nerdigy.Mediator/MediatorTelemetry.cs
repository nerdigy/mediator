namespace Nerdigy.Mediator;

/// <summary>
/// Names the <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/> the
/// mediator reports traces and metrics through.
/// </summary>
/// <remarks>
/// <para>
/// Telemetry is off until something listens. With OpenTelemetry, add the source and meter by name:
/// <c>tracing.AddSource(MediatorTelemetry.ActivitySourceName)</c> and <c>metrics.AddMeter(MediatorTelemetry.MeterName)</c>.
/// </para>
/// <para>
/// Each <c>Send</c>, <c>Publish</c>, and stream enumeration produces an activity named
/// <c>Nerdigy.Mediator.Send</c>, <c>Nerdigy.Mediator.Publish</c>, or <c>Nerdigy.Mediator.Stream</c>, and is recorded in
/// the <see cref="OperationDurationInstrument"/> histogram and the <see cref="ActiveOperationsInstrument"/> counter.
/// A stream's activity and duration cover the whole enumeration.
/// </para>
/// </remarks>
public static class MediatorTelemetry
{
    /// <summary>
    /// The name of the mediator's activity source.
    /// </summary>
    public const string ActivitySourceName = "Nerdigy.Mediator";

    /// <summary>
    /// The name of the mediator's meter.
    /// </summary>
    public const string MeterName = "Nerdigy.Mediator";

    /// <summary>
    /// The histogram of operation durations, in seconds.
    /// </summary>
    public const string OperationDurationInstrument = "nerdigy.mediator.operation.duration";

    /// <summary>
    /// The up-down counter of operations in progress.
    /// </summary>
    public const string ActiveOperationsInstrument = "nerdigy.mediator.operation.active";

    /// <summary>
    /// The histogram of how many handlers each published notification reached.
    /// </summary>
    public const string PublishHandlerCountInstrument = "nerdigy.mediator.publish.handlers";

    /// <summary>
    /// The tag holding the operation: <c>send</c>, <c>publish</c>, or <c>stream</c>.
    /// </summary>
    public const string OperationTag = "nerdigy.mediator.operation";

    /// <summary>
    /// The tag holding the full name of the request or notification type.
    /// </summary>
    public const string MessageTypeTag = "nerdigy.mediator.message.type";

    /// <summary>
    /// The tag holding the outcome on duration measurements: <c>success</c>, <c>error</c>, or <c>canceled</c>.
    /// </summary>
    public const string OutcomeTag = "nerdigy.mediator.outcome";

    /// <summary>
    /// The activity tag set to <see langword="true"/> when an exception handler recovered from a failure.
    /// </summary>
    public const string ExceptionHandledTag = "nerdigy.mediator.exception.handled";

    /// <summary>
    /// The activity tag holding how many handlers a published notification reached.
    /// </summary>
    public const string HandlerCountTag = "nerdigy.mediator.handler.count";
}