using Nerdigy.Mediator.Abstractions;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies pipeline, processor, and exception behavior for send operations.
/// </summary>
public sealed class MediatorPipelineTests
{
    /// <summary>
    /// Verifies pipeline behaviors execute in registration order around handler invocation.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WithPipelineBehaviors_ExecutesInRegistrationOrder()
    {
        List<string> events = [];
        var handler = new PipelineRequestHandler(events);
        IPipelineBehavior<PipelineRequest, string>[] behaviors =
        [
            new RecordingBehavior(events, "behavior-1"),
            new RecordingBehavior(events, "behavior-2")
        ];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<PipelineRequest, string>), handler),
            (typeof(IEnumerable<IPipelineBehavior<PipelineRequest, string>>), behaviors));
        var mediator = new MediatorRuntime(provider);

        var response = await mediator.Send(new PipelineRequest("start"), CancellationToken.None);

        Assert.Equal("response", response);
        Assert.Equal(
            ["behavior-1:before", "behavior-2:before", "handler", "behavior-2:after", "behavior-1:after"],
            events);
    }

    /// <summary>
    /// Verifies preprocessors and postprocessors execute in registration order around handler invocation.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WithProcessors_ExecutesPreAndPostInRegistrationOrder()
    {
        List<string> events = [];
        var handler = new PipelineRequestHandler(events);
        IRequestPreProcessor<PipelineRequest>[] preprocessors =
        [
            new RecordingPreProcessor(events, "pre-1"),
            new RecordingPreProcessor(events, "pre-2")
        ];
        IRequestPostProcessor<PipelineRequest, string>[] postprocessors =
        [
            new RecordingPostProcessor(events, "post-1"),
            new RecordingPostProcessor(events, "post-2")
        ];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<PipelineRequest, string>), handler),
            (typeof(IEnumerable<IRequestPreProcessor<PipelineRequest>>), preprocessors),
            (typeof(IEnumerable<IRequestPostProcessor<PipelineRequest, string>>), postprocessors));
        var mediator = new MediatorRuntime(provider);

        var response = await mediator.Send(new PipelineRequest("start"), CancellationToken.None);

        Assert.Equal("response", response);
        Assert.Equal(["pre-1", "pre-2", "handler", "post-1:response", "post-2:response"], events);
    }

    /// <summary>
    /// Verifies behaviors can short-circuit and skip handler and postprocessors.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenBehaviorShortCircuits_SkipsHandlerAndPostProcessors()
    {
        List<string> events = [];
        var handler = new PipelineRequestHandler(events);
        IPipelineBehavior<PipelineRequest, string>[] behaviors = [new ShortCircuitBehavior(events)];
        IRequestPostProcessor<PipelineRequest, string>[] postprocessors = [new RecordingPostProcessor(events, "post")];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<PipelineRequest, string>), handler),
            (typeof(IEnumerable<IPipelineBehavior<PipelineRequest, string>>), behaviors),
            (typeof(IEnumerable<IRequestPostProcessor<PipelineRequest, string>>), postprocessors));
        var mediator = new MediatorRuntime(provider);

        var response = await mediator.Send(new PipelineRequest("start"), CancellationToken.None);

        Assert.Equal("short-circuit", response);
        Assert.Equal(["short-circuit"], events);
        Assert.False(handler.WasCalled);
    }

    /// <summary>
    /// Verifies the most specific exception handler handles the thrown exception first.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenExceptionIsHandled_UsesMostSpecificHandler()
    {
        var handler = new ThrowingRequestHandler();
        var specific = new SpecificExceptionHandler();
        var fallback = new FallbackExceptionHandler();
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<PipelineRequest, string>), handler),
            (typeof(IEnumerable<IRequestExceptionHandler<PipelineRequest, string, InvalidOperationException>>), new IRequestExceptionHandler<PipelineRequest, string, InvalidOperationException>[] { specific }),
            (typeof(IEnumerable<IRequestExceptionHandler<PipelineRequest, string, Exception>>), new IRequestExceptionHandler<PipelineRequest, string, Exception>[] { fallback }));
        var mediator = new MediatorRuntime(provider);

        var response = await mediator.Send(new PipelineRequest("start"), CancellationToken.None);

        Assert.Equal("specific", response);
        Assert.True(specific.WasCalled);
        Assert.False(fallback.WasCalled);
    }

    /// <summary>
    /// Verifies exception actions run when no exception handler handles the exception.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenExceptionIsUnhandled_ExecutesActionsAndRethrows()
    {
        var handler = new ThrowingRequestHandler();
        var action = new RecordingExceptionAction();
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<PipelineRequest, string>), handler),
            (typeof(IEnumerable<IRequestExceptionAction<PipelineRequest, InvalidOperationException>>), new IRequestExceptionAction<PipelineRequest, InvalidOperationException>[] { action }));
        var mediator = new MediatorRuntime(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new PipelineRequest("start"), CancellationToken.None));
        Assert.True(action.WasCalled);
    }

    /// <summary>
    /// Verifies void requests also execute pipeline behaviors and preprocessors.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task SendVoid_ExecutesBehaviorsAndPreProcessors()
    {
        List<string> events = [];
        var handler = new VoidPipelineRequestHandler(events);
        IRequestPreProcessor<VoidPipelineRequest>[] preprocessors = [new VoidRecordingPreProcessor(events)];
        IPipelineBehavior<VoidPipelineRequest, Unit>[] behaviors = [new VoidRecordingBehavior(events)];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<VoidPipelineRequest>), handler),
            (typeof(IEnumerable<IRequestPreProcessor<VoidPipelineRequest>>), preprocessors),
            (typeof(IEnumerable<IPipelineBehavior<VoidPipelineRequest, Unit>>), behaviors));
        var mediator = new MediatorRuntime(provider);

        await mediator.Send((IRequest)new VoidPipelineRequest("delete"), CancellationToken.None);

        Assert.Equal(["void-pre", "void-behavior:before", "void-handler", "void-behavior:after"], events);
    }

    /// <summary>
    /// Represents a sample request for pipeline tests.
    /// </summary>
    /// <param name="Value">The request payload.</param>
    private sealed record PipelineRequest(string Value) : IRequest<string>;

    /// <summary>
    /// Handles <see cref="PipelineRequest"/> requests.
    /// </summary>
    private sealed class PipelineRequestHandler : IRequestHandler<PipelineRequest, string>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="PipelineRequestHandler"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public PipelineRequestHandler(List<string> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            _events = events;
        }

        /// <summary>
        /// Gets a value indicating whether the handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Handles the request and returns a fixed response.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<string> Handle(PipelineRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            _events.Add("handler");

            return Task.FromResult("response");
        }
    }

    /// <summary>
    /// Throws an exception for every request.
    /// </summary>
    private sealed class ThrowingRequestHandler : IRequestHandler<PipelineRequest, string>
    {
        /// <summary>
        /// Throws an <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that never completes successfully.</returns>
        public Task<string> Handle(PipelineRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("failure");
        }
    }

    /// <summary>
    /// Records before/after events around handler execution.
    /// </summary>
    private sealed class RecordingBehavior : IPipelineBehavior<PipelineRequest, string>
    {
        private readonly List<string> _events;
        private readonly string _name;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingBehavior"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        /// <param name="name">The behavior name.</param>
        public RecordingBehavior(List<string> events, string name)
        {
            ArgumentNullException.ThrowIfNull(events);
            ArgumentNullException.ThrowIfNull(name);
            _events = events;
            _name = name;
        }

        /// <summary>
        /// Executes behavior logic around the next pipeline delegate.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next pipeline delegate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public async Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"{_name}:before");
            var response = await next().ConfigureAwait(false);
            _events.Add($"{_name}:after");

            return response;
        }
    }

    /// <summary>
    /// Returns a fixed response without calling the next pipeline delegate.
    /// </summary>
    private sealed class ShortCircuitBehavior : IPipelineBehavior<PipelineRequest, string>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="ShortCircuitBehavior"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public ShortCircuitBehavior(List<string> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            _events = events;
        }

        /// <summary>
        /// Short-circuits request processing.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next pipeline delegate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the short-circuit response.</returns>
        public Task<string> Handle(PipelineRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("short-circuit");

            return Task.FromResult("short-circuit");
        }
    }

    /// <summary>
    /// Records execution in the preprocessor stage.
    /// </summary>
    private sealed class RecordingPreProcessor : IRequestPreProcessor<PipelineRequest>
    {
        private readonly List<string> _events;
        private readonly string _name;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingPreProcessor"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        /// <param name="name">The processor name.</param>
        public RecordingPreProcessor(List<string> events, string name)
        {
            ArgumentNullException.ThrowIfNull(events);
            ArgumentNullException.ThrowIfNull(name);
            _events = events;
            _name = name;
        }

        /// <summary>
        /// Records preprocessor execution.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(PipelineRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add(_name);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records execution in the postprocessor stage.
    /// </summary>
    private sealed class RecordingPostProcessor : IRequestPostProcessor<PipelineRequest, string>
    {
        private readonly List<string> _events;
        private readonly string _name;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingPostProcessor"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        /// <param name="name">The processor name.</param>
        public RecordingPostProcessor(List<string> events, string name)
        {
            ArgumentNullException.ThrowIfNull(events);
            ArgumentNullException.ThrowIfNull(name);
            _events = events;
            _name = name;
        }

        /// <summary>
        /// Records postprocessor execution.
        /// </summary>
        /// <param name="request">The handled request.</param>
        /// <param name="response">The handler response.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(PipelineRequest request, string response, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"{_name}:{response}");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles an <see cref="InvalidOperationException"/> and returns a fallback response.
    /// </summary>
    private sealed class SpecificExceptionHandler : IRequestExceptionHandler<PipelineRequest, string, InvalidOperationException>
    {
        /// <summary>
        /// Gets a value indicating whether this handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Handles an <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="state">The mutable handler state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            PipelineRequest request,
            InvalidOperationException exception,
            RequestExceptionHandlerState<string> state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            state.SetHandled("specific");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Handles base <see cref="Exception"/> errors.
    /// </summary>
    private sealed class FallbackExceptionHandler : IRequestExceptionHandler<PipelineRequest, string, Exception>
    {
        /// <summary>
        /// Gets a value indicating whether this handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Handles a base <see cref="Exception"/>.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="state">The mutable handler state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            PipelineRequest request,
            Exception exception,
            RequestExceptionHandlerState<string> state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            state.SetHandled("fallback");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records exception action execution.
    /// </summary>
    private sealed class RecordingExceptionAction : IRequestExceptionAction<PipelineRequest, InvalidOperationException>
    {
        /// <summary>
        /// Gets a value indicating whether this action was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Executes action logic for an <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Execute(PipelineRequest request, InvalidOperationException exception, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents a sample void request for pipeline tests.
    /// </summary>
    /// <param name="CommandName">The command name.</param>
    private sealed record VoidPipelineRequest(string CommandName) : IRequest;

    /// <summary>
    /// Handles <see cref="VoidPipelineRequest"/> requests.
    /// </summary>
    private sealed class VoidPipelineRequestHandler : IRequestHandler<VoidPipelineRequest>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="VoidPipelineRequestHandler"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public VoidPipelineRequestHandler(List<string> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            _events = events;
        }

        /// <summary>
        /// Handles the void request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(VoidPipelineRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("void-handler");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records execution in the void-request preprocessor stage.
    /// </summary>
    private sealed class VoidRecordingPreProcessor : IRequestPreProcessor<VoidPipelineRequest>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="VoidRecordingPreProcessor"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public VoidRecordingPreProcessor(List<string> events)
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
        public Task Process(VoidPipelineRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("void-pre");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records execution in a void-request behavior.
    /// </summary>
    private sealed class VoidRecordingBehavior : IPipelineBehavior<VoidPipelineRequest, Unit>
    {
        private readonly List<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="VoidRecordingBehavior"/> class.
        /// </summary>
        /// <param name="events">The event sink used by tests.</param>
        public VoidRecordingBehavior(List<string> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            _events = events;
        }

        /// <summary>
        /// Executes behavior logic around the next pipeline delegate.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next pipeline delegate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to <see cref="Unit.Value"/>.</returns>
        public async Task<Unit> Handle(VoidPipelineRequest request, RequestHandlerDelegate<Unit> next, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("void-behavior:before");
            var response = await next().ConfigureAwait(false);
            _events.Add("void-behavior:after");

            return response;
        }
    }
}