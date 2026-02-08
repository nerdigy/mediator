# Recipe Catalog

Use these recipes when asking an LLM to generate code for common mediator workflows. Each recipe includes a prompt you can paste directly.

## 1) CQRS Query With Typed Response

**Use when:** You need read-only data retrieval with one handler.

```text
Create a CQRS query in .NET 10 using Nerdigy.Mediator:
- Query: GetOrderByIdQuery(Guid OrderId) : IRequest<OrderDto>
- Handler: IRequestHandler<GetOrderByIdQuery, OrderDto>
- Dependency: IOrderReadRepository
- Return NotFound-friendly response shape
- Include endpoint example using ISender
```

## 2) Command With Side Effects And Notification

**Use when:** You need a state change and then downstream fan-out.

```text
Add a command flow using Nerdigy.Mediator:
- Command: CreateOrderCommand : IRequest<CreateOrderResult>
- Handler saves order and publishes OrderCreated notification
- Add two notification handlers:
  1) audit log
  2) cache invalidation
- Ensure cancellation tokens are passed through all calls
```

## 3) Void Command

**Use when:** No response payload is needed.

```text
Create a void command using IRequest:
- Command: ArchiveOrderCommand(Guid OrderId) : IRequest
- Handler: IRequestHandler<ArchiveOrderCommand>
- Include endpoint showing await sender.Send(new ArchiveOrderCommand(...))
```

## 4) Open Generic Logging Behavior

**Use when:** You want request timing/telemetry around every request.

```text
Create an open generic logging behavior:
- class LoggingBehavior<TRequest,TResponse> : IPipelineBehavior<TRequest,TResponse>
- Log start/end and elapsed time
- Register with options.AddOpenBehavior(typeof(LoggingBehavior<,>))
- Show expected execution order relative to handler
```

## 5) Validation + Short-Circuit

**Use when:** You want to fail fast before handler logic.

```text
Implement request validation as IPipelineBehavior<TRequest,TResponse>:
- Run validators before next()
- On invalid request, return typed failure response without calling next()
- Show one request/response contract that supports validation errors
- Include DI registration and one unit test example
```

## 6) Stream Request For Incremental Results

**Use when:** You need progressive responses (large lists, live updates, imports).

```text
Implement streaming with Nerdigy.Mediator:
- Request: SearchOrdersStreamQuery(string Term) : IStreamRequest<OrderDto>
- Handler: IStreamRequestHandler<SearchOrdersStreamQuery, OrderDto>
- Yield results incrementally via IAsyncEnumerable<OrderDto>
- Respect cancellation during iteration
- Include caller example using await foreach over mediator.CreateStream(...)
```

## 7) Typed Exception Recovery

**Use when:** You need fallback responses for known failures.

```text
Add exception handling for GetOrderByIdQuery:
- Implement IRequestExceptionHandler<GetOrderByIdQuery, OrderDto, TimeoutException>
- Set fallback with state.SetHandled(...)
- Add IRequestExceptionAction<GetOrderByIdQuery, Exception> for telemetry
- Explain when actions run and when they are skipped
```

## 8) Notification Strategy Selection

**Use when:** You need to control sequential vs parallel notification execution.

```text
Update mediator registration to support notification strategy selection:
- Use options.UseNotificationPublisherStrategy(...)
- Show Sequential and Parallel examples
- Explain tradeoffs:
  - deterministic ordering
  - throughput
  - exception behavior
```

## 9) Multi-Assembly Registration

**Use when:** Messages and handlers live in separate projects.

```text
Show AddMediator configuration that scans multiple assemblies:
- Contracts assembly
- Application handlers assembly
- Infrastructure handlers assembly
Use RegisterServicesFromAssemblies(...)
Include a minimal solution structure example
```

## 10) LLM-Assisted Refactor To Mediator

**Use when:** You are migrating from service-to-service direct calls.

```text
Refactor this service layer to Nerdigy.Mediator:
- Convert each use case method into IRequest/IRequestHandler
- Extract cross-cutting concerns into IPipelineBehavior
- Preserve existing behavior and exception semantics
- Output a migration map: old method -> new request/handler
- Include a phased rollout plan with low risk steps
```

## Tip: Ask For Strict Output Shape

Append this to any recipe prompt:

```text
Output format:
1. File tree
2. Full code per file
3. Registration changes
4. Minimal tests
5. Validation checklist
```
