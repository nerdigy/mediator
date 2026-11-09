using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies the active operations counter when listeners change during an operation.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class MediatorActiveOperationsTests
{
    /// <summary>
    /// Verifies a meter listener that attaches during an operation sees no decrement for an increment it never saw.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenMeterListenerAttachesDuringOperation_RecordsNoDecrement()
    {
        using var activityOnly = new TelemetryRecorder(activities: true, metrics: false);
        var handler = new BlockingHandler();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<BlockingRequest, Unit>), handler)));

        var sent = mediator.Send(new BlockingRequest(), CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var metrics = new TelemetryRecorder(activities: false, metrics: true);
        handler.Release.SetResult();
        await sent;

        Assert.Single(activityOnly.ActivitiesFor<BlockingRequest>());
        Assert.DoesNotContain(
            metrics.MeasurementsFor<BlockingRequest>(MediatorTelemetry.ActiveOperationsInstrument),
            static measurement => measurement.Value < 0);
    }

    /// <summary>
    /// A request whose handler waits to be released.
    /// </summary>
    private sealed record BlockingRequest : IRequest<Unit>;

    /// <summary>
    /// Waits to be released, and reports when it has started.
    /// </summary>
    private sealed class BlockingHandler : IRequestHandler<BlockingRequest, Unit>
    {
        /// <summary>
        /// Gets a source that completes when the handler has started.
        /// </summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets a source that releases the handler.
        /// </summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async Task<Unit> Handle(BlockingRequest request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task;

            return Unit.Value;
        }
    }
}