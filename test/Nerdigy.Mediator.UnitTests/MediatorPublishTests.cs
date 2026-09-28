using Nerdigy.Mediator.Contracts;

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
    /// Verifies that a handler throwing synchronously neither skips later handlers nor abandons in-flight handlers.
    /// </summary>
    /// <param name="useCollection">Whether handlers are supplied as an <see cref="ICollection{T}"/> or a lazy enumerable.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publish_WithTaskWhenAllPublisher_WhenHandlerThrowsSynchronously_RunsAllHandlersBeforeFaulting(bool useCollection)
    {
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("sync failure");

        INotificationHandler<UserCreatedNotification>[] handlers =
        [
            new BlockingNotificationHandler(firstStarted, firstRelease.Task),
            new SynchronouslyThrowingNotificationHandler(failure),
            new TrackingNotificationHandler(thirdStarted)
        ];

        var publishTask = new TaskWhenAllPublisher().Publish(
            useCollection ? handlers : EnumerateLazily(handlers),
            new UserCreatedNotification("theta"),
            CancellationToken.None);

        Assert.True(firstStarted.Task.IsCompleted);
        Assert.True(thirdStarted.Task.IsCompleted);
        Assert.False(publishTask.IsCompleted);

        firstRelease.SetResult(true);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => publishTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, exception);
    }

    /// <summary>
    /// Verifies that a handler throwing <see cref="OperationCanceledException"/> synchronously yields a canceled task, matching an async handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WithTaskWhenAllPublisher_WhenHandlerThrowsCancellationSynchronously_ReturnsCanceledTask()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var handler = new CountingNotificationHandler<UserCreatedNotification>();

        INotificationHandler<UserCreatedNotification>[] handlers =
        [
            new SynchronouslyThrowingNotificationHandler(new OperationCanceledException(cancellation.Token)),
            handler
        ];

        var publishTask = new TaskWhenAllPublisher().Publish(
            handlers,
            new UserCreatedNotification("iota"),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishTask);
        Assert.True(publishTask.IsCanceled);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// Verifies that a single handler's synchronous exception still reaches the caller.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WithTaskWhenAllPublisher_WhenSingleHandlerThrowsSynchronously_PropagatesException()
    {
        var failure = new InvalidOperationException("sync failure");
        var mediator = CreateMediator(
            useParallelPublisher: true,
            RegisterHandlers<UserCreatedNotification>(new SynchronouslyThrowingNotificationHandler(failure)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Publish(new UserCreatedNotification("kappa"), CancellationToken.None));
        Assert.Same(failure, exception);
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
    /// Verifies that publishing through an <see cref="INotification"/> reference invokes the concrete type's handlers.
    /// </summary>
    /// <param name="useParallelPublisher">Whether to use <see cref="TaskWhenAllPublisher"/>.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WhenNotificationTypedAsInterface_InvokesConcreteHandlers(bool useParallelPublisher)
    {
        var handler = new CountingNotificationHandler<UserCreatedNotification>();
        var mediator = CreateMediator(useParallelPublisher, RegisterHandlers(handler));
        INotification notification = new UserCreatedNotification("delta");

        await mediator.Publish(notification, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Same(notification, handler.LastNotification);
    }

    /// <summary>
    /// Verifies that publishing through a base-class reference invokes the derived type's handlers.
    /// </summary>
    /// <param name="useParallelPublisher">Whether to use <see cref="TaskWhenAllPublisher"/>.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WhenNotificationTypedAsBaseClass_InvokesDerivedHandlers(bool useParallelPublisher)
    {
        var handler = new CountingNotificationHandler<OrderPlacedNotification>();
        var mediator = CreateMediator(useParallelPublisher, RegisterHandlers(handler));
        OrderNotification notification = new OrderPlacedNotification(42);

        await mediator.Publish(notification, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Same(notification, handler.LastNotification);
    }

    /// <summary>
    /// Verifies that publishing from a generic wrapper closed over <see cref="INotification"/> invokes the concrete type's handlers.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenCalledThroughGenericWrapper_InvokesConcreteHandlers()
    {
        var handler = new CountingNotificationHandler<UserCreatedNotification>();
        var mediator = CreateMediator(useParallelPublisher: false, RegisterHandlers(handler));
        IReadOnlyList<INotification> pendingNotifications = [new UserCreatedNotification("epsilon"), new UserCreatedNotification("zeta")];

        foreach (var notification in pendingNotifications)
        {
            await PublishThroughWrapper(mediator, notification);
        }

        Assert.Equal(2, handler.Calls);
    }

    /// <summary>
    /// Verifies that publishing a boxed value-type notification invokes the value type's handlers.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Publish_WhenValueTypeNotificationTypedAsInterface_InvokesConcreteHandlers()
    {
        var handler = new CountingNotificationHandler<CounterIncrementedNotification>();
        var mediator = CreateMediator(useParallelPublisher: false, RegisterHandlers(handler));
        INotification notification = new CounterIncrementedNotification(7);

        await mediator.Publish(notification, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(new CounterIncrementedNotification(7), handler.LastNotification);
    }

    /// <summary>
    /// Verifies that publishing through an <see cref="INotification"/> reference without handlers completes successfully.
    /// </summary>
    /// <param name="useParallelPublisher">Whether to use <see cref="TaskWhenAllPublisher"/>.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WhenInterfaceTypedNotificationHasNoHandlers_CompletesSuccessfully(bool useParallelPublisher)
    {
        var mediator = CreateMediator(useParallelPublisher);
        INotification notification = new UserCreatedNotification("eta");

        await mediator.Publish(notification, CancellationToken.None);
    }

    /// <summary>
    /// Creates a mediator backed by the supplied registrations.
    /// </summary>
    /// <param name="useParallelPublisher">Whether to use <see cref="TaskWhenAllPublisher"/>.</param>
    /// <param name="registrations">The service registrations used for type resolution.</param>
    /// <returns>A mediator instance.</returns>
    private static MediatorRuntime CreateMediator(
        bool useParallelPublisher,
        params (Type ServiceType, object? Implementation)[] registrations)
    {
        INotificationPublisher publisher = useParallelPublisher
            ? new TaskWhenAllPublisher()
            : new ForeachAwaitPublisher();

        return new MediatorRuntime(new TestServiceProvider(registrations), publisher);
    }

    /// <summary>
    /// Creates a handler collection registration for a notification type.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="handlers">The handlers to register.</param>
    /// <returns>A registration tuple for <see cref="TestServiceProvider"/>.</returns>
    private static (Type ServiceType, object? Implementation) RegisterHandlers<TNotification>(
        params INotificationHandler<TNotification>[] handlers)
        where TNotification : INotification
    {
        return (typeof(IEnumerable<INotificationHandler<TNotification>>), handlers);
    }

    /// <summary>
    /// Yields items through an iterator so consumers cannot treat the sequence as a collection.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items to yield.</param>
    /// <returns>A lazily evaluated sequence.</returns>
    private static IEnumerable<T> EnumerateLazily<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
    }

    /// <summary>
    /// Publishes a notification from generic code, mirroring domain-event dispatch helpers.
    /// </summary>
    /// <typeparam name="TNotification">The notification type inferred by the caller.</typeparam>
    /// <param name="publisher">The publisher.</param>
    /// <param name="notification">The notification to publish.</param>
    /// <returns>A task that completes when publishing finishes.</returns>
    private static Task PublishThroughWrapper<TNotification>(IPublisher publisher, TNotification notification)
        where TNotification : INotification
    {
        return publisher.Publish(notification, CancellationToken.None);
    }

    /// <summary>
    /// Represents a sample notification for publish tests.
    /// </summary>
    /// <param name="UserName">The created user name.</param>
    private sealed record UserCreatedNotification(string UserName) : INotification;

    /// <summary>
    /// Represents a base notification type for base-class publish tests.
    /// </summary>
    private abstract record OrderNotification : INotification;

    /// <summary>
    /// Represents a derived notification for base-class publish tests.
    /// </summary>
    /// <param name="OrderId">The placed order identifier.</param>
    private sealed record OrderPlacedNotification(int OrderId) : OrderNotification;

    /// <summary>
    /// Represents a value-type notification for boxed publish tests.
    /// </summary>
    /// <param name="Amount">The increment amount.</param>
    private readonly record struct CounterIncrementedNotification(int Amount) : INotification;

    /// <summary>
    /// Handles a notification by counting invocations and capturing the last notification.
    /// </summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    private sealed class CountingNotificationHandler<TNotification> : INotificationHandler<TNotification>
        where TNotification : INotification
    {
        private int _calls;

        /// <summary>
        /// Gets the number of handled notifications.
        /// </summary>
        public int Calls => _calls;

        /// <summary>
        /// Gets the most recently handled notification.
        /// </summary>
        public TNotification? LastNotification { get; private set; }

        /// <summary>
        /// Handles the notification by recording the invocation.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(TNotification notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            LastNotification = notification;

            return Task.CompletedTask;
        }
    }

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
    /// Handles a notification by throwing before returning a task, as a non-async handler would.
    /// </summary>
    private sealed class SynchronouslyThrowingNotificationHandler : INotificationHandler<UserCreatedNotification>
    {
        private readonly Exception _exception;

        /// <summary>
        /// Initializes a new instance of the <see cref="SynchronouslyThrowingNotificationHandler"/> class.
        /// </summary>
        /// <param name="exception">The exception to throw.</param>
        public SynchronouslyThrowingNotificationHandler(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _exception = exception;
        }

        /// <summary>
        /// Handles the notification by throwing synchronously.
        /// </summary>
        /// <param name="notification">The notification to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Never returns.</returns>
        public Task Handle(UserCreatedNotification notification, CancellationToken cancellationToken)
        {
            throw _exception;
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