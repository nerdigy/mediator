# Exception Handling

Nerdigy.Mediator gives you two hooks for handling exceptions that occur during request or stream processing:

- **Exception handlers** -- intercept an exception, optionally suppress it, and supply a recovery response.
- **Exception actions** -- run side effects (logging, metrics, alerting) before the exception is rethrown.

Both hooks are typed to a specific request, response, and exception type, so you can target exactly the failure scenarios you care about.

## How the Exception Flow Works

When any exception escapes the request pipeline (pre-processors, behaviors, handler, or post-processors), the runtime executes a two-phase process:

1. **Exception handlers** run first, from most-specific exception type to least-specific. If any handler calls `state.SetHandled(response)`, execution stops and the recovery response is returned to the caller. No further handlers or actions run. Handlers are skipped when the caller canceled (see [Cancellation](#cancellation)).
2. **Exception actions** run next, but only if no handler marked the exception as handled. Every registered action executes in order from most-specific to least-specific exception type, even when an earlier action fails.
3. **Rethrow.** If no handler suppressed the exception, the runtime rethrows the original exception instance with its stack trace preserved via `ExceptionDispatchInfo`. If a handler or action failed along the way, it throws a `MediatorExceptionProcessingException` instead (see [Failures During Exception Processing](#failures-during-exception-processing)).

Each handler or action implementation runs at most once per exception (see [Type Hierarchy Resolution](#type-hierarchy-resolution)).

```
Pipeline throws
    |
    v
ExceptionHandlers (most-specific -> least-specific)
    |                          |
    | state.SetHandled(...)    | not handled
    v                          v
 return recovery          ExceptionActions (most-specific -> least-specific)
   response                    |
                               v
                          rethrow original exception
                          (or MediatorExceptionProcessingException
                           if a handler or action failed)
```

A missing handler is reported through the same flow: the `InvalidOperationException` it throws reaches exception actions like any other failure.

## Request Exception Handlers

Implement `IRequestExceptionHandler<TRequest, TResponse, TException>` to intercept a specific exception type thrown during `Send`. The handler receives a `RequestExceptionHandlerState<TResponse>` that you use to mark the exception as handled and provide a fallback response.

```csharp
public sealed class TimeoutFallbackHandler
    : IRequestExceptionHandler<GetUserQuery, UserDto, TimeoutException>
{
    public Task Handle(
        GetUserQuery request,
        TimeoutException exception,
        RequestExceptionHandlerState<UserDto> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(new UserDto(request.Id, "unavailable"));

        return Task.CompletedTask;
    }
}
```

When `state.SetHandled(response)` is called:

- The exception is suppressed -- it will not propagate to the caller.
- The provided response is returned from `Send` as if the handler had succeeded.
- No exception actions run.

If you do not call `state.SetHandled(...)`, the next handler in the chain is invoked. If no handler marks the exception as handled, execution falls through to exception actions and a rethrow.

### Handling Multiple Exception Types

Register multiple exception handlers to cover different failure modes for the same request:

```csharp
public sealed class DbFallbackHandler
    : IRequestExceptionHandler<GetUserQuery, UserDto, DbException>
{
    public Task Handle(
        GetUserQuery request,
        DbException exception,
        RequestExceptionHandlerState<UserDto> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(UserDto.Empty);

        return Task.CompletedTask;
    }
}
```

Both `TimeoutFallbackHandler` and `DbFallbackHandler` can be registered for the same request type. The runtime resolves handlers based on the actual exception type thrown and walks the exception type hierarchy from most-specific to least-specific.

### Catch-All Handlers

Use `Exception` as the type parameter to handle any exception for a given request:

```csharp
public sealed class CatchAllHandler
    : IRequestExceptionHandler<GetUserQuery, UserDto, Exception>
{
    public Task Handle(
        GetUserQuery request,
        Exception exception,
        RequestExceptionHandlerState<UserDto> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(UserDto.Empty);

        return Task.CompletedTask;
    }
}
```

::: warning
A catch-all handler suppresses every exception for the request type. Use this only when you have a meaningful default response for all failure scenarios. It never suppresses the caller's own cancellation; see [Cancellation](#cancellation).
:::

## Cancellation

When the caller's `CancellationToken` is canceled and the request fails with an `OperationCanceledException`, the runtime skips exception handlers, so no handler can turn the caller's cancellation into a response. Exception actions still run, so cancellations can be logged, and the cancellation propagates to the caller.

A cancellation the caller did not request is handled like any other exception. For example, a behavior that passes a timeout token to `next(timeoutToken)` can have its `OperationCanceledException` recovered by an exception handler, because the caller's token was not canceled.

Stream requests follow the same rule, using the enumeration's token.

## Exception Actions

Implement `IRequestExceptionAction<TRequest, TException>` for side effects that should run when an exception is not handled. Actions cannot suppress the exception -- they observe it before the rethrow.

```csharp
public sealed class LoggingExceptionAction
    : IRequestExceptionAction<GetUserQuery, Exception>
{
    private readonly ILogger<LoggingExceptionAction> _logger;

    public LoggingExceptionAction(ILogger<LoggingExceptionAction> logger)
    {
        _logger = logger;
    }

    public Task Execute(
        GetUserQuery request,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Failed to process {RequestType} for user {UserId}",
            typeof(GetUserQuery).Name, request.Id);

        return Task.CompletedTask;
    }
}
```

Actions are the right place for:

- **Logging** -- structured logging with request context
- **Metrics** -- incrementing failure counters
- **Alerting** -- sending notifications for critical failures

::: info
Exception actions share the `IRequestExceptionAction<TRequest, TException>` interface for both standard requests and stream requests. You do not need a separate action interface for streams.
:::

## Stream Exception Handlers

Implement `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` to intercept exceptions thrown during `CreateStream`. Instead of providing a single response value, the handler supplies a replacement `IAsyncEnumerable<TResponse>` stream.

```csharp
public sealed class StreamFallbackHandler
    : IStreamRequestExceptionHandler<ReadEventsRequest, EventEnvelope, IOException>
{
    public Task Handle(
        ReadEventsRequest request,
        IOException exception,
        StreamRequestExceptionHandlerState<EventEnvelope> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(EmptyStream(cancellationToken));

        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<EventEnvelope> EmptyStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
```

The stream exception pipeline catches exceptions at two points:

- **Stream creation** -- exceptions thrown during pre-processors, stream behaviors, or initial handler invocation.
- **Stream enumeration** -- exceptions thrown during `MoveNextAsync` as items are yielded.

In both cases, `IStreamRequestExceptionHandler` can supply a replacement stream. If a handler provides one during enumeration, the runtime switches to the new stream and continues yielding items from it.

Exception handlers can replace a stream one time only for each enumeration. If the replacement stream throws, exception actions run and the exception propagates. To try more than one fallback source, write the replacement stream so that it tries each source.

When enumeration fails, the failed enumerator is disposed before the exception handler runs, so the replacement can reopen the same resource. If that disposal throws, no replacement is attempted; actions run and the runtime throws `MediatorExceptionProcessingException`.

## Type Hierarchy Resolution

The runtime walks the thrown exception's type hierarchy from most-specific to least-specific. For a `SqlTimeoutException` that inherits from `DbException` which inherits from `Exception`, the resolution order is:

1. Handlers registered for `SqlTimeoutException`
2. Handlers registered for `DbException`
3. Handlers registered for `Exception`

The first handler that calls `state.SetHandled(...)` wins. If multiple handlers are registered for the same exception type, they execute in registration order.

The same hierarchy logic applies to exception actions.

**Each implementation runs once, at the most specific level it matches,** like a `catch` block. This matters for open generic components: the container closes `GlobalExceptionAction<TRequest, TException>` at every level of the hierarchy, but for a `SqlTimeoutException` it runs once, as `GlobalExceptionAction<TRequest, SqlTimeoutException>`. A generic implementation counts as one implementation across all its closed types. To act at several levels, use a separate class for each level.

## Failures During Exception Processing

If an exception handler or exception action throws, or a failed stream's cleanup throws, the original exception is not lost. The runtime still runs every remaining exception action, then throws a `MediatorExceptionProcessingException`:

- **`InnerException`** is the exception the request originally failed with.
- **`ProcessingExceptions`** lists the exceptions thrown while processing it.

```csharp
try
{
    await mediator.Send(new GetUserQuery(id), cancellationToken);
}
catch (MediatorExceptionProcessingException exception) when (exception.InnerException is NotFoundException)
{
    // The request failed with NotFoundException, and an exception action also failed.
}
catch (NotFoundException)
{
    // The request failed with NotFoundException.
}
```

### Nested Requests

When a handler sends another request and that inner request fails, the inner request's exception actions run first. If the exception then escapes the outer handler, the outer request's exception actions run too, so a global logging action sees the exception at both levels. Catch the exception in the outer handler if it should only be reported once.

## Registration

Exception handlers and actions are discovered automatically by `AddMediator` assembly scanning, or registered explicitly with `AddExceptionHandler`, `AddExceptionAction`, and `AddStreamExceptionHandler`. You can register multiple handlers and actions for the same request/exception combination; registering the same implementation twice has no effect.

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<TimeoutFallbackHandler>();
});
```

No additional configuration is needed. Any class implementing `IRequestExceptionHandler<,,>`, `IStreamRequestExceptionHandler<,,>`, or `IRequestExceptionAction<,>` in the scanned assemblies is registered.

### Open-Generic Registration

Open-generic exception handlers and actions are supported. Register a handler that applies to every request type:

```csharp
public sealed class GlobalExceptionAction<TRequest, TException>
    : IRequestExceptionAction<TRequest, TException>
    where TRequest : IBaseRequest
    where TException : Exception
{
    private readonly ILogger<GlobalExceptionAction<TRequest, TException>> _logger;

    public GlobalExceptionAction(ILogger<GlobalExceptionAction<TRequest, TException>> logger)
    {
        _logger = logger;
    }

    public Task Execute(
        TRequest request,
        TException exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception for {RequestType}", typeof(TRequest).Name);

        return Task.CompletedTask;
    }
}
```

Assembly scanning registers this open-generic action for all request types automatically. It runs once per failed request, at the most specific level of the thrown exception's hierarchy.

## Handlers vs. Actions at a Glance

| | Exception Handler | Exception Action |
|---|---|---|
| **Interface** | `IRequestExceptionHandler<TRequest, TResponse, TException>` | `IRequestExceptionAction<TRequest, TException>` |
| **Purpose** | Suppress exception, provide recovery response | Side effects before rethrow |
| **Can prevent rethrow** | Yes, via `state.SetHandled(...)` | No |
| **Runs when** | First phase, unless the caller canceled | Only if no handler suppressed the exception |
| **Has access to response type** | Yes (`TResponse`) | No |
| **Stream variant** | `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` | Same `IRequestExceptionAction` interface |
