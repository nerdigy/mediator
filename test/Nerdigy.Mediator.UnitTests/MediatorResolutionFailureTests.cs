using System.Diagnostics;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies that a failure to resolve pipeline components or notification handlers reaches the caller as a failed task.
/// </summary>
/// <remarks>
/// A telemetry listener switches <c>Send</c> and <c>Publish</c> between two code paths for the whole process, so these
/// tests must not run in parallel with other tests that attach one.
/// </remarks>
[Collection("Telemetry")]
public sealed class MediatorResolutionFailureTests
{
    /// <summary>
    /// Identifies a pipeline component set that <c>Send</c> resolves.
    /// </summary>
    public enum PipelineComponent
    {
        /// <summary>The request preprocessors.</summary>
        PreProcessor,

        /// <summary>The pipeline behaviors.</summary>
        Behavior,

        /// <summary>The request postprocessors.</summary>
        PostProcessor,
    }

    /// <summary>
    /// Verifies a resolution failure returns a faulted task carrying the original exception, and exception actions run.
    /// </summary>
    /// <param name="component">The component set that fails to resolve.</param>
    /// <param name="withListener">Whether a telemetry listener is attached.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(PipelineComponent.PreProcessor, false)]
    [InlineData(PipelineComponent.PreProcessor, true)]
    [InlineData(PipelineComponent.Behavior, false)]
    [InlineData(PipelineComponent.Behavior, true)]
    [InlineData(PipelineComponent.PostProcessor, false)]
    [InlineData(PipelineComponent.PostProcessor, true)]
    public async Task Send_WhenPipelineComponentResolutionFails_ReturnsFaultedTaskAndRunsAction(PipelineComponent component, bool withListener)
    {
        List<string> events = [];
        var failure = new InvalidOperationException("resolution failed");
        var provider = new ThrowingServiceProvider(
            new TestServiceProvider(
                (typeof(IRequestHandler<ResolutionRequest, string>), new CompletingHandler()),
                (typeof(IEnumerable<IRequestExceptionAction<ResolutionRequest, Exception>>),
                    new IRequestExceptionAction<ResolutionRequest, Exception>[] { new RecordingAction(events) })),
            type => type == ServiceTypeOf(component),
            failure);
        var mediator = new MediatorRuntime(provider);
        using var listener = withListener ? StartListener() : null;

        var task = mediator.Send(new ResolutionRequest(), CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Same(failure, thrown);
        Assert.True(task.IsFaulted);
        Assert.Equal(["action"], events);
    }

    /// <summary>
    /// Verifies an exception handler can supply a response for a resolution failure.
    /// </summary>
    /// <param name="component">The component set that fails to resolve.</param>
    /// <param name="withListener">Whether a telemetry listener is attached.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(PipelineComponent.PreProcessor, false)]
    [InlineData(PipelineComponent.PreProcessor, true)]
    [InlineData(PipelineComponent.Behavior, false)]
    [InlineData(PipelineComponent.Behavior, true)]
    [InlineData(PipelineComponent.PostProcessor, false)]
    [InlineData(PipelineComponent.PostProcessor, true)]
    public async Task Send_WhenPipelineComponentResolutionFails_ExceptionHandlerCanSupplyResponse(PipelineComponent component, bool withListener)
    {
        var provider = new ThrowingServiceProvider(
            new TestServiceProvider(
                (typeof(IRequestHandler<ResolutionRequest, string>), new CompletingHandler()),
                (typeof(IEnumerable<IRequestExceptionHandler<ResolutionRequest, string, Exception>>),
                    new IRequestExceptionHandler<ResolutionRequest, string, Exception>[] { new FallbackHandler() })),
            type => type == ServiceTypeOf(component),
            new InvalidOperationException("resolution failed"));
        var mediator = new MediatorRuntime(provider);
        using var listener = withListener ? StartListener() : null;

        var response = await mediator.Send(new ResolutionRequest(), CancellationToken.None);

        Assert.Equal("fallback", response);
    }

    /// <summary>
    /// Verifies that when exception processing also cannot resolve its components, as with a disposed scope, the
    /// resolution failure is kept as the inner exception.
    /// </summary>
    /// <param name="withListener">Whether a telemetry listener is attached.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WhenEveryResolutionFails_WrapsResolutionFailureInProcessingException(bool withListener)
    {
        var failure = new ObjectDisposedException("scope");
        var provider = new ThrowingServiceProvider(new TestServiceProvider(), static _ => true, failure);
        var mediator = new MediatorRuntime(provider);
        using var listener = withListener ? StartListener() : null;

        var task = mediator.Send(new ResolutionRequest(), CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<MediatorExceptionProcessingException>(() => task);
        Assert.Same(failure, thrown.InnerException);
        Assert.NotEmpty(thrown.ProcessingExceptions);
    }

    /// <summary>
    /// Verifies the fast path: no pipeline components and a synchronously completed handler return the handler's own task.
    /// </summary>
    [Fact]
    public void Send_WhenNoPipelineComponentsAndHandlerCompletesSynchronously_ReturnsHandlerTask()
    {
        var handlerTask = Task.FromResult("cached");
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<ResolutionRequest, string>), new CachedTaskHandler(handlerTask))));

        var sendTask = mediator.Send(new ResolutionRequest(), CancellationToken.None);

        Assert.Same(handlerTask, sendTask);
    }

    /// <summary>
    /// Verifies a notification handler resolution failure returns a faulted task for both built-in publishers.
    /// </summary>
    /// <param name="parallel">Whether the parallel publisher is used.</param>
    /// <param name="withListener">Whether a telemetry listener is attached.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Publish_WhenHandlerResolutionFails_ReturnsFaultedTask(bool parallel, bool withListener)
    {
        var failure = new InvalidOperationException("resolution failed");
        var mediator = CreatePublishMediator(parallel, failure);
        using var listener = withListener ? StartListener() : null;

        var task = mediator.Publish(new ResolutionNotification(), CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Same(failure, thrown);
        Assert.True(task.IsFaulted);
    }

    /// <summary>
    /// Verifies a cancellation thrown while resolving notification handlers returns a canceled task.
    /// </summary>
    /// <param name="parallel">Whether the parallel publisher is used.</param>
    /// <param name="withListener">Whether a telemetry listener is attached.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Publish_WhenHandlerResolutionThrowsCancellation_ReturnsCanceledTask(bool parallel, bool withListener)
    {
        var mediator = CreatePublishMediator(parallel, new OperationCanceledException("resolution canceled"));
        using var listener = withListener ? StartListener() : null;

        var task = mediator.Publish(new ResolutionNotification(), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
    }

    /// <summary>
    /// Verifies a publisher that throws before returning a task produces a faulted task.
    /// </summary>
    /// <param name="withListener">Whether a telemetry listener is attached.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WhenPublisherThrowsSynchronously_ReturnsFaultedTask(bool withListener)
    {
        var failure = new FormatException("publisher failed");
        var mediator = new MediatorRuntime(new TestServiceProvider(), new ThrowingPublisher(failure));
        using var listener = withListener ? StartListener() : null;

        var task = mediator.Publish(new ResolutionNotification(), CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<FormatException>(() => task);
        Assert.Same(failure, thrown);
        Assert.True(task.IsFaulted);
    }

    /// <summary>
    /// Verifies a publisher that throws a cancellation before returning a task produces a canceled task.
    /// </summary>
    /// <param name="withListener">Whether a telemetry listener is attached.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WhenPublisherThrowsCancellationSynchronously_ReturnsCanceledTask(bool withListener)
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(), new ThrowingPublisher(new OperationCanceledException("publisher canceled")));
        using var listener = withListener ? StartListener() : null;

        var task = mediator.Publish(new ResolutionNotification(), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
    }

    /// <summary>
    /// Gets the service type a pipeline component set resolves through.
    /// </summary>
    /// <param name="component">The component set.</param>
    /// <returns>The closed sequence service type.</returns>
    private static Type ServiceTypeOf(PipelineComponent component)
    {
        return component switch
        {
            PipelineComponent.PreProcessor => typeof(IEnumerable<IRequestPreProcessor<ResolutionRequest>>),
            PipelineComponent.Behavior => typeof(IEnumerable<IPipelineBehavior<ResolutionRequest, string>>),
            PipelineComponent.PostProcessor => typeof(IEnumerable<IRequestPostProcessor<ResolutionRequest, string>>),
            _ => throw new ArgumentOutOfRangeException(nameof(component)),
        };
    }

    /// <summary>
    /// Creates a mediator whose notification handlers fail to resolve.
    /// </summary>
    /// <param name="parallel">Whether the parallel publisher is used.</param>
    /// <param name="failure">The resolution failure.</param>
    /// <returns>The mediator.</returns>
    private static MediatorRuntime CreatePublishMediator(bool parallel, Exception failure)
    {
        var provider = new ThrowingServiceProvider(
            new TestServiceProvider(),
            static type => type == typeof(IEnumerable<INotificationHandler<ResolutionNotification>>),
            failure);

        return new MediatorRuntime(provider, parallel ? new TaskWhenAllPublisher() : new ForeachAwaitPublisher());
    }

    /// <summary>
    /// Attaches a listener that samples every mediator activity.
    /// </summary>
    /// <returns>The listener, which the test must dispose.</returns>
    private static ActivityListener StartListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == MediatorTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    /// <summary>
    /// Represents a request used by these tests.
    /// </summary>
    private sealed record ResolutionRequest : IRequest<string>;

    /// <summary>
    /// Represents a notification used by these tests.
    /// </summary>
    private sealed record ResolutionNotification : INotification;

    /// <summary>
    /// Completes with a response.
    /// </summary>
    private sealed class CompletingHandler : IRequestHandler<ResolutionRequest, string>
    {
        /// <inheritdoc />
        public Task<string> Handle(ResolutionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult("completed");
        }
    }

    /// <summary>
    /// Returns a task supplied by the test.
    /// </summary>
    /// <param name="task">The task to return.</param>
    private sealed class CachedTaskHandler(Task<string> task) : IRequestHandler<ResolutionRequest, string>
    {
        /// <inheritdoc />
        public Task<string> Handle(ResolutionRequest request, CancellationToken cancellationToken)
        {
            return task;
        }
    }

    /// <summary>
    /// Handles every exception with a fallback response.
    /// </summary>
    private sealed class FallbackHandler : IRequestExceptionHandler<ResolutionRequest, string, Exception>
    {
        /// <inheritdoc />
        public Task Handle(ResolutionRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            state.SetHandled("fallback");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records that it ran.
    /// </summary>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class RecordingAction(List<string> events) : IRequestExceptionAction<ResolutionRequest, Exception>
    {
        /// <inheritdoc />
        public Task Execute(ResolutionRequest request, Exception exception, CancellationToken cancellationToken)
        {
            events.Add("action");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws before returning a task.
    /// </summary>
    /// <param name="failure">The exception to throw.</param>
    private sealed class ThrowingPublisher(Exception failure) : INotificationPublisher
    {
        /// <inheritdoc />
        public Task Publish<TNotification>(
            IEnumerable<INotificationHandler<TNotification>> handlers,
            TNotification notification,
            CancellationToken cancellationToken)
            where TNotification : INotification
        {
            throw failure;
        }
    }
}