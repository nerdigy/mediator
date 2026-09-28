using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies dispatch of void-style requests sent through their <c>IRequest&lt;Unit&gt;</c> base contract.
/// </summary>
public sealed class MediatorVoidRequestTests
{
    /// <summary>
    /// Gets the ways a void-style request can be sent.
    /// </summary>
    public static TheoryData<string> SendModes => ["void", "unit-view", "explicit-unit", "generic-forward"];

    /// <summary>
    /// Verifies that a void-style request sent as <c>IRequest&lt;Unit&gt;</c> invokes its void handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenVoidRequestSentThroughUnitView_InvokesVoidHandler()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<VoidCommand>), new VoidCommandHandler(events)));
        var mediator = new MediatorRuntime(provider);
        IRequest<Unit> command = new VoidCommand("alpha");

        var response = await mediator.Send(command, CancellationToken.None);

        Assert.Equal(Unit.Value, response);
        Assert.Equal(["handler:alpha"], events);
    }

    /// <summary>
    /// Verifies that a void-style request sent with an explicit <see cref="Unit"/> response type invokes its void handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenVoidRequestSentWithExplicitUnit_InvokesVoidHandler()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<VoidCommand>), new VoidCommandHandler(events)));
        var mediator = new MediatorRuntime(provider);

        var response = await mediator.Send<Unit>(new VoidCommand("alpha"), CancellationToken.None);

        Assert.Equal(Unit.Value, response);
        Assert.Equal(["handler:alpha"], events);
    }

    /// <summary>
    /// Verifies that a generic forwarding helper can send a void-style request to its void handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenVoidRequestForwardedThroughGenericHelper_InvokesVoidHandler()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<VoidCommand>), new VoidCommandHandler(events)));
        var mediator = new MediatorRuntime(provider);

        var response = await Forward(mediator, new VoidCommand("alpha"));

        Assert.Equal(Unit.Value, response);
        Assert.Equal(["handler:alpha"], events);
    }

    /// <summary>
    /// Verifies that every way of sending a void-style request runs its pipeline exactly once.
    /// </summary>
    /// <param name="mode">The send mode under test.</param>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Theory]
    [MemberData(nameof(SendModes))]
    public async Task Send_WhenVoidRequestSent_RunsPipelineOnce(string mode)
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<VoidCommand>), new VoidCommandHandler(events)),
            (typeof(IEnumerable<IRequestPreProcessor<VoidCommand>>), new IRequestPreProcessor<VoidCommand>[] { new VoidCommandPreProcessor(events) }),
            (typeof(IEnumerable<IPipelineBehavior<VoidCommand, Unit>>), new IPipelineBehavior<VoidCommand, Unit>[] { new VoidCommandBehavior(events) }),
            (typeof(IEnumerable<IRequestPostProcessor<VoidCommand, Unit>>), new IRequestPostProcessor<VoidCommand, Unit>[] { new VoidCommandPostProcessor(events) }));
        var mediator = new MediatorRuntime(provider);

        await SendWithMode(mediator, new VoidCommand("alpha"), mode);

        Assert.Equal(["pre", "behavior:before", "handler:alpha", "post", "behavior:after"], events);
    }

    /// <summary>
    /// Verifies that a void-style request sent through its <c>IRequest&lt;Unit&gt;</c> view uses its Unit exception handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenVoidRequestSentThroughUnitViewAndHandlerThrows_UsesUnitExceptionHandler()
    {
        List<string> events = [];
        var exceptionHandler = new VoidCommandExceptionHandler();
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<VoidCommand>), new VoidCommandHandler(events)),
            (typeof(IEnumerable<IRequestExceptionHandler<VoidCommand, Unit, InvalidOperationException>>), new IRequestExceptionHandler<VoidCommand, Unit, InvalidOperationException>[] { exceptionHandler }));
        var mediator = new MediatorRuntime(provider);
        IRequest<Unit> command = new VoidCommand("alpha", Throw: true);

        var response = await mediator.Send(command, CancellationToken.None);

        Assert.Equal(Unit.Value, response);
        Assert.Equal(["handler:alpha"], events);
        Assert.Equal("command failed", exceptionHandler.HandledMessage);
    }

    /// <summary>
    /// Verifies that a void-style request sent through its <c>IRequest&lt;Unit&gt;</c> view forwards the cancellation token.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenVoidRequestSentThroughUnitView_PassesCancellationTokenToHandler()
    {
        var handler = new VoidCommandHandler([]);
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<VoidCommand>), handler));
        var mediator = new MediatorRuntime(provider);
        using var cancellation = new CancellationTokenSource();
        IRequest<Unit> command = new VoidCommand("alpha");

        _ = await mediator.Send(command, cancellation.Token);

        Assert.Equal(cancellation.Token, handler.ReceivedToken);
    }

    /// <summary>
    /// Verifies that a void-style request sent through its <c>IRequest&lt;Unit&gt;</c> view without a handler reports the
    /// void handler registration.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenVoidRequestSentThroughUnitViewWithoutHandler_ReportsVoidHandlerRegistration()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());
        IRequest<Unit> command = new VoidCommand("alpha");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(command, CancellationToken.None));

        Assert.Contains("IRequestHandler<VoidCommand>", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a request implementing only <c>IRequest&lt;Unit&gt;</c> still uses its response-bearing handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenRequestDeclaresOnlyUnitResponse_UsesResponseHandler()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<UnitQuery, Unit>), new UnitQueryHandler(events)),
            (typeof(IEnumerable<IPipelineBehavior<UnitQuery, Unit>>), new IPipelineBehavior<UnitQuery, Unit>[] { new UnitQueryBehavior(events) }));
        var mediator = new MediatorRuntime(provider);

        var response = await mediator.Send(new UnitQuery(), CancellationToken.None);

        Assert.Equal(Unit.Value, response);
        Assert.Equal(["behavior:before", "handler", "behavior:after"], events);
    }

    /// <summary>
    /// Sends a void-style request using the requested send mode.
    /// </summary>
    /// <param name="mediator">The mediator to send through.</param>
    /// <param name="command">The command to send.</param>
    /// <param name="mode">The send mode.</param>
    /// <returns>A task that completes when the send finishes.</returns>
    private static async Task SendWithMode(MediatorRuntime mediator, VoidCommand command, string mode)
    {
        switch (mode)
        {
            case "void":
                await mediator.Send(command, CancellationToken.None);
                break;
            case "unit-view":
                IRequest<Unit> view = command;
                _ = await mediator.Send(view, CancellationToken.None);
                break;
            case "explicit-unit":
                _ = await mediator.Send<Unit>(command, CancellationToken.None);
                break;
            case "generic-forward":
                _ = await Forward(mediator, command);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown send mode.");
        }
    }

    /// <summary>
    /// Forwards a request through the generic send overload, as a generic wrapper would.
    /// </summary>
    /// <typeparam name="TResponse">The response payload type.</typeparam>
    /// <param name="sender">The sender to forward through.</param>
    /// <param name="request">The request to forward.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    private static Task<TResponse> Forward<TResponse>(ISender sender, IRequest<TResponse> request)
    {
        return sender.Send(request, CancellationToken.None);
    }

    /// <summary>
    /// Represents a void-style command.
    /// </summary>
    /// <param name="Value">The command payload.</param>
    /// <param name="Throw">Whether the handler should throw.</param>
    private sealed record VoidCommand(string Value, bool Throw = false) : IRequest;

    /// <summary>
    /// Handles <see cref="VoidCommand"/> requests.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class VoidCommandHandler(List<string> events) : IRequestHandler<VoidCommand>
    {
        /// <summary>
        /// Gets the cancellation token received by the handler.
        /// </summary>
        public CancellationToken ReceivedToken { get; private set; }

        /// <summary>
        /// Handles the request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(VoidCommand request, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            events.Add($"handler:{request.Value}");

            if (request.Throw)
            {
                throw new InvalidOperationException("command failed");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records preprocessing for <see cref="VoidCommand"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class VoidCommandPreProcessor(List<string> events) : IRequestPreProcessor<VoidCommand>
    {
        /// <summary>
        /// Records the preprocessing event.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(VoidCommand request, CancellationToken cancellationToken)
        {
            events.Add("pre");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records behavior execution for <see cref="VoidCommand"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class VoidCommandBehavior(List<string> events) : IPipelineBehavior<VoidCommand, Unit>
    {
        /// <summary>
        /// Records events around the next delegate.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next delegate in the pipeline.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public async Task<Unit> Handle(VoidCommand request, RequestHandlerDelegate<Unit> next, CancellationToken cancellationToken)
        {
            events.Add("behavior:before");
            var response = await next();
            events.Add("behavior:after");

            return response;
        }
    }

    /// <summary>
    /// Records postprocessing for <see cref="VoidCommand"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class VoidCommandPostProcessor(List<string> events) : IRequestPostProcessor<VoidCommand, Unit>
    {
        /// <summary>
        /// Records the postprocessing event.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="response">The handler response.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(VoidCommand request, Unit response, CancellationToken cancellationToken)
        {
            events.Add("post");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Recovers from <see cref="VoidCommand"/> failures.
    /// </summary>
    private sealed class VoidCommandExceptionHandler : IRequestExceptionHandler<VoidCommand, Unit, InvalidOperationException>
    {
        /// <summary>
        /// Gets the message of the handled exception, or <see langword="null"/> when none was handled.
        /// </summary>
        public string? HandledMessage { get; private set; }

        /// <summary>
        /// Marks the exception as handled.
        /// </summary>
        /// <param name="request">The failing request.</param>
        /// <param name="exception">The exception thrown by the handler.</param>
        /// <param name="state">The exception handling state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            VoidCommand request,
            InvalidOperationException exception,
            RequestExceptionHandlerState<Unit> state,
            CancellationToken cancellationToken)
        {
            HandledMessage = exception.Message;
            state.SetHandled(Unit.Value);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents a response-bearing request whose response type is <see cref="Unit"/>.
    /// </summary>
    private sealed record UnitQuery : IRequest<Unit>;

    /// <summary>
    /// Handles <see cref="UnitQuery"/> requests.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class UnitQueryHandler(List<string> events) : IRequestHandler<UnitQuery, Unit>
    {
        /// <summary>
        /// Handles the request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to <see cref="Unit.Value"/>.</returns>
        public Task<Unit> Handle(UnitQuery request, CancellationToken cancellationToken)
        {
            events.Add("handler");

            return Task.FromResult(Unit.Value);
        }
    }

    /// <summary>
    /// Records behavior execution for <see cref="UnitQuery"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class UnitQueryBehavior(List<string> events) : IPipelineBehavior<UnitQuery, Unit>
    {
        /// <summary>
        /// Records events around the next delegate.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next delegate in the pipeline.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public async Task<Unit> Handle(UnitQuery request, RequestHandlerDelegate<Unit> next, CancellationToken cancellationToken)
        {
            events.Add("behavior:before");
            var response = await next();
            events.Add("behavior:after");

            return response;
        }
    }
}