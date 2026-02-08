# Notifications

Notifications let a single event reach multiple handlers. Unlike requests, which map one-to-one with a handler, a notification fans out to every registered `INotificationHandler<T>` -- zero, one, or many. This makes notifications the right tool for domain events, audit logging, cache invalidation, and any side-effect that should not couple the publisher to the consumer.

## Defining a Notification

A notification is any type that implements `INotification`. Records work well because notifications are immutable messages.

```csharp
public sealed record OrderPlaced(Guid OrderId, decimal Total) : INotification;
```

## Implementing Handlers

Each handler implements `INotificationHandler<TNotification>`. Nerdigy.Mediator supports multiple handlers per notification type -- the assembly scanner registers them all automatically via `TryAddEnumerable`.

```csharp
public sealed class OrderAuditHandler : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        // Write to audit log
        return Task.CompletedTask;
    }
}

public sealed class OrderConfirmationEmailHandler : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        // Send confirmation email
        return Task.CompletedTask;
    }
}

public sealed class InventoryReservationHandler : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        // Reserve inventory
        return Task.CompletedTask;
    }
}
```

All three handlers run when `OrderPlaced` is published. No registration beyond assembly scanning is required.

## Publishing a Notification

Call `Publish` on `IMediator` or `IPublisher`:

```csharp
await mediator.Publish(new OrderPlaced(orderId, 149.99m), cancellationToken);
```

If no handlers are registered for the notification type, `Publish` completes successfully and does nothing. This is by design -- the publisher does not need to know whether anyone is listening.

## Publishing Strategies

The mediator delegates handler invocation to an `INotificationPublisher` strategy. Two built-in strategies ship with the library, and you can implement your own.

### Sequential (Default)

`ForeachAwaitPublisher` awaits each handler one at a time, in registration order.

```
Handler 1 ──────> Handler 2 ──────> Handler 3 ──────> done
```

**Use when:**

- Handlers share state or resources that are not thread-safe
- Execution order matters (e.g., audit must complete before email sends)
- You need predictable, deterministic exception behavior -- the first handler to throw stops subsequent handlers

### Parallel

`TaskWhenAllPublisher` starts all handlers concurrently and awaits `Task.WhenAll`.

```
Handler 1 ──────────> ┐
Handler 2 ────>       ├──> done
Handler 3 ──────>     ┘
```

**Use when:**

- Handlers are independent and thread-safe
- Latency matters more than ordering -- total wall-clock time equals the slowest handler, not the sum
- You accept that all handlers start before any exception surfaces (exceptions are aggregated into an `AggregateException`)

::: tip
`TaskWhenAllPublisher` includes fast paths: zero handlers return `Task.CompletedTask` immediately, and a single handler runs without allocating a `Task[]` array.
:::

### Configuring the Strategy

Select a built-in strategy in `AddMediator`:

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<OrderAuditHandler>();

    // Sequential (default -- no call needed, shown for clarity)
    options.UseNotificationPublisherStrategy(
        NerdigyMediatorNotificationPublisherStrategy.Sequential);

    // Or parallel
    options.UseNotificationPublisherStrategy(
        NerdigyMediatorNotificationPublisherStrategy.Parallel);
});
```

The strategy applies globally to all notification types. If you need per-notification control, implement a custom publisher.

## Custom Publishers

Implement `INotificationPublisher` to control exactly how handlers are invoked. The interface receives the resolved handlers, the notification, and a cancellation token:

```csharp
public sealed class ResilientPublisher : INotificationPublisher
{
    public async Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification
    {
        foreach (INotificationHandler<TNotification> handler in handlers)
        {
            try
            {
                await handler.Handle(notification, cancellationToken);
            }
            catch (Exception)
            {
                // Log and continue -- don't let one handler failure
                // prevent the remaining handlers from executing.
            }
        }
    }
}
```

Register a custom publisher by type or by instance:

```csharp
// By type -- resolved from the container
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<OrderAuditHandler>();
    options.UseNotificationPublisher<ResilientPublisher>();
});

// By instance -- registered as a singleton
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<OrderAuditHandler>();
    options.UseNotificationPublisher(new ResilientPublisher());
});
```

## Practical Patterns

### Domain Events

Publish notifications after a state change to decouple the write path from downstream reactions:

```csharp
public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrderCommand, OrderConfirmation>
{
    private readonly IPublisher _publisher;

    public PlaceOrderHandler(IPublisher publisher)
    {
        _publisher = publisher;
    }

    public async Task<OrderConfirmation> Handle(
        PlaceOrderCommand request,
        CancellationToken cancellationToken)
    {
        // Persist the order...
        OrderConfirmation confirmation = new(Guid.NewGuid());

        await _publisher.Publish(
            new OrderPlaced(confirmation.OrderId, request.Total),
            cancellationToken);

        return confirmation;
    }
}
```

### Cache Invalidation

Invalidate cached data in response to write operations without coupling the write handler to the cache:

```csharp
public sealed class ProductCacheInvalidationHandler : INotificationHandler<ProductUpdated>
{
    private readonly IDistributedCache _cache;

    public ProductCacheInvalidationHandler(IDistributedCache cache)
    {
        _cache = cache;
    }

    public Task Handle(ProductUpdated notification, CancellationToken cancellationToken)
    {
        return _cache.RemoveAsync($"product:{notification.ProductId}", cancellationToken);
    }
}
```

## Behavior Notes

- **Zero handlers is valid.** `Publish` completes successfully when no handlers are registered.
- **Cancellation tokens propagate.** The `CancellationToken` passed to `Publish` is forwarded to every handler.
- **No pipeline behaviors.** Notifications do not pass through `IPipelineBehavior<,>`, pre-processors, or post-processors. Each handler receives the notification directly from the publisher strategy.
- **Handler registration is additive.** The assembly scanner uses `TryAddEnumerable`, so the same handler type is only registered once per notification type, but multiple distinct handler types are all registered.
