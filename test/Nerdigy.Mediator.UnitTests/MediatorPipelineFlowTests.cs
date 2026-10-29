using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies pipeline ordering and how <c>next(cancellationToken)</c> passes tokens through behaviors.
/// </summary>
public sealed class MediatorPipelineFlowTests
{
    /// <summary>
    /// Verifies every component runs in the documented order.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_RunsComponentsInDocumentedOrder()
    {
        List<string> events = [];
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FlowRequest, string>), new FlowHandler(events)),
            (typeof(IEnumerable<IRequestPreProcessor<FlowRequest>>), new IRequestPreProcessor<FlowRequest>[] { new FlowPreProcessor(events) }),
            (typeof(IEnumerable<IPipelineBehavior<FlowRequest, string>>),
                new IPipelineBehavior<FlowRequest, string>[] { new NamedBehavior("outer", events), new NamedBehavior("inner", events) }),
            (typeof(IEnumerable<IRequestPostProcessor<FlowRequest, string>>), new IRequestPostProcessor<FlowRequest, string>[] { new FlowPostProcessor(events) })));

        await mediator.Send(new FlowRequest(), CancellationToken.None);

        Assert.Equal(["pre", "outer:before", "inner:before", "handler", "post", "inner:after", "outer:after"], events);
    }

    /// <summary>
    /// Verifies a token passed to <c>next</c> reaches later behaviors and the handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenBehaviorPassesToken_HandlerReceivesIt()
    {
        using var callerCancellation = new CancellationTokenSource();
        using var behaviorCancellation = new CancellationTokenSource();
        var handler = new TokenCapturingHandler();
        var inner = new TokenCapturingBehavior();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FlowRequest, string>), handler),
            (typeof(IEnumerable<IPipelineBehavior<FlowRequest, string>>),
                new IPipelineBehavior<FlowRequest, string>[] { new TokenReplacingBehavior(behaviorCancellation.Token), inner })));

        await mediator.Send(new FlowRequest(), callerCancellation.Token);

        Assert.Equal(behaviorCancellation.Token, inner.Received);
        Assert.Equal(behaviorCancellation.Token, handler.Received);
    }

    /// <summary>
    /// Verifies <c>next()</c> without a token keeps the token the behavior received.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenBehaviorCallsNextWithoutToken_HandlerReceivesCallerToken()
    {
        using var callerCancellation = new CancellationTokenSource();
        var handler = new TokenCapturingHandler();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FlowRequest, string>), handler),
            (typeof(IEnumerable<IPipelineBehavior<FlowRequest, string>>),
                new IPipelineBehavior<FlowRequest, string>[] { new NamedBehavior("outer", []) })));

        await mediator.Send(new FlowRequest(), callerCancellation.Token);

        Assert.Equal(callerCancellation.Token, handler.Received);
    }

    /// <summary>
    /// Verifies a behavior can call <c>next</c> more than once, for example to retry.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenBehaviorCallsNextTwice_RunsRestOfPipelineTwice()
    {
        List<string> events = [];
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FlowRequest, string>), new FlowHandler(events)),
            (typeof(IEnumerable<IPipelineBehavior<FlowRequest, string>>),
                new IPipelineBehavior<FlowRequest, string>[] { new RetryBehavior(), new NamedBehavior("inner", events) })));

        await mediator.Send(new FlowRequest(), CancellationToken.None);

        Assert.Equal(["inner:before", "handler", "inner:after", "inner:before", "handler", "inner:after"], events);
    }

    /// <summary>
    /// Verifies a token passed to a stream behavior's <c>next</c> reaches the stream handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenBehaviorPassesToken_HandlerReceivesIt()
    {
        using var behaviorCancellation = new CancellationTokenSource();
        var handler = new TokenCapturingStreamHandler();
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IStreamRequestHandler<FlowStreamRequest, int>), handler),
            (typeof(IEnumerable<IStreamPipelineBehavior<FlowStreamRequest, int>>),
                new IStreamPipelineBehavior<FlowStreamRequest, int>[] { new TokenReplacingStreamBehavior(behaviorCancellation.Token) })));

        await foreach (var _ in mediator.CreateStream(new FlowStreamRequest(), CancellationToken.None))
        {
        }

        Assert.Equal(behaviorCancellation.Token, handler.Received);
    }

    /// <summary>
    /// Verifies a void request sent through <c>Send&lt;Unit&gt;</c> and through <c>Send(IRequest)</c> reaches the same handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_VoidRequestThroughBothOverloads_ReachesVoidHandler()
    {
        var handler = new FlowVoidHandler();
        var mediator = new MediatorRuntime(new TestServiceProvider((typeof(IRequestHandler<FlowVoidRequest>), handler)));

        await mediator.Send(new FlowVoidRequest(), CancellationToken.None);
        var unit = await mediator.Send<Unit>(new FlowVoidRequest(), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(Unit.Value, unit);
    }

    /// <summary>
    /// Verifies a void request with only an <c>IRequestHandler&lt;TRequest, Unit&gt;</c> gets an error that names the fix.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenVoidRequestOnlyHasUnitHandler_ExplainsWhichInterfaceToImplement()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FlowVoidRequest, Unit>), new FlowVoidUnitHandler())));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FlowVoidRequest(), CancellationToken.None));

        Assert.Contains("only dispatched to IRequestHandler<FlowVoidRequest>", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies many threads sending a request type for the first time at once all get the right response.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenFirstDispatchIsConcurrent_AllCallsSucceed()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider(
            (typeof(IRequestHandler<FirstDispatchRequest, int>), new FirstDispatchHandler())));
        using var start = new ManualResetEventSlim();

        var calls = Enumerable.Range(0, 32)
            .Select(value => Task.Run(() =>
            {
                start.Wait();

                return mediator.Send(new FirstDispatchRequest(value));
            }))
            .ToArray();
        start.Set();

        Assert.Equal(Enumerable.Range(1, 32), await Task.WhenAll(calls));
    }

    /// <summary>
    /// Verifies a request sent from inside a handler runs its own exception actions, and the outer request's actions
    /// run again when the failure propagates.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenNestedSendFails_RunsExceptionActionsAtBothLevels()
    {
        List<string> events = [];
        MediatorRuntime? mediator = null;
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<FlowRequest, string>), new NestingHandler(() => mediator!)),
            (typeof(IRequestHandler<FlowVoidRequest>), new FailingVoidHandler()),
            (typeof(IEnumerable<IRequestExceptionAction<FlowRequest, Exception>>),
                new IRequestExceptionAction<FlowRequest, Exception>[] { new RecordingAction<FlowRequest>("outer", events) }),
            (typeof(IEnumerable<IRequestExceptionAction<FlowVoidRequest, Exception>>),
                new IRequestExceptionAction<FlowVoidRequest, Exception>[] { new RecordingAction<FlowVoidRequest>("inner", events) }));
        mediator = new MediatorRuntime(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new FlowRequest()));

        Assert.Equal(["inner", "outer"], events);
    }

    /// <summary>
    /// Represents a request for flow tests.
    /// </summary>
    private sealed record FlowRequest : IRequest<string>;

    /// <summary>
    /// Represents a void request for flow tests.
    /// </summary>
    private sealed record FlowVoidRequest : IRequest;

    /// <summary>
    /// Represents a stream request for flow tests.
    /// </summary>
    private sealed record FlowStreamRequest : IStreamRequest<int>;

    /// <summary>
    /// Represents a request type that is only dispatched by the concurrent first-dispatch test.
    /// </summary>
    /// <param name="Value">The request value.</param>
    private sealed record FirstDispatchRequest(int Value) : IRequest<int>;

    /// <summary>
    /// Returns the request value plus one.
    /// </summary>
    private sealed class FirstDispatchHandler : IRequestHandler<FirstDispatchRequest, int>
    {
        /// <inheritdoc />
        public Task<int> Handle(FirstDispatchRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.Value + 1);
        }
    }

    /// <summary>
    /// Sends a void request that fails.
    /// </summary>
    /// <param name="mediator">Gets the mediator to send through.</param>
    private sealed class NestingHandler(Func<IMediator> mediator) : IRequestHandler<FlowRequest, string>
    {
        /// <inheritdoc />
        public async Task<string> Handle(FlowRequest request, CancellationToken cancellationToken)
        {
            await mediator().Send(new FlowVoidRequest(), cancellationToken);

            return "unreachable";
        }
    }

    /// <summary>
    /// Fails every void request.
    /// </summary>
    private sealed class FailingVoidHandler : IRequestHandler<FlowVoidRequest>
    {
        /// <inheritdoc />
        public Task Handle(FlowVoidRequest request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("inner failure");
        }
    }

    /// <summary>
    /// Records its name when it runs.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="name">The name to record.</param>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class RecordingAction<TRequest>(string name, List<string> events) : IRequestExceptionAction<TRequest, Exception>
        where TRequest : IBaseRequest
    {
        /// <inheritdoc />
        public Task Execute(TRequest request, Exception exception, CancellationToken cancellationToken)
        {
            events.Add(name);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records that it ran.
    /// </summary>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class FlowHandler(List<string> events) : IRequestHandler<FlowRequest, string>
    {
        /// <inheritdoc />
        public Task<string> Handle(FlowRequest request, CancellationToken cancellationToken)
        {
            events.Add("handler");

            return Task.FromResult("done");
        }
    }

    /// <summary>
    /// Counts calls to a void request.
    /// </summary>
    private sealed class FlowVoidHandler : IRequestHandler<FlowVoidRequest>
    {
        /// <summary>
        /// Gets the number of calls.
        /// </summary>
        public int Calls { get; private set; }

        /// <inheritdoc />
        public Task Handle(FlowVoidRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles a void request through the wrong interface.
    /// </summary>
    private sealed class FlowVoidUnitHandler : IRequestHandler<FlowVoidRequest, Unit>
    {
        /// <inheritdoc />
        public Task<Unit> Handle(FlowVoidRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Unit.Value);
        }
    }

    /// <summary>
    /// Captures the token it received.
    /// </summary>
    private sealed class TokenCapturingHandler : IRequestHandler<FlowRequest, string>
    {
        /// <summary>
        /// Gets the token the handler received.
        /// </summary>
        public CancellationToken Received { get; private set; }

        /// <inheritdoc />
        public Task<string> Handle(FlowRequest request, CancellationToken cancellationToken)
        {
            Received = cancellationToken;

            return Task.FromResult("done");
        }
    }

    /// <summary>
    /// Captures the token its stream received.
    /// </summary>
    private sealed class TokenCapturingStreamHandler : IStreamRequestHandler<FlowStreamRequest, int>
    {
        /// <summary>
        /// Gets the token the handler received.
        /// </summary>
        public CancellationToken Received { get; private set; }

        /// <inheritdoc />
        public async IAsyncEnumerable<int> Handle(FlowStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Received = cancellationToken;
            await Task.Yield();

            yield return 1;
        }
    }

    /// <summary>
    /// Records that it ran.
    /// </summary>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class FlowPreProcessor(List<string> events) : IRequestPreProcessor<FlowRequest>
    {
        /// <inheritdoc />
        public Task Process(FlowRequest request, CancellationToken cancellationToken)
        {
            events.Add("pre");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records that it ran.
    /// </summary>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class FlowPostProcessor(List<string> events) : IRequestPostProcessor<FlowRequest, string>
    {
        /// <inheritdoc />
        public Task Process(FlowRequest request, string response, CancellationToken cancellationToken)
        {
            events.Add("post");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records before and after calling <c>next</c>.
    /// </summary>
    /// <param name="name">The behavior name.</param>
    /// <param name="events">The event sink used by tests.</param>
    private sealed class NamedBehavior(string name, List<string> events) : IPipelineBehavior<FlowRequest, string>
    {
        /// <inheritdoc />
        public async Task<string> Handle(FlowRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            events.Add($"{name}:before");
            var response = await next();
            events.Add($"{name}:after");

            return response;
        }
    }

    /// <summary>
    /// Captures the token it received.
    /// </summary>
    private sealed class TokenCapturingBehavior : IPipelineBehavior<FlowRequest, string>
    {
        /// <summary>
        /// Gets the token the behavior received.
        /// </summary>
        public CancellationToken Received { get; private set; }

        /// <inheritdoc />
        public Task<string> Handle(FlowRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            Received = cancellationToken;

            return next();
        }
    }

    /// <summary>
    /// Passes its own token to the rest of the pipeline.
    /// </summary>
    /// <param name="token">The token to pass on.</param>
    private sealed class TokenReplacingBehavior(CancellationToken token) : IPipelineBehavior<FlowRequest, string>
    {
        /// <inheritdoc />
        public Task<string> Handle(FlowRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            return next(token);
        }
    }

    /// <summary>
    /// Passes its own token to the stream handler.
    /// </summary>
    /// <param name="token">The token to pass on.</param>
    private sealed class TokenReplacingStreamBehavior(CancellationToken token) : IStreamPipelineBehavior<FlowStreamRequest, int>
    {
        /// <inheritdoc />
        public IAsyncEnumerable<int> Handle(FlowStreamRequest request, StreamHandlerDelegate<int> next, CancellationToken cancellationToken)
        {
            return next(token);
        }
    }

    /// <summary>
    /// Calls <c>next</c> twice.
    /// </summary>
    private sealed class RetryBehavior : IPipelineBehavior<FlowRequest, string>
    {
        /// <inheritdoc />
        public async Task<string> Handle(FlowRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            _ = await next();

            return await next();
        }
    }
}