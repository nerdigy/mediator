using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Publishes notifications by running all handlers concurrently with <see cref="Task.WhenAll(Task[])"/>.
/// </summary>
/// <remarks>
/// The handler sequence is read completely before any handler starts. If reading it fails, no handler has started and
/// the returned task is faulted, so no started work is left running when the caller sees the failure.
/// </remarks>
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

        INotificationHandler<TNotification>[] handlerArray;

        try
        {
            handlerArray = handlers as INotificationHandler<TNotification>[] ?? [.. handlers];
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }

        switch (handlerArray.Length)
        {
            case 0:
                return Task.CompletedTask;
            case 1:
                return StartHandler(handlerArray[0], notification, cancellationToken);
        }

        var pendingTasks = new Task[handlerArray.Length];

        for (var index = 0; index < handlerArray.Length; index++)
        {
            pendingTasks[index] = StartHandler(handlerArray[index], notification, cancellationToken);
        }

        return Task.WhenAll(pendingTasks);
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