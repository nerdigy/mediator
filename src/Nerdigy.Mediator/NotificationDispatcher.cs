using System.Collections.Concurrent;
using System.Reflection;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Provides cached notification dispatch delegates keyed by the notification runtime type.
/// </summary>
internal static class NotificationDispatcher
{
    private static readonly ConcurrentDictionary<Type, NotificationDispatchDelegate> s_dispatchers = new();

    private static readonly MethodInfo s_publishUntypedMethod = typeof(NotificationDispatcher).GetMethod(
        nameof(PublishUntyped),
        BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Dispatches a notification to the handlers registered for its concrete runtime type.
    /// </summary>
    /// <typeparam name="TNotification">The notification type inferred at the call site.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="notificationPublisher">The notification publishing strategy.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    public static Task Dispatch<TNotification>(
        IServiceProvider serviceProvider,
        INotificationPublisher notificationPublisher,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(notificationPublisher);
        ArgumentNullException.ThrowIfNull(notification);

        // Sealed and value types cannot differ from their runtime type, so these checks fold away in the JIT
        // and the common case stays free of reflection and dictionary lookups.
        if (typeof(TNotification).IsValueType
            || typeof(TNotification).IsSealed
            || notification.GetType() == typeof(TNotification))
        {
            return PublishCore(serviceProvider, notificationPublisher, notification, cancellationToken);
        }

        var notificationType = notification.GetType();
        var dispatcher = s_dispatchers.GetOrAdd(notificationType, static type => BuildDispatcher(type));

        return dispatcher(serviceProvider, notificationPublisher, notification, cancellationToken);
    }

    /// <summary>
    /// Builds a dispatch delegate for a concrete notification runtime type.
    /// </summary>
    /// <param name="notificationType">The concrete notification type.</param>
    /// <returns>A cached dispatch delegate for the notification type.</returns>
    private static NotificationDispatchDelegate BuildDispatcher(Type notificationType)
    {
        return s_publishUntypedMethod
            .MakeGenericMethod(notificationType)
            .CreateDelegate<NotificationDispatchDelegate>();
    }

    /// <summary>
    /// Casts a notification to its concrete runtime type and publishes it.
    /// </summary>
    /// <typeparam name="TNotification">The concrete notification type.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="notificationPublisher">The notification publishing strategy.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    private static Task PublishUntyped<TNotification>(
        IServiceProvider serviceProvider,
        INotificationPublisher notificationPublisher,
        INotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        return PublishCore(serviceProvider, notificationPublisher, (TNotification)notification, cancellationToken);
    }

    /// <summary>
    /// Resolves the handlers for <typeparamref name="TNotification"/> and passes them to the publisher strategy.
    /// </summary>
    /// <typeparam name="TNotification">The concrete notification type.</typeparam>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="notificationPublisher">The notification publishing strategy.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    private static Task PublishCore<TNotification>(
        IServiceProvider serviceProvider,
        INotificationPublisher notificationPublisher,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var handlers = ServiceProviderUtilities.GetServices<INotificationHandler<TNotification>>(serviceProvider);

        return notificationPublisher.Publish(handlers, notification, cancellationToken);
    }

    /// <summary>
    /// Represents a cached notification dispatch delegate.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="notificationPublisher">The notification publishing strategy.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    private delegate Task NotificationDispatchDelegate(
        IServiceProvider serviceProvider,
        INotificationPublisher notificationPublisher,
        INotification notification,
        CancellationToken cancellationToken);
}