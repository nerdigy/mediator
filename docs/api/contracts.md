# Contracts Reference

All public contracts live in the `Nerdigy.Mediator.Abstractions` package. Install it alone when you only need to define messages and handlers in a class library that should not reference the runtime.

```bash
dotnet add package Nerdigy.Mediator.Abstractions
```

---

## Core Dispatch Interfaces

These are the entry points your application code depends on. Inject `IMediator` (or the narrower `ISender` / `IPublisher`) and dispatch messages without knowing which handler will process them.

### IMediator

Combines request sending and notification publishing into a single interface.

```csharp
public interface IMediator : ISender, IPublisher;
```

Inject `IMediator` when a component needs both `Send` and `Publish`. Prefer the narrower `ISender` or `IPublisher` when only one capability is required.

### ISender

Dispatches requests and stream requests to their handlers.

```csharp
public interface ISender
{
    Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default);

    Task Send(
        IRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);
}
```

- **`Send<TResponse>`** -- dispatches a request to exactly one `IRequestHandler<TRequest, TResponse>` and returns the response.
- **`Send`** (void overload) -- dispatches a request to exactly one `IRequestHandler<TRequest>`. No return value.
- **`CreateStream<TResponse>`** -- dispatches a stream request to exactly one `IStreamRequestHandler<TRequest, TResponse>` and returns an `IAsyncEnumerable<TResponse>`.

### IPublisher

Publishes notifications to all registered handlers.

```csharp
public interface IPublisher
{
    Task Publish<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
```

The notification is delivered to every `INotificationHandler<TNotification>` registered in the container. The publishing strategy (sequential or parallel) is configured at registration time.

---

## Message Contracts

Marker interfaces that define the shape of messages flowing through the mediator. Your request records and notification classes implement these.

### IBaseRequest

Root marker interface for all request types. Pre-processors and exception actions constrain against this type.

```csharp
public interface IBaseRequest;
```

You do not implement `IBaseRequest` directly. Use `IRequest<TResponse>`, `IRequest`, or `IStreamRequest<TResponse>` instead.

### IRequest\<TResponse\>

A request that expects a typed response. Each request type must have exactly one corresponding handler.

```csharp
public interface IRequest<out TResponse> : IBaseRequest;
```

**Usage:**

```csharp
public sealed record GetUserQuery(Guid Id) : IRequest<UserDto>;
```

### IRequest

A request that produces no response. Extends `IRequest<Unit>` internally, so it flows through the same pipeline infrastructure.

```csharp
public interface IRequest : IRequest<Unit>;
```

**Usage:**

```csharp
public sealed record DeleteUserCommand(Guid Id) : IRequest;
```

### IStreamRequest\<TResponse\>

A request that returns an asynchronous stream of values rather than a single response.

```csharp
public interface IStreamRequest<out TResponse> : IBaseRequest;
```

**Usage:**

```csharp
public sealed record ReadEventsRequest(string StreamName) : IStreamRequest<EventEnvelope>;
```

### INotification

A fire-and-forget message dispatched to zero or more handlers. Unlike requests, notifications have no return value and support multiple handlers.

```csharp
public interface INotification;
```

**Usage:**

```csharp
public sealed record UserCreatedEvent(Guid UserId) : INotification;
```

---

## Handler Interfaces

One handler per message type. The assembly scanner auto-registers these when you call `AddMediator(...)`.

### IRequestHandler\<TRequest, TResponse\>

Handles a request and returns a response.

```csharp
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

**Usage:**

```csharp
public sealed class GetUserHandler : IRequestHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new UserDto(request.Id, "Ada"));
    }
}
```

### IRequestHandler\<TRequest\>

Handles a void request. No return value.

```csharp
public interface IRequestHandler<in TRequest>
    where TRequest : IRequest
{
    Task Handle(TRequest request, CancellationToken cancellationToken);
}
```

**Usage:**

```csharp
public sealed class DeleteUserHandler : IRequestHandler<DeleteUserCommand>
{
    public Task Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        // perform deletion
        return Task.CompletedTask;
    }
}
```

### IStreamRequestHandler\<TRequest, TResponse\>

Handles a stream request and yields an asynchronous sequence of values.

```csharp
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

**Usage:**

```csharp
public sealed class ReadEventsHandler : IStreamRequestHandler<ReadEventsRequest, EventEnvelope>
{
    public async IAsyncEnumerable<EventEnvelope> Handle(
        ReadEventsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in LoadEvents(request.StreamName, cancellationToken))
        {
            yield return envelope;
        }
    }
}
```

### INotificationHandler\<TNotification\>

Handles a published notification. Multiple handlers can be registered for the same notification type.

```csharp
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
```

**Usage:**

```csharp
public sealed class SendWelcomeEmail : INotificationHandler<UserCreatedEvent>
{
    public Task Handle(UserCreatedEvent notification, CancellationToken cancellationToken)
    {
        // send email
        return Task.CompletedTask;
    }
}
```

---

## Pipeline Interfaces

Pipeline components wrap or augment request handling. They execute in registration order.

