# Decision Matrix

Use this matrix to quickly choose the right contract and dispatch call.

## Message Type Matrix

| You Need | Define Contract As | Implement Handler | Call | Handler Count |
|---|---|---|---|---|
| One response value | `IRequest<TResponse>` | `IRequestHandler<TRequest, TResponse>` | `Send(...)` | Exactly one |
| No response value | `IRequest` | `IRequestHandler<TRequest>` | `Send(...)` | Exactly one |
| Fan-out event | `INotification` | `INotificationHandler<TNotification>` | `Publish(...)` | Zero to many |
| Async stream of values | `IStreamRequest<TResponse>` | `IStreamRequestHandler<TRequest, TResponse>` | `CreateStream(...)` | Exactly one |

## Cross-Cutting Matrix

| Concern | Extension Point | Applies To | Can Short-Circuit |
|---|---|---|---|
| Validation before handling | `IRequestPreProcessor<TRequest>` | Requests and streams (`IBaseRequest`) | No |
| Around request execution | `IPipelineBehavior<TRequest, TResponse>` | Request/response and void requests | Yes |
| Around stream execution | `IStreamPipelineBehavior<TRequest, TResponse>` | Stream requests | Yes |
| Post-handle side effects | `IRequestPostProcessor<TRequest, TResponse>` | Request/response and void requests | No |
| Recover from known exceptions | `IRequestExceptionHandler<TRequest, TResponse, TException>` | Request/response and void requests | Yes (`SetHandled`) |
| Side effects on unhandled exceptions | `IRequestExceptionAction<TRequest, TException>` | Request/response and void requests | No |
| Recover from stream exceptions | `IStreamRequestExceptionHandler<TRequest, TResponse, TException>` | Stream requests | Yes (`SetHandled`) |

## Notification Delivery Matrix

| Strategy | Configure | Best For | Tradeoff |
|---|---|---|---|
| Sequential | `UseNotificationPublisherStrategy(Sequential)` | Deterministic order and simpler tracing | Lower throughput |
| Parallel | `UseNotificationPublisherStrategy(Parallel)` | Faster fan-out when handlers are independent | Concurrency and aggregate exception handling |

## Package Selection Matrix

| Project Type | Package |
|---|---|
| Contracts-only shared library | `Nerdigy.Mediator.Abstractions` |
| Application runtime without DI helper | `Nerdigy.Mediator` + `Nerdigy.Mediator.Abstractions` |
| Typical app with `IServiceCollection` | `Nerdigy.Mediator.DependencyInjection` (transitively includes runtime + abstractions) |

## LLM Prompt Helper

Use this decision stub in prompts:

```text
Pick mediator contract using these rules:
- If exactly one response is needed: IRequest<TResponse> + Send
- If no response is needed: IRequest + Send
- If one-to-many event fan-out is needed: INotification + Publish
- If progressive results are needed: IStreamRequest<TResponse> + CreateStream
```

## Registration Quick Reference

```csharp
services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<Program>();
    options.UseNotificationPublisherStrategy(
        NerdigyMediatorNotificationPublisherStrategy.Sequential);
    options.AddOpenBehavior(typeof(LoggingBehavior<,>));
});
```
