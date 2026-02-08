using Nerdigy.Mediator.Abstractions;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies notification publish behavior for the mediator runtime.
/// </summary>
public sealed class MediatorPublishTests
{
    /// <summary>
    /// Verifies that publishing without handlers completes successfully.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenNoHandlersRegistered_CompletesSuccessfully()
    {
        var provider = new TestServiceProvider();
        var mediator = new MediatorRuntime(provider);

        await mediator.Publish(new UserCreatedNotification("alpha"), CancellationToken.None);
    }

    /// <summary>
    /// Verifies that the default publisher invokes handlers sequentially.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WithDefaultPublisher_InvokesHandlersSequentially()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        INotificationHandler<UserCreatedNotification>[] handlers =
        [
            new BlockingNotificationHandler(firstStarted, firstRelease.Task),
            new TrackingNotificationHandler(secondStarted)
        ];

        var provider = new TestServiceProvider(
            (typeof(IEnumerable<INotificationHandler<UserCreatedNotification>>), handlers));
        var mediator = new MediatorRuntime(provider);

        var publishTask = mediator.Publish(new UserCreatedNotification("beta"), CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondStarted.Task.IsCompleted);

        firstRelease.SetResult(true);
        await publishTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(secondStarted.Task.IsCompleted);
    }

    /// <summary>
    /// Verifies that the parallel publisher starts all handlers without waiting for previous handlers to finish.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WithTaskWhenAllPublisher_InvokesHandlersConcurrently()
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandlers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        INotificationHandler<UserCreatedNotification>[] handlers =
        [
            new BlockingNotificationHandler(firstStarted, releaseHandlers.Task),
            new BlockingNotificationHandler(secondStarted, releaseHandlers.Task)
        ];

        var provider = new TestServiceProvider(
            (typeof(IEnumerable<INotificationHandler<UserCreatedNotification>>), handlers));
        var mediator = new MediatorRuntime(provider, new TaskWhenAllPublisher());

        var publishTask = mediator.Publish(new UserCreatedNotification("gamma"), CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        releaseHandlers.SetResult(true);
        await publishTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Verifies that publishing a null notification throws.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenNotificationIsNull_ThrowsArgumentNullException()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Publish<UserCreatedNotification>(null!, CancellationToken.None));
    }

    /// <summary>
    /// Represents a sample notification for publish tests.
    /// </summary>
    /// <param name="UserName">The created user name.</param>
    private sealed record UserCreatedNotification(string UserName) : INotification;

    /// <summary>
    /// Handles a notification by signaling start and then waiting on an external gate.
    /// </summary>
    private sealed class BlockingNotificationHandler : INotificationHandler<UserCreatedNotification>
    {
        private readonly Task _gate;
        private readonly TaskCompletionSource<bool> _started;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlockingNotificationHandler"/> class.
        /// </summary>
        /// <param name="started">The signal set when handling begins.</param>
        /// <param name="gate">The gate task that controls completion.</param>
        public BlockingNotificationHandler(TaskCompletionSource<bool> started, Task gate)
        {
            ArgumentNullException.ThrowIfNull(started);
            ArgumentNullException.ThrowIfNull(gate);
            _started = started;
            _gate = gate;
        }

        /// <summary>
        /// Handles the notification by signaling start and awaiting the gate.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the gate is released.</returns>
        public async Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _started.TrySetResult(true);
            await _gate.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Handles a notification by signaling that execution started.
    /// </summary>
    private sealed class TrackingNotificationHandler : INotificationHandler<UserCreatedNotification>
    {
        private readonly TaskCompletionSource<bool> _started;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrackingNotificationHandler"/> class.
        /// </summary>
        /// <param name="started">The signal set when handling begins.</param>
        public TrackingNotificationHandler(TaskCompletionSource<bool> started)
        {
            ArgumentNullException.ThrowIfNull(started);
            _started = started;
        }

        /// <summary>
        /// Handles the notification by signaling that execution started.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _started.TrySetResult(true);

            return Task.CompletedTask;
        }
    }
}