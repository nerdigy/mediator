# Requests

Requests are the primary dispatch mechanism in Nerdigy.Mediator. Each request routes to exactly one handler and optionally returns a response. This makes requests ideal for modeling both queries (read operations that return data) and commands (write operations that may or may not return a result).

## Defining a Request

A request is any type that implements `IRequest<TResponse>`. C# records work well here because requests are typically immutable data carriers.

```csharp
using Nerdigy.Mediator.Abstractions;

public sealed record GetUserQuery(Guid Id) : IRequest<UserDto>;
```

`GetUserQuery` carries a single `Guid` and declares that its handler will return a `UserDto`.

## Implementing a Handler

Every request needs exactly one handler. Implement `IRequestHandler<TRequest, TResponse>` and place your logic in the `Handle` method.

```csharp
public sealed class GetUserHandler : IRequestHandler<GetUserQuery, UserDto>
{
    private readonly IUserRepository _repository;

    public GetUserHandler(IUserRepository repository)
    {
        _repository = repository;
    }

    public async Task<UserDto> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        User user = await _repository.GetByIdAsync(request.Id, cancellationToken);

        return new UserDto(user.Id, user.Name);
    }
}
```

Handlers are resolved from the DI container, so constructor injection works as expected. The `CancellationToken` passed to `Send` flows through the entire pipeline and into your handler.

## Sending a Request

Inject `ISender` or `IMediator` and call `Send`. The generic type parameter is inferred from the request type.

```csharp
public sealed class UsersController
{
    private readonly ISender _sender;

    public UsersController(ISender sender)
    {
        _sender = sender;
    }

    public async Task<UserDto> GetUser(Guid id, CancellationToken cancellationToken)
    {
        UserDto user = await _sender.Send(new GetUserQuery(id), cancellationToken);

        return user;
    }
}
```

`IMediator` extends both `ISender` and `IPublisher`. When your code only sends requests, depend on `ISender` to keep the dependency narrow.

## Void Requests

Not every request needs a return value. Commands that create, update, or delete resources often have nothing meaningful to return. Implement `IRequest` (without a type parameter) for these cases.

```csharp
public sealed record DeleteUserCommand(Guid Id) : IRequest;
```

The void handler interface mirrors the response-bearing version but drops the response type parameter. Its `Handle` method returns `Task` instead of `Task<TResponse>`.

```csharp
public sealed class DeleteUserHandler : IRequestHandler<DeleteUserCommand>
{
    private readonly IUserRepository _repository;

    public DeleteUserHandler(IUserRepository repository)
    {
        _repository = repository;
    }

    public async Task Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(request.Id, cancellationToken);
    }
}
```

Send a void request with the non-generic `Send` overload on `ISender`.

```csharp
await sender.Send(new DeleteUserCommand(userId), cancellationToken);
```

::: info How void requests work internally
`IRequest` extends `IRequest<Unit>`, where `Unit` is a zero-size struct that represents the absence of a value. The runtime bridges between the void handler and the response-bearing pipeline so that void requests participate in the same pre-processors, behaviors, post-processors, and exception handling as response-bearing requests. You never interact with `Unit` directly.
:::

## One Handler Per Request

Nerdigy.Mediator enforces a single-handler-per-request-type constraint. The assembly scanner registers handlers with `TryAdd`, which means the first handler discovered for a given request type wins. If no handler is registered, calling `Send` throws an `InvalidOperationException` with a diagnostic message that names the missing handler type.

This is by design. Requests model point-to-point dispatch -- one request, one handler, one response. If you need multiple recipients for the same message, use [notifications](/guide/notifications) instead.

## Practical Patterns

### Queries and Commands

A common convention is to suffix query request types with `Query` and command request types with `Command`. This is purely organizational -- the runtime treats them identically.

```csharp
// Query: reads data and returns a result
public sealed record GetOrderQuery(Guid OrderId) : IRequest<OrderDto>;

// Command with result: mutates state and returns the new resource
public sealed record PlaceOrderCommand(string Product, int Quantity) : IRequest<OrderConfirmation>;

// Command without result: mutates state, returns nothing
public sealed record CancelOrderCommand(Guid OrderId) : IRequest;
```

### Constructor-Injected Dependencies

Handlers are resolved from the DI container on every `Send` call. Inject services such as repositories, loggers, or HTTP clients through the constructor.

```csharp
public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrderCommand, OrderConfirmation>
{
    private readonly IOrderService _orderService;
    private readonly ILogger<PlaceOrderHandler> _logger;

    public PlaceOrderHandler(IOrderService orderService, ILogger<PlaceOrderHandler> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    public async Task<OrderConfirmation> Handle(
        PlaceOrderCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Placing order for {Product}", request.Product);

        OrderConfirmation confirmation = await _orderService.PlaceAsync(
            request.Product, request.Quantity, cancellationToken);

        return confirmation;
    }
}
```

## Dispatch Behavior

- **Missing handler.** Throws `InvalidOperationException` with a message identifying the unregistered request and handler types.
- **Cancellation.** The `CancellationToken` passed to `Send` propagates to every pipeline component and the handler itself.
- **Cached dispatch.** The runtime compiles dispatch delegates per concrete request type using expression trees and caches them in a `ConcurrentDictionary`. The first call for a given request type incurs a one-time compilation cost. Subsequent calls dispatch with zero reflection overhead.
- **Pipeline integration.** Every request -- including void requests -- flows through the full pipeline: pre-processors, pipeline behaviors, the handler, and post-processors. See the [Pipelines](/guide/pipelines) guide for details.
