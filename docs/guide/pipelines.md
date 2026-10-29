# Pipelines and Processors

Pipelines let you wrap cross-cutting concerns around every request without touching handler code. Logging, validation, authorization, timing -- implement each once and apply it broadly via open-generic registrations.

## How the Pipeline Works

When you call `Send(...)`, Nerdigy.Mediator builds a pipeline around your handler. The execution order is fixed:

1. **Pre-processors** -- run sequentially before the behaviors, in registration order
2. **Pipeline behaviors** -- wrap the handler like middleware, nesting from outermost to innermost in registration order
3. **Handler** -- your `IRequestHandler<TRequest, TResponse>` executes
4. **Post-processors** -- run sequentially after the handler returns, in registration order, inside the innermost behavior

If any stage throws, exception handlers and actions take over (see [Exception Handling](/guide/exception-handling)).

### Visual Execution Flow

```
PreProcessor 1
PreProcessor 2
Behavior 1 (before next)
  Behavior 2 (before next)
    ── Handler ──
    PostProcessor 1
    PostProcessor 2
  Behavior 2 (after next)
Behavior 1 (after next)
```

Pre-processors run outside every behavior, so a behavior does not see their time or failures, and a short-circuiting behavior cannot skip them. Post-processors run inside the innermost behavior, so every behavior sees their time, and they are skipped when a behavior short-circuits.

When a request has no pre-processors, behaviors, or post-processors, the runtime calls the handler directly.

Each behavior receives a `RequestHandlerDelegate<TResponse>` called `next`. Calling `next()` invokes the next behavior in the chain, or the handler itself if no behaviors remain. This is the same nesting model as ASP.NET Core middleware. A behavior can call `next` more than once, for example to retry, and each call runs the rest of the pipeline again.

## Pipeline Behaviors

`IPipelineBehavior<TRequest, TResponse>` is the primary extension point. Each behavior wraps the rest of the pipeline, giving you full control over what happens before, after, and around handler execution.

### Interface

```csharp
public interface IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

The `next` delegate is a `RequestHandlerDelegate<TResponse>` that invokes the remainder of the pipeline:

```csharp
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);
```

Call `next()` to keep the token the behavior received, or `next(token)` to give the inner behaviors and the handler a different token. Passing `default` also keeps the current token.

### Logging Behavior

A behavior that logs before and after every request:

```csharp
public sealed class LoggingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling {RequestType}", typeof(TRequest).Name);

        TResponse response = await next();

        _logger.LogInformation("Handled {RequestType}", typeof(TRequest).Name);

        return response;
    }
}
```

Because this behavior uses open generics (`TRequest`, `TResponse`), the assembly scanner registers it for every request type automatically.

### Timing Behavior

Measure handler execution duration:

```csharp
public sealed class TimingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<TimingBehavior<TRequest, TResponse>> _logger;

    public TimingBehavior(ILogger<TimingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        TResponse response = await next();

        stopwatch.Stop();
        _logger.LogInformation(
            "{RequestType} completed in {ElapsedMs}ms",
            typeof(TRequest).Name,
            stopwatch.ElapsedMilliseconds);

        return response;
    }
}
```

### Short-Circuiting

A behavior can return without calling `next()`, skipping the handler entirely. This is useful for validation, caching, or authorization:

```csharp
public sealed class ValidationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        foreach (IValidator<TRequest> validator in _validators)
        {
            ValidationResult result = await validator.ValidateAsync(request, cancellationToken);

            if (!result.IsValid)
            {
                throw new ValidationException(result.Errors);
            }
        }

        return await next();
    }
}
```

When this behavior throws before calling `next()`, the handler never executes and the exception flows to exception handlers.

### Timeout Behavior

Pass a linked token to `next` so the handler itself is canceled when the timeout expires:

```csharp
public sealed class TimeoutBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        return await next(timeout.Token);
    }
}
```

An `OperationCanceledException` caused by the timeout can be recovered by an exception handler, because the caller's token was not canceled. See [Cancellation](/guide/exception-handling#cancellation).

## Pre-Processors

`IRequestPreProcessor<TRequest>` runs before the pipeline behavior chain. Pre-processors fire sequentially in registration order and cannot modify the response -- they only observe or validate the request.

### Interface

```csharp
public interface IRequestPreProcessor<TRequest>
    where TRequest : IBaseRequest
{
    Task Process(TRequest request, CancellationToken cancellationToken);
}
```

::: info
The constraint is `IBaseRequest`, not `IRequest<TResponse>`. Pre-processors apply to both request/response and void requests without needing the response type.
:::

### Request Logging Pre-Processor

```csharp
public sealed class RequestLoggingPreProcessor<TRequest> : IRequestPreProcessor<TRequest>
    where TRequest : IBaseRequest
{
    private readonly ILogger<RequestLoggingPreProcessor<TRequest>> _logger;

    public RequestLoggingPreProcessor(ILogger<RequestLoggingPreProcessor<TRequest>> logger)
    {
        _logger = logger;
    }

    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing {RequestType}: {@Request}", typeof(TRequest).Name, request);

        return Task.CompletedTask;
    }
}
```

### Throwing from a Pre-Processor

If a pre-processor throws, the pipeline behaviors and handler do not execute. The exception flows directly to exception handlers.

```csharp
public sealed class AuthorizationPreProcessor<TRequest> : IRequestPreProcessor<TRequest>
    where TRequest : IBaseRequest
{
    private readonly IAuthorizationService _authorizationService;

    public AuthorizationPreProcessor(IAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    public async Task Process(TRequest request, CancellationToken cancellationToken)
    {
        bool isAuthorized = await _authorizationService.IsAuthorizedAsync(cancellationToken);

        if (!isAuthorized)
        {
            throw new UnauthorizedAccessException("Request denied.");
        }
    }
}
```

## Post-Processors

`IRequestPostProcessor<TRequest, TResponse>` runs after the handler returns successfully. Post-processors receive both the request and the response and execute sequentially in registration order.

### Interface

```csharp
public interface IRequestPostProcessor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
```

### Audit Trail Post-Processor

```csharp
public sealed class AuditPostProcessor<TRequest, TResponse>
    : IRequestPostProcessor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IAuditLog _auditLog;

    public AuditPostProcessor(IAuditLog auditLog)
    {
        _auditLog = auditLog;
    }

    public async Task Process(
        TRequest request,
        TResponse response,
        CancellationToken cancellationToken)
    {
        await _auditLog.RecordAsync(
            typeof(TRequest).Name,
            request,
            response,
            cancellationToken);
    }
}
```

::: tip
Post-processors run inside the behavior chain, after the handler. If a behavior wraps `next()` in a try/catch, post-processors only run when the handler succeeds.
:::

## Stream Pipeline Behaviors

`IStreamPipelineBehavior<TRequest, TResponse>` provides the same wrapping pattern for `CreateStream(...)` calls. Instead of `RequestHandlerDelegate<TResponse>`, it receives a `StreamHandlerDelegate<TResponse>` that returns `IAsyncEnumerable<TResponse>`.

### Interface

```csharp
public interface IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

