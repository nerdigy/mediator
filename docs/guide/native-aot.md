# Native AOT

Nerdigy.Mediator supports trimmed and Native AOT apps. All three packages are annotated for trimming and AOT and are analyzer-clean, and with explicit registration a Native AOT app publishes without trim or AOT warnings.

## What Works

With explicit registration, everything the mediator dispatches works under Native AOT:

- Requests with class or struct request types, and class or value-type responses
- Void requests, through `Send(IRequest)` and `Send<Unit>`
- Stream requests
- Notifications, including struct notifications published through `INotification`
- Pipeline behaviors, pre-processors, and post-processors
- Exception handlers and actions, including the walk up the exception type hierarchy
- Tracing and metrics

Assembly scanning does not work under Native AOT, and no registration source generator is provided.

## Register Components Explicitly

Replace scanning with the explicit registration methods on `MediatorOptions`:

```csharp
services.AddMediator(options => options
    .AddRequestHandler<GetUserQuery, UserDto, GetUserQueryHandler>()
    .AddRequestHandler<DeleteUserCommand, DeleteUserCommandHandler>()
    .AddStreamRequestHandler<GetOrdersStream, OrderDto, GetOrdersStreamHandler>()
    .AddNotificationHandler<UserCreatedNotification, SendWelcomeEmailHandler>()
    .AddBehavior<GetUserQuery, UserDto, CachingBehavior>()
    .AddExceptionHandler<GetUserQuery, UserDto, NotFoundException, NotFoundFallback>());
```

The mediator dispatches on a message's runtime type. Each `AddRequestHandler`, `AddStreamRequestHandler`, and `AddNotificationHandler` call also registers that message type's dispatch code ahead of time, so dispatching it does not need runtime code generation. The type parameters are annotated so the trimmer keeps each component's constructor.

See [Dependency Injection](/guide/dependency-injection#explicit-registration) for every registration method.

### Why Scanning Is Not Supported

`RegisterServicesFromAssembly`, `RegisterServicesFromAssemblies`, `RegisterServicesFromAssemblyContaining<T>`, and `AddMediator(params Assembly[])` discover components with reflection, which trimming can remove. They are marked `[RequiresUnreferencedCode]`, so calling them in a trimmed or AOT project produces an analyzer warning at the call site.

### Message Types That Were Not Registered

A message type whose dispatch code was not registered, for example because its handler was added with `services.AddTransient<IRequestHandler<...>, ...>()`, fails on first dispatch under Native AOT:

```
System.InvalidOperationException:
Message type 'MyApp.GetOrderQuery' cannot be dispatched because runtime code generation
is not available (for example, under Native AOT) and its dispatch code was not registered.
Register its handler with options.AddRequestHandler<GetOrderQuery, OrderDto, THandler>()
in AddMediator(options => ...), or call the matching MediatorRegistry method at startup.
```

Under the JIT, the same message type is dispatched normally; its dispatch code is created on first use.

## Open Generic Components

Microsoft.Extensions.DependencyInjection cannot close an open generic over a value type without runtime code generation. Under Native AOT, resolving an open generic component, such as one added with `AddOpenBehavior` or a scanned open generic behavior, throws when any type argument is a value type:

- a struct request type,
- a value-type response such as `int` or `bool`,
- `Unit`, the response type of every void request.

This is a limitation of the container, not of the mediator, and it applies to every component interface. Under Native AOT:

- Use open behaviors only in apps whose requests and responses are all classes.
- Otherwise register closed components for each request: `AddBehavior<GetUserQuery, UserDto, LoggingBehavior<GetUserQuery, UserDto>>()`. A generic class registered closed works for any type arguments.

```csharp
services.AddMediator(options => options
    .AddRequestHandler<DeleteUserCommand, DeleteUserCommandHandler>()
    // Void request: Unit is a value type, so register the generic behavior closed.
    .AddBehavior<DeleteUserCommand, Unit, LoggingBehavior<DeleteUserCommand, Unit>>());
```

## Constructing the Mediator Yourself

Without the DI package, register the dispatch code for each message type with `MediatorRegistry` at startup:

```csharp
MediatorRegistry.RegisterRequest<GetUserQuery, UserDto>();
MediatorRegistry.RegisterVoidRequest<DeleteUserCommand>();
MediatorRegistry.RegisterStreamRequest<GetOrdersStream, OrderDto>();
MediatorRegistry.RegisterNotification<UserCreatedNotification>(); // only for publishing through INotification

var mediator = new Mediator(serviceProvider, new ForeachAwaitPublisher());
```

Registration is idempotent and applies to every mediator in the process. `RegisterNotification` is only needed when the notification is published through a less specific static type, such as `INotification`; publishing through the notification's own type needs no registration.

## Smoke Test

The repository includes a Native AOT smoke test, `test/Nerdigy.Mediator.AotSmokeTests`, which CI publishes and runs on every pull request. It registers every kind of component explicitly, including struct requests, a struct notification published through `INotification`, an exception hierarchy, and an open behavior in a container with only class types. It prints `PASS` or `FAIL` for each check and exits with the number of failures.

```bash
dotnet publish test/Nerdigy.Mediator.AotSmokeTests/Nerdigy.Mediator.AotSmokeTests.csproj -c Release -r linux-x64 -o artifacts/aot
./artifacts/aot/Nerdigy.Mediator.AotSmokeTests
```

Because every project builds with warnings as errors, a trim or AOT warning fails the publish.
