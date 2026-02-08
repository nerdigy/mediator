# Streaming

Nerdigy.Mediator supports async streaming through `IAsyncEnumerable<T>`, giving you a first-class way to yield items incrementally from a handler rather than building a complete response in memory. Call `CreateStream` on the mediator and consume results with `await foreach` -- items arrive as the handler produces them.

Streaming is the right choice when:

- **The result set is large or unbounded.** Query thousands of database rows without buffering them all into a list.
- **Items arrive over time.** Tail a log, subscribe to a message queue, or push real-time price updates.
- **The consumer controls the pace.** Back-pressure is built into `IAsyncEnumerable<T>` -- the handler only advances when the consumer asks for the next item.

## Defining a Stream Request

A stream request implements `IStreamRequest<TResponse>`, where `TResponse` is the type of each yielded item -- not a collection.

```csharp
public sealed record GetOrdersQuery(Guid CustomerId) : IStreamRequest<OrderDto>;
```

This declares a request that streams `OrderDto` items one at a time.

## Implementing a Stream Handler

A stream handler implements `IStreamRequestHandler<TRequest, TResponse>` and returns `IAsyncEnumerable<TResponse>`. Use `yield return` to emit items.

```csharp
public sealed class GetOrdersHandler
    : IStreamRequestHandler<GetOrdersQuery, OrderDto>
{
    private readonly OrderRepository _repository;

    public GetOrdersHandler(OrderRepository repository)
    {
        _repository = repository;
    }

    public async IAsyncEnumerable<OrderDto> Handle(
        GetOrdersQuery request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (OrderDto order in _repository.GetOrdersAsync(request.CustomerId, cancellationToken))
        {
            yield return order;
        }
    }
}
```

::: tip
Apply the `[EnumeratorCancellation]` attribute to the `cancellationToken` parameter. The compiler needs it to wire up cancellation when the caller uses `WithCancellation` on the resulting `IAsyncEnumerable<T>`.
:::

Each stream request type has exactly one handler. The assembly scanner registers handlers with `TryAdd`, so duplicate registrations for the same request type are ignored. A missing handler throws `InvalidOperationException` at dispatch time.

## Consuming a Stream

Call `CreateStream` on `IMediator` (or `ISender`) and iterate the result with `await foreach`.

```csharp
IAsyncEnumerable<OrderDto> stream = mediator.CreateStream(
    new GetOrdersQuery(customerId),
    cancellationToken);

await foreach (OrderDto order in stream.WithCancellation(cancellationToken))
{
    Console.WriteLine($"Order {order.Id}: {order.Total:C}");
}
```

The `CreateStream` method signature:

```csharp
IAsyncEnumerable<TResponse> CreateStream<TResponse>(
    IStreamRequest<TResponse> request,
    CancellationToken cancellationToken = default);
```

`CreateStream` returns immediately. Pipeline execution, handler resolution, and item enumeration all happen lazily when iteration begins.

## Cancellation

Two cancellation tokens can be in play:

| Token | Source | Purpose |
|---|---|---|
| Request token | Passed to `CreateStream(..., token)` | Cancels pipeline setup and handler execution |
| Enumeration token | Passed via `.WithCancellation(token)` | Cancels during `await foreach` iteration |

If both tokens are cancelable, the runtime links them into a single token forwarded to the handler. If only one is cancelable, that token is used directly.

```csharp
using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

IAsyncEnumerable<OrderDto> stream = mediator.CreateStream(
    new GetOrdersQuery(customerId),
    timeout.Token);

await foreach (OrderDto order in stream)
{
    // timeout.Token cancels both pipeline setup and enumeration
}
```

## Stream Pipeline

Stream requests run through their own pipeline, separate from the request/response pipeline used by `Send`.

**Execution order:**

1. **Pre-processors** -- `IRequestPreProcessor<TRequest>` instances run in registration order
2. **Stream pipeline behaviors** -- `IStreamPipelineBehavior<TRequest, TResponse>` instances wrap the handler, outer-to-inner
3. **Stream handler** -- the `IStreamRequestHandler<TRequest, TResponse>` that yields items

::: info
Stream requests share the same `IRequestPreProcessor<TRequest>` interface used by regular requests. Pre-processors run once before the stream begins, not per item.
:::

### Stream Pipeline Behaviors

