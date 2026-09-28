using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

using MediatorRuntime = Nerdigy.Mediator.Mediator;

namespace Nerdigy.Mediator.UnitTests;

/// <summary>
/// Verifies dispatch of requests and stream requests sent through a covariant view of their declared response type.
/// </summary>
public sealed class MediatorCovariantResponseTests
{
    /// <summary>
    /// Verifies that a request sent as <c>IRequest&lt;object&gt;</c> runs its declared string handler on every call.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenSentThroughObjectView_UsesDeclaredHandlerOnRepeatedCalls()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<LookupQuery, string>), new LookupQueryHandler(events)));
        var mediator = new MediatorRuntime(provider);
        IRequest<object> first = new LookupQuery("first");
        IRequest<object> second = new LookupQuery("second");

        var firstResponse = await mediator.Send(first, CancellationToken.None);
        var secondResponse = await mediator.Send(second, CancellationToken.None);

        Assert.Equal("found:first", firstResponse);
        Assert.Equal("found:second", secondResponse);
        Assert.Equal(["handler:first", "handler:second"], events);
    }

    /// <summary>
    /// Verifies that exact-response sends keep working alongside covariant sends of the same request type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenMixingExactAndCovariantViews_BothUseDeclaredHandler()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<LookupQuery, string>), new LookupQueryHandler(events)));
        var mediator = new MediatorRuntime(provider);
        IRequest<object> covariant = new LookupQuery("covariant");

        var covariantResponse = await mediator.Send(covariant, CancellationToken.None);
        var exactResponse = await mediator.Send(new LookupQuery("exact"), CancellationToken.None);

        Assert.Equal("found:covariant", covariantResponse);
        Assert.Equal("found:exact", exactResponse);
    }

    /// <summary>
    /// Verifies that a request declaring a derived response type can be sent as its base response type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenSentThroughBaseResponseView_ReturnsDerivedResponse()
    {
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<DogQuery, Dog>), new DogQueryHandler()));
        var mediator = new MediatorRuntime(provider);
        IRequest<Animal> query = new DogQuery("Rex");

        var response = await mediator.Send(query, CancellationToken.None);

        var dog = Assert.IsType<Dog>(response);
        Assert.Equal("Rex", dog.Name);
    }

    /// <summary>
    /// Verifies that a covariant send runs the processors and behaviors registered for the declared response type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenSentThroughObjectView_RunsDeclaredResponsePipeline()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<LookupQuery, string>), new LookupQueryHandler(events)),
            (typeof(IEnumerable<IRequestPreProcessor<LookupQuery>>), new IRequestPreProcessor<LookupQuery>[] { new LookupPreProcessor(events) }),
            (typeof(IEnumerable<IPipelineBehavior<LookupQuery, string>>), new IPipelineBehavior<LookupQuery, string>[] { new LookupBehavior(events) }),
            (typeof(IEnumerable<IRequestPostProcessor<LookupQuery, string>>), new IRequestPostProcessor<LookupQuery, string>[] { new LookupPostProcessor(events) }));
        var mediator = new MediatorRuntime(provider);
        IRequest<object> query = new LookupQuery("alpha");

        var response = await mediator.Send(query, CancellationToken.None);

        Assert.Equal("found:alpha", response);
        Assert.Equal(["pre", "behavior:before", "handler:alpha", "post:found:alpha", "behavior:after"], events);
    }

    /// <summary>
    /// Verifies that a covariant send runs the exception handlers registered for the declared response type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenSentThroughObjectViewAndHandlerThrows_UsesDeclaredExceptionHandler()
    {
        List<string> events = [];
        var exceptionHandler = new LookupExceptionHandler();
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<LookupQuery, string>), new LookupQueryHandler(events)),
            (typeof(IEnumerable<IRequestExceptionHandler<LookupQuery, string, InvalidOperationException>>), new IRequestExceptionHandler<LookupQuery, string, InvalidOperationException>[] { exceptionHandler }));
        var mediator = new MediatorRuntime(provider);
        IRequest<object> query = new LookupQuery("alpha", Throw: true);

        var response = await mediator.Send(query, CancellationToken.None);

        Assert.Equal("recovered", response);
        Assert.True(exceptionHandler.WasCalled);
    }

    /// <summary>
    /// Verifies that a covariant send forwards the caller's cancellation token to the declared handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenSentThroughObjectView_PassesCancellationTokenToHandler()
    {
        var handler = new LookupQueryHandler([]);
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<LookupQuery, string>), handler));
        var mediator = new MediatorRuntime(provider);
        using var cancellation = new CancellationTokenSource();
        IRequest<object> query = new LookupQuery("alpha");

        _ = await mediator.Send(query, cancellation.Token);

        Assert.Equal(cancellation.Token, handler.ReceivedToken);
    }

    /// <summary>
    /// Verifies that a covariant send without a declared handler reports the declared response type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenSentThroughObjectViewWithoutHandler_ReportsDeclaredResponseType()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());
        IRequest<object> query = new LookupQuery("alpha");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(query, CancellationToken.None));

        Assert.Contains("IRequestHandler<LookupQuery, String>", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a request declaring several response types compatible with the requested view is rejected.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Send_WhenSeveralDeclaredResponsesMatchView_ThrowsInvalidOperationException()
    {
        var provider = new TestServiceProvider(
            (typeof(IRequestHandler<AmbiguousQuery, string>), new AmbiguousQueryHandler()));
        var mediator = new MediatorRuntime(provider);
        IRequest<object> query = new AmbiguousQuery();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(query, CancellationToken.None));
        var exactResponse = await mediator.Send<string>(new AmbiguousQuery(), CancellationToken.None);

        Assert.Contains("more than one compatible response type", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'System.String'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'System.Uri'", exception.Message, StringComparison.Ordinal);
        Assert.Equal("ambiguous", exactResponse);
    }

    /// <summary>
    /// Verifies that a stream request created as <c>IStreamRequest&lt;object&gt;</c> runs its declared string handler on every call.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenCreatedThroughObjectView_UsesDeclaredHandlerOnRepeatedCalls()
    {
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<LookupStream, string>), new LookupStreamHandler()));
        var mediator = new MediatorRuntime(provider);
        IStreamRequest<object> first = new LookupStream(2);
        IStreamRequest<object> second = new LookupStream(1);

        var firstValues = await ToListAsync(mediator.CreateStream(first, CancellationToken.None));
        var secondValues = await ToListAsync(mediator.CreateStream(second, CancellationToken.None));
        var exactValues = await ToListAsync(mediator.CreateStream(new LookupStream(1), CancellationToken.None));

        Assert.Equal(["item-1", "item-2"], firstValues);
        Assert.Equal(["item-1"], secondValues);
        Assert.Equal(["item-1"], exactValues);
    }

    /// <summary>
    /// Verifies that a stream request declaring a derived response type can be created as its base response type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenCreatedThroughBaseResponseView_ReturnsDerivedResponses()
    {
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<DogStream, Dog>), new DogStreamHandler()));
        var mediator = new MediatorRuntime(provider);
        IStreamRequest<Animal> request = new DogStream();

        var values = await ToListAsync(mediator.CreateStream(request, CancellationToken.None));

        Assert.Equal(["Rex", "Fido"], values.Select(animal => Assert.IsType<Dog>(animal).Name));
    }

    /// <summary>
    /// Verifies that a covariant stream runs the behaviors registered for the declared response type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenCreatedThroughObjectView_RunsDeclaredResponsePipeline()
    {
        List<string> events = [];
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<LookupStream, string>), new LookupStreamHandler()),
            (typeof(IEnumerable<IStreamPipelineBehavior<LookupStream, string>>), new IStreamPipelineBehavior<LookupStream, string>[] { new LookupStreamBehavior(events) }));
        var mediator = new MediatorRuntime(provider);
        IStreamRequest<object> request = new LookupStream(2);

        var values = await ToListAsync(mediator.CreateStream(request, CancellationToken.None));

        Assert.Equal(["item-1", "item-2"], values);
        Assert.Equal(["behavior:item-1", "behavior:item-2"], events);
    }

    /// <summary>
    /// Verifies that a covariant stream runs the exception handlers registered for the declared response type.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenCreatedThroughObjectViewAndHandlerThrows_UsesDeclaredExceptionHandler()
    {
        var exceptionHandler = new LookupStreamExceptionHandler();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<LookupStream, string>), new LookupStreamHandler()),
            (typeof(IEnumerable<IStreamRequestExceptionHandler<LookupStream, string, InvalidOperationException>>), new IStreamRequestExceptionHandler<LookupStream, string, InvalidOperationException>[] { exceptionHandler }));
        var mediator = new MediatorRuntime(provider);
        IStreamRequest<object> request = new LookupStream(2, Throw: true);

        var values = await ToListAsync(mediator.CreateStream(request, CancellationToken.None));

        Assert.Equal(["recovered"], values);
        Assert.True(exceptionHandler.WasCalled);
    }

    /// <summary>
    /// Verifies that a covariant stream forwards the caller's cancellation token to the declared handler.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task CreateStream_WhenCreatedThroughObjectView_PassesCancellationTokenToHandler()
    {
        var handler = new LookupStreamHandler();
        var provider = new TestServiceProvider(
            (typeof(IStreamRequestHandler<LookupStream, string>), handler));
        var mediator = new MediatorRuntime(provider);
        using var cancellation = new CancellationTokenSource();
        IStreamRequest<object> request = new LookupStream(1);

        _ = await ToListAsync(mediator.CreateStream(request, cancellation.Token));

        Assert.Equal(cancellation.Token, handler.ReceivedToken);
    }

    /// <summary>
    /// Verifies that a stream request declaring several response types compatible with the requested view is rejected.
    /// </summary>
    [Fact]
    public void CreateStream_WhenSeveralDeclaredResponsesMatchView_ThrowsInvalidOperationException()
    {
        var mediator = new MediatorRuntime(new TestServiceProvider());
        IStreamRequest<object> request = new AmbiguousStream();

        var exception = Assert.Throws<InvalidOperationException>(
            () => mediator.CreateStream(request, CancellationToken.None));

        Assert.Contains("more than one compatible response type", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enumerates an asynchronous sequence and returns its values as a list.
    /// </summary>
    /// <typeparam name="T">The sequence element type.</typeparam>
    /// <param name="source">The sequence to enumerate.</param>
    /// <returns>A task that resolves to the sequence values.</returns>
    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> results = [];

        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    /// <summary>
    /// Represents a base response type.
    /// </summary>
    private record Animal;

    /// <summary>
    /// Represents a response type derived from <see cref="Animal"/>.
    /// </summary>
    /// <param name="Name">The dog name.</param>
    private sealed record Dog(string Name) : Animal;

    /// <summary>
    /// Represents a request that declares a string response.
    /// </summary>
    /// <param name="Value">The lookup value.</param>
    /// <param name="Throw">Whether the handler should throw.</param>
    private sealed record LookupQuery(string Value, bool Throw = false) : IRequest<string>;

    /// <summary>
    /// Handles <see cref="LookupQuery"/> requests.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class LookupQueryHandler(List<string> events) : IRequestHandler<LookupQuery, string>
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
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<string> Handle(LookupQuery request, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            events.Add($"handler:{request.Value}");

            if (request.Throw)
            {
                throw new InvalidOperationException("lookup failed");
            }

            return Task.FromResult($"found:{request.Value}");
        }
    }

    /// <summary>
    /// Records preprocessing for <see cref="LookupQuery"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class LookupPreProcessor(List<string> events) : IRequestPreProcessor<LookupQuery>
    {
        /// <summary>
        /// Records the preprocessing event.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(LookupQuery request, CancellationToken cancellationToken)
        {
            events.Add("pre");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records behavior execution for <see cref="LookupQuery"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class LookupBehavior(List<string> events) : IPipelineBehavior<LookupQuery, string>
    {
        /// <summary>
        /// Records events around the next delegate.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next delegate in the pipeline.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public async Task<string> Handle(LookupQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            events.Add("behavior:before");
            var response = await next();
            events.Add("behavior:after");

            return response;
        }
    }

    /// <summary>
    /// Records postprocessing for <see cref="LookupQuery"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class LookupPostProcessor(List<string> events) : IRequestPostProcessor<LookupQuery, string>
    {
        /// <summary>
        /// Records the postprocessing event.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="response">The handler response.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Process(LookupQuery request, string response, CancellationToken cancellationToken)
        {
            events.Add($"post:{response}");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Recovers from <see cref="LookupQuery"/> failures.
    /// </summary>
    private sealed class LookupExceptionHandler : IRequestExceptionHandler<LookupQuery, string, InvalidOperationException>
    {
        /// <summary>
        /// Gets a value indicating whether this handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Marks the exception handled with a replacement response.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="state">The mutable exception handling state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            LookupQuery request,
            InvalidOperationException exception,
            RequestExceptionHandlerState<string> state,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            state.SetHandled("recovered");

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents a request that declares a derived response type.
    /// </summary>
    /// <param name="Name">The dog name.</param>
    private sealed record DogQuery(string Name) : IRequest<Dog>;

    /// <summary>
    /// Handles <see cref="DogQuery"/> requests.
    /// </summary>
    private sealed class DogQueryHandler : IRequestHandler<DogQuery, Dog>
    {
        /// <summary>
        /// Handles the request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<Dog> Handle(DogQuery request, CancellationToken cancellationToken)
        {

            return Task.FromResult(new Dog(request.Name));
        }
    }

    /// <summary>
    /// Represents a request that declares two response types that are both compatible with <see cref="object"/>.
    /// </summary>
    private sealed record AmbiguousQuery : IRequest<string>, IRequest<Uri>;

    /// <summary>
    /// Handles <see cref="AmbiguousQuery"/> requests for the string response.
    /// </summary>
    private sealed class AmbiguousQueryHandler : IRequestHandler<AmbiguousQuery, string>
    {
        /// <summary>
        /// Handles the request.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that resolves to the response payload.</returns>
        public Task<string> Handle(AmbiguousQuery request, CancellationToken cancellationToken)
        {

            return Task.FromResult("ambiguous");
        }
    }

    /// <summary>
    /// Represents a stream request that declares string items.
    /// </summary>
    /// <param name="Count">The number of items to stream.</param>
    /// <param name="Throw">Whether the handler should throw before streaming.</param>
    private sealed record LookupStream(int Count, bool Throw = false) : IStreamRequest<string>;

    /// <summary>
    /// Handles <see cref="LookupStream"/> requests.
    /// </summary>
    private sealed class LookupStreamHandler : IStreamRequestHandler<LookupStream, string>
    {
        /// <summary>
        /// Gets the cancellation token received by the handler.
        /// </summary>
        public CancellationToken ReceivedToken { get; private set; }

        /// <summary>
        /// Streams numbered items.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of items.</returns>
        public async IAsyncEnumerable<string> Handle(
            LookupStream request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;

            if (request.Throw)
            {
                throw new InvalidOperationException("stream failed");
            }

            for (var index = 1; index <= request.Count; index++)
            {
                await Task.Yield();
                yield return $"item-{index}";
            }
        }
    }

    /// <summary>
    /// Records each item streamed for <see cref="LookupStream"/>.
    /// </summary>
    /// <param name="events">The shared event log.</param>
    private sealed class LookupStreamBehavior(List<string> events) : IStreamPipelineBehavior<LookupStream, string>
    {
        /// <summary>
        /// Records each item produced by the next delegate.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="next">The next delegate in the pipeline.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of items.</returns>
        public async IAsyncEnumerable<string> Handle(
            LookupStream request,
            StreamHandlerDelegate<string> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next().WithCancellation(cancellationToken))
            {
                events.Add($"behavior:{item}");
                yield return item;
            }
        }
    }

    /// <summary>
    /// Recovers from <see cref="LookupStream"/> failures.
    /// </summary>
    private sealed class LookupStreamExceptionHandler : IStreamRequestExceptionHandler<LookupStream, string, InvalidOperationException>
    {
        /// <summary>
        /// Gets a value indicating whether this handler was called.
        /// </summary>
        public bool WasCalled { get; private set; }

        /// <summary>
        /// Marks the exception handled with a replacement stream.
        /// </summary>
        /// <param name="request">The request being processed.</param>
        /// <param name="exception">The thrown exception.</param>
        /// <param name="state">The mutable exception handling state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task Handle(
            LookupStream request,
            InvalidOperationException exception,
            StreamRequestExceptionHandlerState<string> state,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            state.SetHandled(Recover());

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns the replacement stream.
        /// </summary>
        /// <returns>An asynchronous sequence with a single replacement item.</returns>
        private static async IAsyncEnumerable<string> Recover()
        {
            await Task.Yield();
            yield return "recovered";
        }
    }

    /// <summary>
    /// Represents a stream request that declares a derived response type.
    /// </summary>
    private sealed record DogStream : IStreamRequest<Dog>;

    /// <summary>
    /// Handles <see cref="DogStream"/> requests.
    /// </summary>
    private sealed class DogStreamHandler : IStreamRequestHandler<DogStream, Dog>
    {
        /// <summary>
        /// Streams dogs.
        /// </summary>
        /// <param name="request">The request to handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An asynchronous sequence of dogs.</returns>
        public async IAsyncEnumerable<Dog> Handle(
            DogStream request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Dog("Rex");
            yield return new Dog("Fido");
        }
    }

    /// <summary>
    /// Represents a stream request that declares two item types that are both compatible with <see cref="object"/>.
    /// </summary>
    private sealed record AmbiguousStream : IStreamRequest<string>, IStreamRequest<Uri>;
}