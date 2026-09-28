using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Publishes notifications by running all handlers concurrently with <see cref="Task.WhenAll(IEnumerable{Task})"/>.
/// </summary>
public sealed class TaskWhenAllPublisher : INotificationPublisher
{
    /// <summary>
    /// Publishes a notification to handlers concurrently.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="handlers">The handlers that will process the notification.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while publishing.</param>
    /// <returns>A task that completes when all handlers finish.</returns>
    public Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(notification);

        if (handlers is ICollection<INotificationHandler<TNotification>> collection)
        {
            if (collection.Count == 0)
            {
                return Task.CompletedTask;
            }

            if (collection.Count == 1)
            {
                using var enumerator = collection.GetEnumerator();
                _ = enumerator.MoveNext();

                return enumerator.Current.Handle(notification, cancellationToken);
            }

            var pendingTasks = new Task[collection.Count];
            var index = 0;

            foreach (var handler in collection)
            {
                pendingTasks[index] = StartHandler(handler, notification, cancellationToken);
                index++;
            }

            return Task.WhenAll(pendingTasks);
        }

        Task? firstTask = null;
        List<Task>? additionalTasks = null;

        foreach (var handler in handlers)
        {
            var task = StartHandler(handler, notification, cancellationToken);

            if (firstTask is null)
            {
                firstTask = task;
                continue;
            }

            additionalTasks ??= [firstTask];
            additionalTasks.Add(task);
        }

        if (additionalTasks is not null)
        {
            return Task.WhenAll(additionalTasks);
        }

        if (firstTask is not null)
        {
            return firstTask;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts a handler, converting a synchronous throw into a completed task so remaining handlers still start.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="handler">The handler to start.</param>
    /// <param name="notification">The notification to handle.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while handling.</param>
    /// <returns>The handler task, or a canceled or faulted task when the handler throws synchronously.</returns>
    private static Task StartHandler<TNotification>(
        INotificationHandler<TNotification> handler,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        try
        {
            return handler.Handle(notification, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            // Mirror async method semantics: a thrown cancellation produces a canceled task, not a faulted one.
            var canceled = new TaskCompletionSource();
            _ = canceled.TrySetCanceled(exception.CancellationToken);

            return canceled.Task;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
}