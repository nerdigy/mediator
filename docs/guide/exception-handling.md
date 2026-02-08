# Exception Handling

Nerdigy.Mediator gives you two hooks for handling exceptions that occur during request or stream processing:

- **Exception handlers** -- intercept an exception, optionally suppress it, and supply a recovery response.
- **Exception actions** -- run side effects (logging, metrics, alerting) before the exception is rethrown.

Both hooks are typed to a specific request, response, and exception type, so you can target exactly the failure scenarios you care about.

## How the Exception Flow Works

When any exception escapes the request pipeline (pre-processors, behaviors, handler, or post-processors), the runtime executes a two-phase process:

1. **Exception handlers** run first, from most-specific exception type to least-specific. If any handler calls `state.SetHandled(response)`, execution stops and the recovery response is returned to the caller. No further handlers or actions run.
2. **Exception actions** run next, but only if no handler marked the exception as handled. Every registered action executes in order from most-specific to least-specific exception type.
3. **Rethrow.** If no handler suppressed the exception, the runtime rethrows it with the original stack trace preserved via `ExceptionDispatchInfo`.

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
```

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
A catch-all handler suppresses every exception for the request type. Use this only when you have a meaningful default response for all failure scenarios.
:::

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

## Type Hierarchy Resolution

The runtime walks the thrown exception's type hierarchy from most-specific to least-specific. For a `SqlTimeoutException` that inherits from `DbException` which inherits from `Exception`, the resolution order is:

1. Handlers registered for `SqlTimeoutException`
2. Handlers registered for `DbException`
3. Handlers registered for `Exception`

The first handler that calls `state.SetHandled(...)` wins. If multiple handlers are registered for the same exception type, they execute in registration order.

The same hierarchy logic applies to exception actions.

## Registration

Exception handlers and actions are discovered automatically by `AddMediator` assembly scanning. They use `TryAddEnumerable`, which means you can register multiple handlers and actions for the same request/exception combination.

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

Assembly scanning registers this open-generic action for all request types automatically.

## Handlers vs. Actions at a Glance

| | Exception Handler | Exception Action |
|---|---|---|
| **Interface** | `IRequestExceptionHandler<TRequest, TResponse, TException>` | `IRequestExceptionAction<TRequest, TException>` |
| **Purpose** | Suppress exception, provide recovery response | Side effects before rethrow |
| **Can prevent rethrow** | Yes, via `state.SetHandled(...)` | No |
| **Runs when** | Always (first phase) | Only if no handler suppressed the exception |
| **Has access to response type** | Yes (`TResponse`) | No |
| **Stream variant** | `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` | Same `IRequestExceptionAction` interface |
