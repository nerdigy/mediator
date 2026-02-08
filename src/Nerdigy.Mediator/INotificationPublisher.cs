using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator;

/// <summary>
/// Defines a strategy for publishing notifications to handlers.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Publishes a notification to the supplied handlers.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="handlers">The handlers that will process the notification.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification;
}