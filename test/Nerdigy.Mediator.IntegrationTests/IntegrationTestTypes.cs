using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Abstractions;

namespace Nerdigy.Mediator.IntegrationTests;

/// <summary>
/// Collects integration test events and counters.
/// </summary>
public sealed class IntegrationTracker
{
    private int _notificationCount;
    private readonly List<string> _events = [];

    /// <summary>
    /// Gets a snapshot of recorded events.
    /// </summary>
    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>
    /// Gets the notification handler invocation count.
    /// </summary>
    public int NotificationCount => _notificationCount;

    /// <summary>
    /// Records an event.
    /// </summary>
    /// <param name="eventName">The event name.</param>
    public void Record(string eventName)
    {
        ArgumentNullException.ThrowIfNull(eventName);

        lock (_events)
        {
            _events.Add(eventName);
        }
    }

    /// <summary>
    /// Increments the notification invocation count.
    /// </summary>
    public void IncrementNotificationCount()
    {
        _ = Interlocked.Increment(ref _notificationCount);
    }
}

/// <summary>
/// Represents a send request for integration tests.
/// </summary>
/// <param name="Value">The request payload.</param>
public sealed record IntegrationRequest(string Value) : IRequest<string>;

/// <summary>
/// Handles <see cref="IntegrationRequest"/> requests.
/// </summary>
public sealed class IntegrationRequestHandler : IRequestHandler<IntegrationRequest, string>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationRequestHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationRequestHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles the request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public Task<string> Handle(IntegrationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("handler");

        return Task.FromResult($"handled:{request.Value}");
    }
}

/// <summary>
/// Executes before <see cref="IntegrationRequestHandler"/>.
/// </summary>
public sealed class IntegrationRequestPreProcessor : IRequestPreProcessor<IntegrationRequest>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationRequestPreProcessor"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationRequestPreProcessor(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Processes the request before handler execution.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Process(IntegrationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("pre");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Executes around <see cref="IntegrationRequestHandler"/>.
/// </summary>
public sealed class IntegrationRequestBehavior : IPipelineBehavior<IntegrationRequest, string>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationRequestBehavior"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationRequestBehavior(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes behavior logic around the next delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public async Task<string> Handle(
        IntegrationRequest request,
        RequestHandlerDelegate<string> next,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("behavior:before");
        var response = await next().ConfigureAwait(false);
        _tracker.Record("behavior:after");

        return response;
    }
}

/// <summary>
/// Executes after <see cref="IntegrationRequestHandler"/>.
/// </summary>
public sealed class IntegrationRequestPostProcessor : IRequestPostProcessor<IntegrationRequest, string>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationRequestPostProcessor"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationRequestPostProcessor(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Processes the request after handler execution.
    /// </summary>
    /// <param name="request">The handled request.</param>
    /// <param name="response">The handler response.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Process(IntegrationRequest request, string response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record($"post:{response}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Executes around all request handlers through an open-generic pipeline registration.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public sealed class GenericIntegrationRequestBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericIntegrationRequestBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public GenericIntegrationRequestBehavior(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes behavior logic around the next delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("generic-behavior:before");
        var response = await next().ConfigureAwait(false);
        _tracker.Record("generic-behavior:after");

        return response;
    }
}

/// <summary>
/// Executes around all request handlers and records ordering marker A.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public sealed class OrderedIntegrationRequestBehaviorA<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrderedIntegrationRequestBehaviorA{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public OrderedIntegrationRequestBehaviorA(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes behavior logic around the next delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("ordered-a:before");
        var response = await next().ConfigureAwait(false);
        _tracker.Record("ordered-a:after");

        return response;
    }
}

/// <summary>
/// Executes around all request handlers and records ordering marker B.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response payload type.</typeparam>
public sealed class OrderedIntegrationRequestBehaviorB<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrderedIntegrationRequestBehaviorB{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public OrderedIntegrationRequestBehaviorB(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes behavior logic around the next delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that resolves to the response payload.</returns>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("ordered-b:before");
        var response = await next().ConfigureAwait(false);
        _tracker.Record("ordered-b:after");

        return response;
    }
}

/// <summary>
/// Represents a request whose handler always throws.
/// </summary>
/// <param name="Value">The request payload.</param>
public sealed record ThrowingIntegrationRequest(string Value) : IRequest<string>;

/// <summary>
/// Throws for <see cref="ThrowingIntegrationRequest"/>.
/// </summary>
public sealed class ThrowingIntegrationRequestHandler : IRequestHandler<ThrowingIntegrationRequest, string>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrowingIntegrationRequestHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public ThrowingIntegrationRequestHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Throws an exception for every request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that never completes successfully.</returns>
    public Task<string> Handle(ThrowingIntegrationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("throwing-handler");
        throw new InvalidOperationException("send failure");
    }
}

