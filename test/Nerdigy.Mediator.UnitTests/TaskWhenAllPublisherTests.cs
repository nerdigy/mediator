using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies <see cref="TaskWhenAllPublisher"/> never leaves started handlers running when it fails.
/// </summary>
public sealed class TaskWhenAllPublisherTests
{
    /// <summary>
    /// Verifies a handler sequence that fails part-way starts no handler and returns a faulted task instead of throwing.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenHandlerSequenceFailsPartWay_StartsNoHandlerAndFaultsTask()
    {
        var handler = new PendingHandler();
        var publisher = new TaskWhenAllPublisher();

        var task = publisher.Publish(FailAfter(handler), new Note(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("enumeration failure", exception.Message);
        Assert.False(handler.Started);
    }

    /// <summary>
    /// Yields one handler, then fails.
    /// </summary>
    /// <param name="handler">The handler to yield first.</param>
    /// <returns>The failing handler sequence.</returns>
    private static IEnumerable<INotificationHandler<Note>> FailAfter(INotificationHandler<Note> handler)
    {
        yield return handler;

        throw new InvalidOperationException("enumeration failure");
    }

    /// <summary>
    /// Represents a notification for publisher tests.
    /// </summary>
    private sealed record Note : INotification;

    /// <summary>
    /// Records whether it started and never completes.
    /// </summary>
    private sealed class PendingHandler : INotificationHandler<Note>
    {
        /// <summary>
        /// Gets a value indicating whether the handler started.
        /// </summary>
        public bool Started { get; private set; }

        /// <inheritdoc />
        public Task Handle(Note notification, CancellationToken cancellationToken)
        {
            Started = true;

            return new TaskCompletionSource().Task;
        }
    }
}