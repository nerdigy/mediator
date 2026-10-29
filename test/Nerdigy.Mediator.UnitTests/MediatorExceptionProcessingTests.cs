using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies the exception processing rules: each implementation runs once, caller cancellation is never recovered, and
/// failures during processing never hide the original exception.
/// </summary>
public sealed class MediatorExceptionProcessingTests
{
    /// <summary>
    /// Verifies a generic exception action closed at every level of the exception hierarchy runs once, at the most specific level.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenGenericActionMatchesSeveralLevels_RunsItOnce()
    {
        List<string> events = [];
        var mediator = new MediatorRuntime(new TestServiceProvider(
        [
            (typeof(IRequestHandler<FailingRequest, string>), new FailingRequestHandler(new InvalidOperationException("boom"))),
            .. GenericActionAtEveryLevel(events)
        ]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FailingRequest(), CancellationToken.None));

        Assert.Equal(["action:InvalidOperationException"], events);
    }

    /// <summary>
    /// Verifies a declining generic exception handler closed at every level runs once.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenDecliningGenericHandlerMatchesSeveralLevels_RunsItOnce()
    {
        List<string> events = [];
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FailingRequest, string>), new FailingRequestHandler(new InvalidOperationException("boom"))),
            (typeof(IEnumerable<IRequestExceptionHandler<FailingRequest, string, InvalidOperationException>>),
                new IRequestExceptionHandler<FailingRequest, string, InvalidOperationException>[] { new DecliningHandler<InvalidOperationException>(events) }),
            (typeof(IEnumerable<IRequestExceptionHandler<FailingRequest, string, SystemException>>),
                new IRequestExceptionHandler<FailingRequest, string, SystemException>[] { new DecliningHandler<SystemException>(events) }),
            (typeof(IEnumerable<IRequestExceptionHandler<FailingRequest, string, Exception>>),
                new IRequestExceptionHandler<FailingRequest, string, Exception>[] { new DecliningHandler<Exception>(events) })));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FailingRequest(), CancellationToken.None));

        Assert.Equal(["handler:InvalidOperationException"], events);
    }

    /// <summary>
    /// Verifies a catch-all exception handler cannot turn the caller's cancellation into a response, and actions still run.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenCallerCanceledWithCatchAllHandler_ThrowsCancellationAndRunsActions()
    {
        List<string> events = [];
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FailingRequest, string>), new CancellationObservingHandler()),
            (typeof(IEnumerable<IRequestExceptionHandler<FailingRequest, string, Exception>>),
                new IRequestExceptionHandler<FailingRequest, string, Exception>[] { new FallbackHandler(events) }),
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, Exception>>),
                new IRequestExceptionAction<FailingRequest, Exception>[] { new RecordingAction<Exception>(events) })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mediator.Send(new FailingRequest(), cancellation.Token));

        Assert.Equal(["action:Exception"], events);
    }

    /// <summary>
    /// Verifies a cancellation the caller did not request, such as a behavior's timeout, can still be handled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenCancellationWasNotRequestedByCaller_AllowsHandlerToRecover()
    {
        List<string> events = [];
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FailingRequest, string>), new FailingRequestHandler(new OperationCanceledException("timeout"))),
            (typeof(IEnumerable<IRequestExceptionHandler<FailingRequest, string, Exception>>),
                new IRequestExceptionHandler<FailingRequest, string, Exception>[] { new FallbackHandler(events) })));

        var response = await mediator.Send(new FailingRequest(), CancellationToken.None);

        Assert.Equal("fallback", response);
    }

    /// <summary>
    /// Verifies a failing exception action does not hide the original exception or stop later actions.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenExceptionActionThrows_WrapsOriginalAndRunsRemainingActions()
    {
        List<string> events = [];
        var original = new InvalidOperationException("business failure");
        var actionFailure = new FormatException("logger failure");
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FailingRequest, string>), new FailingRequestHandler(original)),
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, InvalidOperationException>>),
                new IRequestExceptionAction<FailingRequest, InvalidOperationException>[] { new ThrowingAction(actionFailure) }),
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, Exception>>),
                new IRequestExceptionAction<FailingRequest, Exception>[] { new RecordingAction<Exception>(events) })));

        var thrown = await Assert.ThrowsAsync<MediatorExceptionProcessingException>(
            () => mediator.Send(new FailingRequest(), CancellationToken.None));

        Assert.Same(original, thrown.InnerException);
        Assert.Same(actionFailure, Assert.Single(thrown.ProcessingExceptions));
        Assert.Equal(["action:Exception"], events);
    }

    /// <summary>
    /// Verifies a failing exception handler does not hide the original exception, and actions still run.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenExceptionHandlerThrows_WrapsOriginalAndRunsActions()
    {
        List<string> events = [];
        var original = new InvalidOperationException("business failure");
        var handlerFailure = new FormatException("handler failure");
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FailingRequest, string>), new FailingRequestHandler(original)),
            (typeof(IEnumerable<IRequestExceptionHandler<FailingRequest, string, Exception>>),
                new IRequestExceptionHandler<FailingRequest, string, Exception>[] { new ThrowingHandler(handlerFailure) }),
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, Exception>>),
                new IRequestExceptionAction<FailingRequest, Exception>[] { new RecordingAction<Exception>(events) })));

        var thrown = await Assert.ThrowsAsync<MediatorExceptionProcessingException>(
            () => mediator.Send(new FailingRequest(), CancellationToken.None));

        Assert.Same(original, thrown.InnerException);
        Assert.Same(handlerFailure, Assert.Single(thrown.ProcessingExceptions));
        Assert.Equal(["action:Exception"], events);
    }

    /// <summary>
    /// Verifies the original exception keeps its identity when exception processing succeeds.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenNoExceptionComponentHandles_RethrowsOriginalInstance()
    {
        var original = new InvalidOperationException("business failure");
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FailingRequest, string>), new FailingRequestHandler(original))));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FailingRequest(), CancellationToken.None));

        Assert.Same(original, thrown);
    }

    /// <summary>
    /// Verifies a synchronous throw from a handler on the fast path still goes through exception handlers.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenHandlerThrowsSynchronouslyWithoutPipeline_AppliesExceptionHandlers()
    {
        List<string> events = [];
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FailingRequest, string>), new SynchronouslyThrowingHandler()),
            (typeof(IEnumerable<IRequestExceptionHandler<FailingRequest, string, Exception>>),
                new IRequestExceptionHandler<FailingRequest, string, Exception>[] { new FallbackHandler(events) })));

        var response = await mediator.Send(new FailingRequest(), CancellationToken.None);

        Assert.Equal("fallback", response);
    }

    /// <summary>
    /// Verifies a missing handler fails through exception processing, so exception actions observe it.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenHandlerIsMissing_RunsExceptionActions()
    {
        List<string> events = [];
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, Exception>>),
                new IRequestExceptionAction<FailingRequest, Exception>[] { new RecordingAction<Exception>(events) })));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FailingRequest(), CancellationToken.None));

        Assert.Equal(["action:Exception"], events);
    }

    /// <summary>
    /// Registers a recording action closed at each level of the <see cref="InvalidOperationException"/> hierarchy, the
    /// way the container closes an open generic action.
    /// </summary>
    /// <param name="events">The event sink used by tests.</param>
    /// <returns>The registrations.</returns>
    private static (Type, object?)[] GenericActionAtEveryLevel(List<string> events)
    {
        return
        [
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, InvalidOperationException>>),
                new IRequestExceptionAction<FailingRequest, InvalidOperationException>[] { new RecordingAction<InvalidOperationException>(events) }),
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, SystemException>>),
                new IRequestExceptionAction<FailingRequest, SystemException>[] { new RecordingAction<SystemException>(events) }),
            (typeof(IEnumerable<IRequestExceptionAction<FailingRequest, Exception>>),
                new IRequestExceptionAction<FailingRequest, Exception>[] { new RecordingAction<Exception>(events) })
        ];
    }

    /// <summary>
    /// Represents a request whose handler fails.
    /// </summary>
    private sealed record FailingRequest : IRequest<string>;

    /// <summary>
    /// Fails with a supplied exception.
    /// </summary>
    /// <param name="exception">The exception to fail with.</param>
    private sealed class FailingRequestHandler(Exception exception) : IRequestHandler<FailingRequest, string>
    {
        /// <inheritdoc />
        public async Task<string> Handle(FailingRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();

            throw exception;
        }
    }

    /// <summary>
    /// Throws before returning a task.
    /// </summary>
    private sealed class SynchronouslyThrowingHandler : IRequestHandler<FailingRequest, string>
    {
        /// <inheritdoc />
        public Task<string> Handle(FailingRequest request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("synchronous failure");
        }
    }

    /// <summary>
    /// Fails when its cancellation token is canceled.
    /// </summary>
    private sealed class CancellationObservingHandler : IRequestHandler<FailingRequest, string>
    {
        /// <inheritdoc />
        public Task<string> Handle(FailingRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult("completed");
        }
    }

    /// <summary>
    /// Handles every exception with a fallback response.
    /// </summary>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class FallbackHandler(List<string> events) : IRequestExceptionHandler<FailingRequest, string, Exception>
    {
        /// <inheritdoc />
        public Task Handle(FailingRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            events.Add("fallback");
            state.SetHandled("fallback");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records that it ran and declines to handle the exception.
    /// </summary>
    /// <typeparam name="TException">The exception type the handler is registered for.</typeparam>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class DecliningHandler<TException>(List<string> events) : IRequestExceptionHandler<FailingRequest, string, TException>
        where TException : Exception
    {
        /// <inheritdoc />
        public Task Handle(FailingRequest request, TException exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            events.Add($"handler:{typeof(TException).Name}");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws while handling an exception.
    /// </summary>
    /// <param name="failure">The exception to throw.</param>
    private sealed class ThrowingHandler(Exception failure) : IRequestExceptionHandler<FailingRequest, string, Exception>
    {
        /// <inheritdoc />
        public Task Handle(FailingRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Records the exception level it ran for.
    /// </summary>
    /// <typeparam name="TException">The exception type the action is registered for.</typeparam>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class RecordingAction<TException>(List<string> events) : IRequestExceptionAction<FailingRequest, TException>
        where TException : Exception
    {
        /// <inheritdoc />
        public Task Execute(FailingRequest request, TException exception, CancellationToken cancellationToken)
        {
            events.Add($"action:{typeof(TException).Name}");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws while observing an exception.
    /// </summary>
    /// <param name="failure">The exception to throw.</param>
    private sealed class ThrowingAction(Exception failure) : IRequestExceptionAction<FailingRequest, InvalidOperationException>
    {
        /// <inheritdoc />
        public Task Execute(FailingRequest request, InvalidOperationException exception, CancellationToken cancellationToken)
        {
            throw failure;
        }
    }
}