`IStreamPipelineBehavior<TRequest, TResponse>` is the streaming equivalent of `IPipelineBehavior<TRequest, TResponse>`. Each behavior receives the request, a `StreamHandlerDelegate<TResponse>` pointing to the next step, and a cancellation token. It returns `IAsyncEnumerable<TResponse>`.

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

        _logger.LogInformation("Stream completed for {RequestType} with {Count} items",
            typeof(TRequest).Name, count);
    }
}
```

The `StreamHandlerDelegate<TResponse>` type is defined as:

```csharp
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>();
```

Call `next()` to get the inner stream, then iterate and yield its items. You can transform, filter, or augment items as they pass through.

### Short-Circuiting

A behavior can skip the handler entirely by returning its own stream without calling `next()`.

```csharp
public sealed class CachedStreamBehavior<TRequest, TResponse>
    : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    private readonly IStreamCache<TRequest, TResponse> _cache;

    public CachedStreamBehavior(IStreamCache<TRequest, TResponse> cache)
    {
        _cache = cache;
    }

    public IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGet(request, out IAsyncEnumerable<TResponse>? cached))
        {
            return cached;
        }

        return next();
    }
}
```

When a behavior returns without calling `next()`, all inner behaviors and the handler are bypassed.

## Exception Handling

Stream exception handling covers failures that occur during pipeline setup (pre-processors, behavior construction) and during item enumeration (handler `yield` failures). The runtime catches exceptions at both stages and routes them through the same exception processing chain.

### Stream Exception Handlers

Implement `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` to intercept a specific exception type and supply a replacement stream.

```csharp
public sealed class OrderStreamFallbackHandler
    : IStreamRequestExceptionHandler<GetOrdersQuery, OrderDto, HttpRequestException>
{
    private readonly OrderCacheService _cache;

    public OrderStreamFallbackHandler(OrderCacheService cache)
    {
        _cache = cache;
    }

    public Task Handle(
        GetOrdersQuery request,
        HttpRequestException exception,
        StreamRequestExceptionHandlerState<OrderDto> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(_cache.GetCachedOrdersAsync(request.CustomerId, cancellationToken));

        return Task.CompletedTask;
    }
}
```

Call `state.SetHandled(replacementStream)` to mark the exception as handled and provide a fallback `IAsyncEnumerable<TResponse>`. The runtime switches to the replacement stream and continues enumeration. The original exception does not propagate.

The `StreamRequestExceptionHandlerState<TResponse>` class exposes:

- **`Handled`** -- whether a handler has marked the exception as handled
- **`ResponseStream`** -- the replacement stream set by `SetHandled`
- **`SetHandled(IAsyncEnumerable<TResponse>)`** -- marks the exception handled and supplies the replacement

### Exception Actions

`IRequestExceptionAction<TRequest, TException>` runs side-effect logic (logging, metrics, alerting) when no exception handler marks the exception as handled. After all actions execute, the runtime rethrows the original exception with preserved stack trace.

```csharp
public sealed class StreamErrorMetrics
    : IRequestExceptionAction<GetOrdersQuery, Exception>
{
    private readonly IMetricsCollector _metrics;

    public StreamErrorMetrics(IMetricsCollector metrics)
    {
        _metrics = metrics;
    }

    public Task Execute(
        GetOrdersQuery request,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _metrics.IncrementCounter("stream_errors", tags: new { request_type = nameof(GetOrdersQuery) });

        return Task.CompletedTask;
    }
}
```

::: warning
Exception actions cannot prevent the exception from propagating. To recover from an exception, use `IStreamRequestExceptionHandler` and call `state.SetHandled(...)`.
:::

### Exception Resolution Order

The runtime evaluates exception handlers and actions from the most specific exception type to the least specific, walking up the type hierarchy. For example, if a handler throws `HttpRequestException`, the runtime checks handlers registered for `HttpRequestException` first, then `IOException`, then `Exception`.

The first handler that calls `state.SetHandled(...)` wins. Remaining handlers are not invoked.

## Practical Examples

### Streaming Database Results

```csharp
public sealed record SearchProductsQuery(string Term, int MaxResults) : IStreamRequest<ProductDto>;

public sealed class SearchProductsHandler
    : IStreamRequestHandler<SearchProductsQuery, ProductDto>
{
    private readonly AppDbContext _db;

    public SearchProductsHandler(AppDbContext db)
    {
        _db = db;
    }

    public async IAsyncEnumerable<ProductDto> Handle(
        SearchProductsQuery request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (Product product in _db.Products
            .Where(p => p.Name.Contains(request.Term))
            .Take(request.MaxResults)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            yield return new ProductDto(product.Id, product.Name, product.Price);
        }
    }
}
```

### Real-Time Event Feed

```csharp
public sealed record SubscribeToEventsRequest(string Channel) : IStreamRequest<EventMessage>;

public sealed class EventFeedHandler
    : IStreamRequestHandler<SubscribeToEventsRequest, EventMessage>
{
    private readonly IEventBus _eventBus;

    public EventFeedHandler(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public async IAsyncEnumerable<EventMessage> Handle(
        SubscribeToEventsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (EventMessage message in _eventBus
            .SubscribeAsync(request.Channel, cancellationToken))
        {
            yield return message;
        }
    }
}
```

### Progress Reporting

```csharp
public sealed record ImportDataRequest(string FilePath) : IStreamRequest<ImportProgress>;

public sealed record ImportProgress(int ProcessedRows, int TotalRows, bool IsComplete);

public sealed class ImportDataHandler
    : IStreamRequestHandler<ImportDataRequest, ImportProgress>
{
    public async IAsyncEnumerable<ImportProgress> Handle(
        ImportDataRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync(request.FilePath, cancellationToken);
        int total = lines.Length;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessLineAsync(lines[i], cancellationToken);

            if ((i + 1) % 100 == 0 || i == total - 1)
            {
                yield return new ImportProgress(i + 1, total, i == total - 1);
            }
        }
    }

    private static Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        // Process the line
        return Task.CompletedTask;
    }
}
```