### Stream Logging Behavior

```csharp
public sealed class StreamLoggingBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    private readonly ILogger<StreamLoggingBehavior<TRequest, TResponse>> _logger;

    public StreamLoggingBehavior(ILogger<StreamLoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting stream for {RequestType}", typeof(TRequest).Name);
        int count = 0;

        await foreach (TResponse item in next().WithCancellation(cancellationToken))
        {
            count++;
            yield return item;
        }

        _logger.LogInformation(
            "Stream for {RequestType} completed with {Count} items",
            typeof(TRequest).Name,
            count);
    }
}
```

::: info
Stream pipelines also run `IRequestPreProcessor<TRequest>` before the behavior chain. Post-processors are not part of the stream pipeline.
:::

## Open-Generic Behaviors

Every example on this page uses open generics: `TRequest` and `TResponse` are unbound type parameters. The assembly scanner detects open-generic implementations and registers them as open-generic services. This means a single `LoggingBehavior<TRequest, TResponse>` class applies to every request type in your application.

To restrict a behavior to a specific request, close the type parameters:

```csharp
public sealed class GetUserCacheBehavior
    : IPipelineBehavior<GetUserQuery, UserDto>
{
    private readonly ICache _cache;

    public GetUserCacheBehavior(ICache cache)
    {
        _cache = cache;
    }

    public async Task<UserDto> Handle(
        GetUserQuery request,
        RequestHandlerDelegate<UserDto> next,
        CancellationToken cancellationToken)
    {
        UserDto? cached = await _cache.GetAsync<UserDto>(request.Id.ToString(), cancellationToken);

        if (cached is not null)
        {
            return cached;
        }

        UserDto response = await next();
        await _cache.SetAsync(request.Id.ToString(), response, cancellationToken);

        return response;
    }
}
```

This behavior only runs for `GetUserQuery` requests. All other requests skip it.

## Registration and Ordering

Pipeline components are discovered automatically by assembly scanning, or registered explicitly with `AddBehavior`, `AddOpenBehavior`, `AddStreamBehavior`, `AddOpenStreamBehavior`, `AddPreProcessor`, and `AddPostProcessor`. Multiple implementations of the same interface are allowed.

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssembly(typeof(LoggingBehavior<,>).Assembly);
});
```

### Execution Order

Behaviors execute in **registration order**. The first registered behavior wraps all subsequent behaviors, forming a nested chain:

- **Behavior A** (registered first) is the outermost wrapper
- **Behavior B** (registered second) is inside A
- **Handler** is the innermost call

Within one `AddMediator` call, behaviors added with `AddBehavior` and `AddOpenBehavior` come first, in the order you call them, followed by scanned behaviors in order of full type name.

To define order explicitly in `AddMediator`, register behaviors in sequence:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
    options.AddOpenBehavior(typeof(ValidationBehavior<,>));   // outermost
    options.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
    options.AddOpenBehavior(typeof(TimingBehavior<,>));       // innermost
});
```

If you prefer, you can still register manually after the scan:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<CreateOrderHandler>();
});

// Registered after the scan, so it runs inside the scanned behaviors
services.TryAddEnumerable(
    ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(TimingBehavior<,>)));
```

::: warning
`TryAddEnumerable` prevents duplicate registrations of the same implementation type. If the assembly scanner already registered a behavior, adding it again manually has no effect.
:::

## Behaviors vs. Processors

| | Pre/Post-Processor | Pipeline Behavior |
|---|---|---|
| **Wrapping** | No -- runs before or after the pipeline | Yes -- wraps the handler like middleware |
| **Access to response** | Post-processor only | Both before and after `next()` |
| **Short-circuit** | Throw an exception | Return without calling `next()` |
| **Modify response** | No | Yes -- can transform the response from `next()` |
| **Use case** | Logging, validation, audit | Caching, retry, transactions, timing |

Choose **pre/post-processors** for simple observe-and-continue logic. Choose **pipeline behaviors** when you need to wrap, transform, or conditionally skip the handler.
