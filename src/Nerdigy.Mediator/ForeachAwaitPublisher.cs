using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Publishes notifications by awaiting each handler sequentially.
/// </summary>
public sealed class ForeachAwaitPublisher : INotificationPublisher
{
    /// <summary>
    /// Publishes a notification to handlers in sequence.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="handlers">The handlers that will process the notification.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when all handlers finish.</returns>
    public async Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(notification);

        foreach (var handler in handlers)
        {
            await handler.Handle(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}