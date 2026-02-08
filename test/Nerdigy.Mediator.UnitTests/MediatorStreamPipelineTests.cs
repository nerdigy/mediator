using System.Runtime.CompilerServices;
using Nerdigy.Mediator.Abstractions;
using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies stream pipeline and stream exception behavior for create-stream operations.
/// </summary>
public sealed class MediatorStreamPipelineTests
{
    /// <summary>
    /// Verifies stream behaviors execute in registration order around handler enumeration.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WithBehaviors_ExecutesInRegistrationOrder()
    {
        List<string> events = [];
        var handler = new RecordingStreamHandler(events);
        IStreamPipelineBehavior<StreamPipelineRequest, int>[] behaviors =
        [
            new RecordingStreamBehavior(events, "behavior-1"),
            new RecordingStreamBehavior(events, "behavior-2")
        ];
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamPipelineRequest, int>), handler),
            (typeof(IEnumerable<IStreamPipelineBehavior<StreamPipelineRequest, int>>), behaviors));
        var mediator = new MediatorRuntime(provider);

        var stream = mediator.CreateStream(new StreamPipelineRequest(2), CancellationToken.None);
        var values = await ToListAsync(stream, CancellationToken.None);

        Assert.Equal([1, 2], values);
        Assert.Equal(
            ["behavior-1:before", "behavior-2:before", "handler:start", "handler:end", "behavior-2:after", "behavior-1:after"],
            events);
    }

    /// <summary>
    /// Verifies preprocessors run before stream behaviors and handler execution when enumeration starts.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WithPreProcessor_RunsBeforeBehaviorAndHandler()
    {
        List<string> events = [];
        var handler = new RecordingStreamHandler(events);
        IRequestPreProcessor<StreamPipelineRequest>[] preprocessors = [new RecordingStreamPreProcessor(events)];
        IStreamPipelineBehavior<StreamPipelineRequest, int>[] behaviors = [new RecordingStreamBehavior(events, "behavior")];
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamPipelineRequest, int>), handler),
            (typeof(IEnumerable<IRequestPreProcessor<StreamPipelineRequest>>), preprocessors),
            (typeof(IEnumerable<IStreamPipelineBehavior<StreamPipelineRequest, int>>), behaviors));
        var mediator = new MediatorRuntime(provider);

        var stream = mediator.CreateStream(new StreamPipelineRequest(1), CancellationToken.None);
        Assert.Empty(events);

        _ = await ToListAsync(stream, CancellationToken.None);

        Assert.Equal(["pre", "behavior:before", "handler:start", "handler:end", "behavior:after"], events);
    }

    /// <summary>
    /// Verifies stream behaviors can short-circuit and skip handler execution.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenBehaviorShortCircuits_SkipsHandler()
    {
        List<string> events = [];
        var handler = new RecordingStreamHandler(events);
        IStreamPipelineBehavior<StreamPipelineRequest, int>[] behaviors = [new ShortCircuitStreamBehavior(events)];
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamPipelineRequest, int>), handler),
            (typeof(IEnumerable<IStreamPipelineBehavior<StreamPipelineRequest, int>>), behaviors));
        var mediator = new MediatorRuntime(provider);

        var values = await ToListAsync(mediator.CreateStream(new StreamPipelineRequest(10), CancellationToken.None), CancellationToken.None);

        Assert.Equal([42], values);
        Assert.Equal(["short-circuit"], events);
        Assert.False(handler.WasCalled);
    }

    /// <summary>
    /// Verifies stream exception handlers execute from most specific to least specific type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenExceptionHandled_UsesMostSpecificHandler()
    {
        var handler = new ThrowingStreamHandler();
        var specific = new SpecificStreamExceptionHandler();
        var fallback = new FallbackStreamExceptionHandler();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamPipelineRequest, int>), handler),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<StreamPipelineRequest, int, InvalidOperationException>>), new IStreamRequestExceptionHandler<StreamPipelineRequest, int, InvalidOperationException>[] { specific }),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<StreamPipelineRequest, int, Exception>>), new IStreamRequestExceptionHandler<StreamPipelineRequest, int, Exception>[] { fallback }));
        var mediator = new MediatorRuntime(provider);

        var values = await ToListAsync(mediator.CreateStream(new StreamPipelineRequest(1), CancellationToken.None), CancellationToken.None);

        Assert.Equal([7], values);
        Assert.True(specific.WasCalled);
        Assert.False(fallback.WasCalled);
    }

    /// <summary>
    /// Verifies request exception actions execute for unhandled stream exceptions and the exception is rethrown.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenExceptionUnhandled_ExecutesActionsAndRethrows()
    {
        var handler = new ThrowingStreamHandler();
        var action = new RecordingStreamExceptionAction();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<StreamPipelineRequest, int>), handler),
            (typeof(IEnumerable<IRequestExceptionAction<StreamPipelineRequest, InvalidOperationException>>), new IRequestExceptionAction<StreamPipelineRequest, InvalidOperationException>[] { action }));
        var mediator = new MediatorRuntime(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToListAsync(mediator.CreateStream(new StreamPipelineRequest(1), CancellationToken.None), CancellationToken.None));
        Assert.True(action.WasCalled);
    }

    /// <summary>
    /// Enumerates an asynchronous sequence and returns its values as a list.
    /// </summary>
    /// <typeparam name="T">The sequence element type.</typeparam>
    /// <param name="source">The sequence to enumerate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the sequence values.</returns>
    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<T> results = [];

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            results.Add(item);
        }

        return results;
    }

    /// <summary>
    /// Represents a sample stream request for pipeline tests.
    /// </summary>
    /// <param name="Count">The number of values requested from the stream.</param>
    private sealed record StreamPipelineRequest(int Count) : IStreamRequest<int>;

    /// <summary>
    /// Handles <see cref="StreamPipelineRequest"/> requests and records lifecycle events.
    /// </summary>
    private sealed class RecordingStreamHandler : IStreamRequestHandler<StreamPipelineRequest, int>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingStreamHandler"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public RecordingStreamHandler(List<string> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            _events = events;
        }

        /// <summary>
        /// Gets a value indicating whether the handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Handles a stream request and returns incrementing values.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of stream values.</returns>
        public IAsyncEnumerable<int> Handle(StreamPipelineRequest request, CancellationToken cancellationToken)
        {
            WasCalled = true;

            return Execute(request.Count, cancellationToken);
        }

        /// <summary>
        /// Enumerates the handler response values.
        /// </summary>
        /// <param name="count">The number of values to emit.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of stream values.</returns>
        private async IAsyncEnumerable<int> Execute(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _events.Add("handler:start");

            for (var index = 1; index <= count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return index;
                await Task.Yield();
            }

            _events.Add("handler:end");
        }
    }

    /// <summary>
    /// Throws an exception during stream enumeration.
    /// </summary>
    private sealed class ThrowingStreamHandler : IStreamRequestHandler<StreamPipelineRequest, int>
    {
        /// <summary>
        /// Handles a stream request and returns a stream that throws.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence that throws during enumeration.</returns>
        public IAsyncEnumerable<int> Handle(StreamPipelineRequest request, CancellationToken cancellationToken)
        {

            return ThrowingStream(cancellationToken);
        }

        /// <summary>
        /// Produces a stream that throws an <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence that throws.</returns>
        private static async IAsyncEnumerable<int> ThrowingStream([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("stream failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    /// <summary>
    /// Records before and after events around stream pipeline delegation.
    /// </summary>
    private sealed class RecordingStreamBehavior : IStreamPipelineBehavior<StreamPipelineRequest, int>
    {
        private readonly List<string> _events;
        private readonly string _name;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingStreamBehavior"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        /// <param name="name">The behavior name.</param>
        public RecordingStreamBehavior(List<string> events, string name)
        {
            ArgumentNullException.ThrowIfNull(events);
            ArgumentNullException.ThrowIfNull(name);
            _events = events;
            _name = name;
        }

        /// <summary>
        /// Executes behavior logic around the next stream delegate.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next stream delegate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of stream values.</returns>
        public IAsyncEnumerable<int> Handle(
            StreamPipelineRequest request,
            StreamHandlerDelegate<int> next,
            CancellationToken cancellationToken)
        {
            _events.Add($"{_name}:before");

            return Wrap(next, cancellationToken);
        }

        /// <summary>
        /// Wraps the next stream delegate and records completion.
        /// </summary>
        /// <param name="next">The next stream delegate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of stream values.</returns>
        private async IAsyncEnumerable<int> Wrap(
            StreamHandlerDelegate<int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next().WithCancellation(cancellationToken))
            {
                yield return item;
            }

            _events.Add($"{_name}:after");
        }
    }

    /// <summary>
    /// Short-circuits stream processing and returns a fixed response stream.
    /// </summary>
    private sealed class ShortCircuitStreamBehavior : IStreamPipelineBehavior<StreamPipelineRequest, int>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="ShortCircuitStreamBehavior"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public ShortCircuitStreamBehavior(List<string> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            _events = events;
        }

        /// <summary>
        /// Returns a short-circuit stream without delegating to the next behavior.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next stream delegate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of short-circuit values.</returns>
        public IAsyncEnumerable<int> Handle(
            StreamPipelineRequest request,
            StreamHandlerDelegate<int> next,
            CancellationToken cancellationToken)
        {
            _events.Add("short-circuit");

            return ReturnValues(cancellationToken);
        }

        /// <summary>
        /// Returns short-circuit values.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of short-circuit values.</returns>
        private static async IAsyncEnumerable<int> ReturnValues([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return 42;
        }
    }

    /// <summary>
    /// Records stream request preprocessor execution.
    /// </summary>
    private sealed class RecordingStreamPreProcessor : IRequestPreProcessor<StreamPipelineRequest>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingStreamPreProcessor"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public RecordingStreamPreProcessor(List<string> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            _events = events;
        }

        /// <summary>
        /// Records preprocessor execution.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(StreamPipelineRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("pre");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles invalid operation stream exceptions with a replacement stream.
    /// </summary>
    private sealed class SpecificStreamExceptionHandler : IStreamRequestExceptionHandler<StreamPipelineRequest, int, InvalidOperationException>
    {
        /// <summary>
        /// Gets a value indicating whether this handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Handles an <see cref="InvalidOperationException"/> and provides replacement stream values.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="state">The mutable exception handling state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            StreamPipelineRequest request,
            InvalidOperationException exception,
            StreamRequestExceptionHandlerState<int> state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            state.SetHandled(ReturnFallback(cancellationToken));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns fallback stream values.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of fallback values.</returns>
        private static async IAsyncEnumerable<int> ReturnFallback([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return 7;
        }
    }

    /// <summary>
    /// Handles base exception stream errors.
    /// </summary>
    private sealed class FallbackStreamExceptionHandler : IStreamRequestExceptionHandler<StreamPipelineRequest, int, Exception>
    {
        /// <summary>
        /// Gets a value indicating whether this handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Handles a base <see cref="Exception"/> and provides fallback values.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="state">The mutable exception handling state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            StreamPipelineRequest request,
            Exception exception,
            StreamRequestExceptionHandlerState<int> state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            state.SetHandled(ReturnFallback(cancellationToken));

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns fallback stream values.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of fallback values.</returns>
        private static async IAsyncEnumerable<int> ReturnFallback([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return 8;
        }
    }

    /// <summary>
    /// Records request exception action execution for stream requests.
    /// </summary>
    private sealed class RecordingStreamExceptionAction : IRequestExceptionAction<StreamPipelineRequest, InvalidOperationException>
    {
        /// <summary>
        /// Gets a value indicating whether this action was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Executes action logic for an unhandled stream exception.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Execute(
            StreamPipelineRequest request,
            InvalidOperationException exception,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;

            return Task.CompletedTask;
        }
    }
}
