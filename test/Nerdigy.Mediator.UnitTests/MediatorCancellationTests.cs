using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies how exception actions that are canceled by the caller's token affect exception processing.
/// </summary>
public sealed class MediatorCancellationTests
{
    /// <summary>
    /// Verifies an action canceled by the caller's token is not a processing failure: the caller receives the original
    /// exception and later actions still run.
    /// </summary>
    /// <param name="stream">Whether the failure happens on the stream path instead of the request path.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenCallerCanceledAndActionObservesToken_ThrowsOriginalAndRunsRemainingActions(bool stream)
    {
        List<string> events = [];
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException(cancellation.Token);
        var mediator = CreateMediator(original, cancellation, new TokenObservingAction(), new RecordingAction(events));

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(mediator, stream, cancellation.Token));

        Assert.Same(original, thrown);
        Assert.Equal(["recorded"], events);
    }

    /// <summary>
    /// Verifies an action that fails with another exception while the caller's token is canceled is still a processing failure.
    /// </summary>
    /// <param name="stream">Whether the failure happens on the stream path instead of the request path.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenCallerCanceledAndActionThrowsOtherException_IsProcessingFailure(bool stream)
    {
        List<string> events = [];
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException(cancellation.Token);
        var actionFailure = new FormatException("logger failure");
        var mediator = CreateMediator(original, cancellation, new ThrowingAction(actionFailure), new RecordingAction(events));

        var thrown = await Assert.ThrowsAsync<MediatorExceptionProcessingException>(() => Run(mediator, stream, cancellation.Token));

        Assert.Same(original, thrown.InnerException);
        Assert.Same(actionFailure, Assert.Single(thrown.ProcessingExceptions));
        Assert.Equal(["recorded"], events);
    }

    /// <summary>
    /// Verifies an action that throws a cancellation while the caller's token is not canceled is still a processing failure.
    /// </summary>
    /// <param name="stream">Whether the failure happens on the stream path instead of the request path.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenCallerDidNotCancelAndActionThrowsCancellation_IsProcessingFailure(bool stream)
    {
        List<string> events = [];
        var original = new InvalidOperationException("business failure");
        var actionFailure = new OperationCanceledException("action timeout");
        var mediator = CreateMediator(original, cancellation: null, new ThrowingAction(actionFailure), new RecordingAction(events));

        var thrown = await Assert.ThrowsAsync<MediatorExceptionProcessingException>(() => Run(mediator, stream, CancellationToken.None));

        Assert.Same(original, thrown.InnerException);
        Assert.Same(actionFailure, Assert.Single(thrown.ProcessingExceptions));
        Assert.Equal(["recorded"], events);
    }

    /// <summary>
    /// Creates a mediator whose handler fails and whose exception actions run in the given order.
    /// </summary>
    /// <param name="failure">The exception the handler fails with.</param>
    /// <param name="cancellation">A token source the handler cancels before failing, if any.</param>
    /// <param name="actions">The exception actions.</param>
    /// <returns>The mediator.</returns>
    private static MediatorRuntime CreateMediator(
        Exception failure,
        CancellationTokenSource? cancellation,
        params IRequestExceptionAction<CancelProbe, Exception>[] actions)
    {
        var handler = new FailingHandler(failure, cancellation);

        return new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<CancelProbe, string>), handler),
            (typeof(IStreamRequestHandler<CancelProbe, int>), handler),
            (typeof(IEnumerable<IRequestExceptionAction<CancelProbe, Exception>>), actions)));
    }

    /// <summary>
    /// Sends the request, or reads its stream to the end.
    /// </summary>
    /// <param name="mediator">The mediator.</param>
    /// <param name="stream">Whether to use the stream path.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task that represents the operation.</returns>
    private static async Task Run(MediatorRuntime mediator, bool stream, CancellationToken cancellationToken)
    {
        if (!stream)
        {
            _ = await mediator.Send(new CancelProbe(), cancellationToken);

            return;
        }

        await foreach (var _ in mediator.CreateStream(new CancelProbe(), cancellationToken))
        {
        }
    }

    /// <summary>
    /// Represents a message that can be sent as a request or read as a stream.
    /// </summary>
    private sealed record CancelProbe : IRequest<string>, IStreamRequest<int>;

    /// <summary>
    /// Fails on both paths, after optionally canceling the caller's token.
    /// </summary>
    /// <param name="failure">The exception to fail with.</param>
    /// <param name="cancellation">A token source to cancel before failing, if any.</param>
    private sealed class FailingHandler(Exception failure, CancellationTokenSource? cancellation)
        : IRequestHandler<CancelProbe, string>, IStreamRequestHandler<CancelProbe, int>
    {
        /// <inheritdoc />
        async Task<string> IRequestHandler<CancelProbe, string>.Handle(CancelProbe request, CancellationToken cancellationToken)
        {
            await Fail();

            return string.Empty;
        }

        /// <inheritdoc />
        async IAsyncEnumerable<int> IStreamRequestHandler<CancelProbe, int>.Handle(
            CancelProbe request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return 1;

            await Fail();
        }

        /// <summary>
        /// Cancels the token source, if any, and throws the failure.
        /// </summary>
        /// <returns>A task that faults with the failure.</returns>
        private async Task Fail()
        {
            await Task.Yield();

            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
            }

            throw failure;
        }
    }

    /// <summary>
    /// Awaits work that observes the caller's token, as a logging action would.
    /// </summary>
    private sealed class TokenObservingAction : IRequestExceptionAction<CancelProbe, Exception>
    {
        /// <inheritdoc />
        public async Task Execute(CancelProbe request, Exception exception, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>
    /// Throws a supplied exception.
    /// </summary>
    /// <param name="failure">The exception to throw.</param>
    private sealed class ThrowingAction(Exception failure) : IRequestExceptionAction<CancelProbe, Exception>
    {
        /// <inheritdoc />
        public Task Execute(CancelProbe request, Exception exception, CancellationToken cancellationToken)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Records that it ran.
    /// </summary>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class RecordingAction(List<string> events) : IRequestExceptionAction<CancelProbe, Exception>
    {
        /// <inheritdoc />
        public Task Execute(CancelProbe request, Exception exception, CancellationToken cancellationToken)
        {
            events.Add("recorded");

            return Task.CompletedTask;
        }
    }
}