/// <summary>
/// Handles thrown exceptions for <see cref="ThrowingIntegrationRequest"/>.
/// </summary>
public sealed class ThrowingIntegrationRequestExceptionHandler
    : IRequestExceptionHandler<ThrowingIntegrationRequest, string, InvalidOperationException>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrowingIntegrationRequestExceptionHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public ThrowingIntegrationRequestExceptionHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles a thrown exception and supplies a fallback response.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="state">The mutable handler state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Handle(
        ThrowingIntegrationRequest request,
        InvalidOperationException exception,
        RequestExceptionHandlerState<string> state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("throwing-handler:exception-handled");
        state.SetHandled("recovered");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents a request with no matching exception handler.
/// </summary>
/// <param name="Value">The request payload.</param>
public sealed record UnhandledThrowingIntegrationRequest(string Value) : IRequest<string>;

/// <summary>
/// Throws for <see cref="UnhandledThrowingIntegrationRequest"/>.
/// </summary>
public sealed class UnhandledThrowingIntegrationRequestHandler : IRequestHandler<UnhandledThrowingIntegrationRequest, string>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnhandledThrowingIntegrationRequestHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public UnhandledThrowingIntegrationRequestHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Throws an exception for every request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that never completes successfully.</returns>
    public Task<string> Handle(UnhandledThrowingIntegrationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("unhandled-throwing-handler");
        throw new InvalidOperationException("unhandled send failure");
    }
}

/// <summary>
/// Executes for unhandled send request exceptions.
/// </summary>
public sealed class UnhandledThrowingIntegrationRequestExceptionAction
    : IRequestExceptionAction<UnhandledThrowingIntegrationRequest, InvalidOperationException>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnhandledThrowingIntegrationRequestExceptionAction"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public UnhandledThrowingIntegrationRequestExceptionAction(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes after unhandled send exceptions.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Execute(
        UnhandledThrowingIntegrationRequest request,
        InvalidOperationException exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("unhandled-send-action");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents an integration test notification.
/// </summary>
/// <param name="Name">The notification name.</param>
public sealed record IntegrationNotification(string Name) : INotification;

/// <summary>
/// Handles <see cref="IntegrationNotification"/> notifications.
/// </summary>
public sealed class IntegrationNotificationHandlerOne : INotificationHandler<IntegrationNotification>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationNotificationHandlerOne"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationNotificationHandlerOne(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles a notification.
    /// </summary>
    /// <param name="notification">The notification to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Handle(IntegrationNotification notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.IncrementNotificationCount();
        _tracker.Record("notification-handler-1");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Handles <see cref="IntegrationNotification"/> notifications.
/// </summary>
public sealed class IntegrationNotificationHandlerTwo : INotificationHandler<IntegrationNotification>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationNotificationHandlerTwo"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationNotificationHandlerTwo(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles a notification.
    /// </summary>
    /// <param name="notification">The notification to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Handle(IntegrationNotification notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.IncrementNotificationCount();
        _tracker.Record("notification-handler-2");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents a stream request for integration tests.
/// </summary>
/// <param name="Count">The number of values to produce.</param>
public sealed record IntegrationStreamRequest(int Count) : IStreamRequest<int>;

/// <summary>
/// Handles <see cref="IntegrationStreamRequest"/> requests.
/// </summary>
public sealed class IntegrationStreamRequestHandler : IStreamRequestHandler<IntegrationStreamRequest, int>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationStreamRequestHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationStreamRequestHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles a stream request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of stream values.</returns>
    public IAsyncEnumerable<int> Handle(IntegrationStreamRequest request, CancellationToken cancellationToken)
    {
        _tracker.Record("stream-handler:configured");

        return Execute(request.Count, cancellationToken);
    }

    /// <summary>
    /// Executes stream enumeration.
    /// </summary>
    /// <param name="count">The number of values to produce.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of stream values.</returns>
    private async IAsyncEnumerable<int> Execute(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _tracker.Record("stream-handler:start");

        for (var value = 1; value <= count; value++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }

        _tracker.Record("stream-handler:end");
    }
}

/// <summary>
/// Executes before <see cref="IntegrationStreamRequestHandler"/>.
/// </summary>
public sealed class IntegrationStreamPreProcessor : IRequestPreProcessor<IntegrationStreamRequest>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationStreamPreProcessor"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationStreamPreProcessor(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Processes the stream request before handler execution.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Process(IntegrationStreamRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("stream-pre");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps stream handler enumeration and transforms output values.
/// </summary>
public sealed class IntegrationStreamBehavior : IStreamPipelineBehavior<IntegrationStreamRequest, int>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntegrationStreamBehavior"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public IntegrationStreamBehavior(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes behavior logic around the next stream delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next stream delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of transformed stream values.</returns>
    public IAsyncEnumerable<int> Handle(
        IntegrationStreamRequest request,
        StreamHandlerDelegate<int> next,
        CancellationToken cancellationToken)
    {
        _tracker.Record("stream-behavior:before");

        return Wrap(next, cancellationToken);
    }

    /// <summary>
    /// Wraps stream enumeration and transforms each value.
    /// </summary>
    /// <param name="next">The next stream delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of transformed stream values.</returns>
    private async IAsyncEnumerable<int> Wrap(
        StreamHandlerDelegate<int> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            yield return item * 2;
        }

        _tracker.Record("stream-behavior:after");
    }
}

