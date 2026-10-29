using System.Collections.Concurrent;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Dispatches notifications to the handlers registered for their concrete runtime type.
/// </summary>
internal static class NotificationDispatcher
{
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
        // Sealed and value types cannot differ from their runtime type, so these checks fold away in the JIT
        // and the common case stays free of dictionary lookups.
        if (typeof(TNotification).IsValueType
            || typeof(TNotification).IsSealed
            || notification.GetType() == typeof(TNotification))
        {
            return Publish(serviceProvider, notificationPublisher, notification, cancellationToken);
        }

        return NotificationInvoker.Get(notification.GetType())
            .Publish(serviceProvider, notificationPublisher, notification, cancellationToken);
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
    public static Task Publish<TNotification>(
        IServiceProvider serviceProvider,
        INotificationPublisher notificationPublisher,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var handlers = ServiceProviderUtilities.GetServices<INotificationHandler<TNotification>>(serviceProvider);

        MediatorInstrumentation.RecordHandlerCount(handlers.Length);

        return notificationPublisher.Publish(handlers, notification, cancellationToken);
    }
}

/// <summary>
/// Publishes notifications whose runtime type is only known as <see cref="INotification"/>.
/// </summary>
internal abstract class NotificationInvoker
{
    private static readonly ConcurrentDictionary<Type, NotificationInvoker> s_invokers = new();

    /// <summary>
    /// Publishes the notification to the handlers registered for its concrete type.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers.</param>
    /// <param name="notificationPublisher">The notification publishing strategy.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    public abstract Task Publish(
        IServiceProvider serviceProvider,
        INotificationPublisher notificationPublisher,
        INotification notification,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the cached invoker for a concrete notification type.
    /// </summary>
    /// <param name="notificationType">The concrete notification runtime type.</param>
    /// <returns>The invoker for the notification type.</returns>
    public static NotificationInvoker Get(Type notificationType)
    {
        return s_invokers.TryGetValue(notificationType, out var invoker)
            ? invoker
            : s_invokers.GetOrAdd(notificationType, static type => Create(type));
    }

    /// <summary>
    /// Registers a statically created invoker for a concrete notification type.
    /// </summary>
    /// <param name="notificationType">The concrete notification type.</param>
    /// <param name="invoker">The invoker for the notification type.</param>
    public static void Register(Type notificationType, NotificationInvoker invoker)
    {
        s_invokers.TryAdd(notificationType, invoker);
    }

    /// <summary>
    /// Creates an invoker for a notification type that was not registered up front.
    /// </summary>
    /// <param name="notificationType">The concrete notification runtime type.</param>
    /// <returns>The created invoker.</returns>
    private static NotificationInvoker Create(Type notificationType)
    {
        return GenericInvokerFactory.CreateForMessage<NotificationInvoker>(
            typeof(NotificationInvoker<>),
            notificationType,
            $"options.AddNotificationHandler<{notificationType.Name}, THandler>()",
            notificationType);
    }
}

/// <summary>
/// Publishes notifications of type <typeparamref name="TNotification"/>.
/// </summary>
/// <typeparam name="TNotification">The concrete notification type.</typeparam>
internal sealed class NotificationInvoker<TNotification> : NotificationInvoker
    where TNotification : INotification
{
    /// <inheritdoc />
    public override Task Publish(
        IServiceProvider serviceProvider,
        INotificationPublisher notificationPublisher,
        INotification notification,
        CancellationToken cancellationToken)
    {
        return NotificationDispatcher.Publish(serviceProvider, notificationPublisher, (TNotification)notification, cancellationToken);
    }
}