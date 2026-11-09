namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Groups every test that attaches a mediator <see cref="System.Diagnostics.ActivityListener"/> or
/// <see cref="System.Diagnostics.Metrics.MeterListener"/>.
/// </summary>
/// <remarks>
/// Those listeners are process-wide and switch <c>Send</c> and <c>Publish</c> between two code paths. This collection does
/// not run in parallel with any other test, so a test outside it never runs while a mediator listener is attached.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "Telemetry";
}