::: info Pipeline Execution Order
**PreProcessors** --> **Pipeline Behaviors** (outer to inner) --> **Handler** --> **PostProcessors**
:::

### IRequestPreProcessor\<TRequest\>

Runs before the handler and all pipeline behaviors. Use for validation, logging, or enrichment.

```csharp
public interface IRequestPreProcessor<in TRequest>
    where TRequest : IBaseRequest
{
    Task Process(TRequest request, CancellationToken cancellationToken);
}
```

The `IBaseRequest` constraint means pre-processors apply to both standard requests and stream requests.

### IPipelineBehavior\<TRequest, TResponse\>

Middleware for request handling. Wraps the handler call and can inspect, modify, or short-circuit the request.

```csharp
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

Call `await next()` to invoke the next behavior or the handler itself. Return without calling `next` to short-circuit.

**Usage:**

```csharp
public sealed class LoggingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // before handler
        TResponse response = await next();
        // after handler
        return response;
    }
}
```

### IStreamPipelineBehavior\<TRequest, TResponse\>

Middleware for stream request handling. Same wrapping pattern as `IPipelineBehavior`, but operates on `IAsyncEnumerable<TResponse>`.

```csharp
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

### IRequestPostProcessor\<TRequest, TResponse\>

Runs after the handler completes successfully. Receives both the request and the response. Use for auditing, caching, or metrics.

```csharp
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : IRequest<TResponse>
{
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
```

---

## Exception Interfaces

Exception components run when the pipeline throws. Handlers can recover by providing a fallback response; actions perform side effects before the exception is rethrown.

::: info Exception Evaluation Order
Exception handlers and actions are evaluated from **most specific** exception type to **least specific** (`InvalidOperationException` before `Exception`). The first handler that calls `state.SetHandled(...)` wins. If no handler recovers, actions run and the original exception is rethrown with its stack trace preserved.
:::

### IRequestExceptionHandler\<TRequest, TResponse, TException\>

Intercepts an exception and can recover by supplying a fallback response.

```csharp
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IRequest<TResponse>
    where TException : Exception
{
    Task Handle(
        TRequest request,
        TException exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}
```

Call `state.SetHandled(response)` to suppress the exception and return `response` to the caller.

### IStreamRequestExceptionHandler\<TRequest, TResponse, TException\>

Intercepts an exception thrown during stream enumeration and can recover by supplying a replacement stream.

```csharp
public interface IStreamRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IStreamRequest<TResponse>
    where TException : Exception
{
    Task Handle(
        TRequest request,
        TException exception,
        StreamRequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}
```

Call `state.SetHandled(replacementStream)` to suppress the exception and yield the replacement `IAsyncEnumerable<TResponse>`.

### IRequestExceptionAction\<TRequest, TException\>

Performs side effects (logging, metrics, alerting) when an exception occurs. Actions cannot recover the exception -- they run after exception handlers and before rethrow.

```csharp
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IBaseRequest
    where TException : Exception
{
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}
```

### RequestExceptionHandlerState\<TResponse\>

Mutable state object passed to request exception handlers. Controls whether the exception is suppressed.

```csharp
public sealed class RequestExceptionHandlerState<TResponse>
{
    public bool Handled { get; }
    public TResponse? Response { get; }

    public void SetHandled(TResponse response);
}
```

- **`Handled`** -- `true` after `SetHandled` has been called.
- **`Response`** -- the fallback value returned to the caller when `Handled` is `true`.

### StreamRequestExceptionHandlerState\<TResponse\>

Mutable state object passed to stream exception handlers. Controls whether the exception is suppressed.

```csharp
public sealed class StreamRequestExceptionHandlerState<TResponse>
{
    public bool Handled { get; }
    public IAsyncEnumerable<TResponse>? ResponseStream { get; }

    public void SetHandled(IAsyncEnumerable<TResponse> responseStream);
}
```

- **`Handled`** -- `true` after `SetHandled` has been called.
- **`ResponseStream`** -- the replacement stream returned to the caller when `Handled` is `true`. Must not be `null`.

---

## Delegate Types

These delegates represent the "next step" in a pipeline. Pipeline behaviors receive them as parameters and invoke them to continue the chain.

### RequestHandlerDelegate\<TResponse\>

The next delegate in a request pipeline.

```csharp
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();
```

Invoke with `await next()` inside an `IPipelineBehavior<TRequest, TResponse>`.

### StreamHandlerDelegate\<TResponse\>

The next delegate in a stream request pipeline.

```csharp
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>();
```

Invoke with `next()` inside an `IStreamPipelineBehavior<TRequest, TResponse>`.

---

## Utility Types

### Unit

A void-like struct used as the `TResponse` type for requests that produce no value. `IRequest` extends `IRequest<Unit>` so void requests flow through the same generic pipeline infrastructure.

```csharp
public readonly struct Unit : IEquatable<Unit>
{
    public static readonly Unit Value = default;
}
```

You do not interact with `Unit` directly in most application code. Implement `IRequest` and `IRequestHandler<TRequest>` instead of working with `IRequest<Unit>` and `IRequestHandler<TRequest, Unit>`.