/// <summary>
/// Executes around all stream request handlers through an open-generic pipeline registration.
/// </summary>
/// <typeparam name="TRequest">The stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response payload type.</typeparam>
public sealed class GenericIntegrationStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericIntegrationStreamBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public GenericIntegrationStreamBehavior(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes behavior logic around the next stream delegate.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="next">The next stream delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of stream values.</returns>
    public IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        _tracker.Record("generic-stream-behavior:before");

        return Wrap(next, cancellationToken);
    }

    /// <summary>
    /// Wraps stream enumeration and records completion.
    /// </summary>
    /// <param name="next">The next stream delegate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of stream values.</returns>
    private async IAsyncEnumerable<TResponse> Wrap(
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            yield return item;
        }

        _tracker.Record("generic-stream-behavior:after");
    }
}

/// <summary>
/// Represents a stream request whose handler throws.
/// </summary>
public sealed record ThrowingIntegrationStreamRequest : IStreamRequest<int>;

/// <summary>
/// Throws during stream enumeration for <see cref="ThrowingIntegrationStreamRequest"/>.
/// </summary>
public sealed class ThrowingIntegrationStreamRequestHandler : IStreamRequestHandler<ThrowingIntegrationStreamRequest, int>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrowingIntegrationStreamRequestHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public ThrowingIntegrationStreamRequestHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles a stream request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence that throws.</returns>
    public IAsyncEnumerable<int> Handle(ThrowingIntegrationStreamRequest request, CancellationToken cancellationToken)
    {
        _tracker.Record("stream-throwing-handler:configured");

        return ThrowingStream(cancellationToken);
    }

    /// <summary>
    /// Produces a stream that throws an exception.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence that throws.</returns>
    private async IAsyncEnumerable<int> ThrowingStream([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _tracker.Record("stream-throwing-handler:start");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("stream handled failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>
/// Handles stream exceptions for <see cref="ThrowingIntegrationStreamRequest"/>.
/// </summary>
public sealed class ThrowingIntegrationStreamRequestExceptionHandler
    : IStreamRequestExceptionHandler<ThrowingIntegrationStreamRequest, int, InvalidOperationException>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThrowingIntegrationStreamRequestExceptionHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public ThrowingIntegrationStreamRequestExceptionHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles an exception and supplies a fallback stream.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="state">The mutable handler state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Handle(
        ThrowingIntegrationStreamRequest request,
        InvalidOperationException exception,
        StreamRequestExceptionHandlerState<int> state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("stream-exception-handled");
        state.SetHandled(Fallback(cancellationToken));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Produces fallback stream values.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence of fallback values.</returns>
    private static async IAsyncEnumerable<int> Fallback([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return 99;
    }
}

/// <summary>
/// Represents a stream request with no matching exception handler.
/// </summary>
public sealed record UnhandledThrowingIntegrationStreamRequest : IStreamRequest<int>;

/// <summary>
/// Throws during stream enumeration for <see cref="UnhandledThrowingIntegrationStreamRequest"/>.
/// </summary>
public sealed class UnhandledThrowingIntegrationStreamRequestHandler : IStreamRequestHandler<UnhandledThrowingIntegrationStreamRequest, int>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnhandledThrowingIntegrationStreamRequestHandler"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public UnhandledThrowingIntegrationStreamRequestHandler(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Handles a stream request.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence that throws.</returns>
    public IAsyncEnumerable<int> Handle(UnhandledThrowingIntegrationStreamRequest request, CancellationToken cancellationToken)
    {
        _tracker.Record("stream-unhandled-throwing-handler:configured");

        return ThrowingStream(cancellationToken);
    }

    /// <summary>
    /// Produces a stream that throws an exception.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An asynchronous sequence that throws.</returns>
    private async IAsyncEnumerable<int> ThrowingStream([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _tracker.Record("stream-unhandled-throwing-handler:start");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("stream unhandled failure");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>
/// Executes for unhandled stream request exceptions.
/// </summary>
public sealed class UnhandledThrowingIntegrationStreamRequestExceptionAction
    : IRequestExceptionAction<UnhandledThrowingIntegrationStreamRequest, InvalidOperationException>
{
    private readonly IntegrationTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnhandledThrowingIntegrationStreamRequestExceptionAction"/> class.
    /// </summary>
    /// <param name="tracker">The shared test tracker.</param>
    public UnhandledThrowingIntegrationStreamRequestExceptionAction(IntegrationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
    }

    /// <summary>
    /// Executes after unhandled stream exceptions.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task Execute(
        UnhandledThrowingIntegrationStreamRequest request,
        InvalidOperationException exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tracker.Record("unhandled-stream-action");

        return Task.CompletedTask;
    }
}