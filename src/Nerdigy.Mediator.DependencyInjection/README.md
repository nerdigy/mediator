# Nerdigy.Mediator.DependencyInjection

`Microsoft.Extensions.DependencyInjection` registration for [Nerdigy.Mediator](https://github.com/nerdigy/mediator). Includes the runtime and contracts.

```bash
dotnet add package Nerdigy.Mediator.DependencyInjection
```

Scan assemblies for handlers and pipeline components:

```csharp
using Nerdigy.Mediator.DependencyInjection;

services.AddMediator(options =>
{
    options.RegisterServicesFromAssemblyContaining<Program>();
    options.AddOpenBehavior(typeof(LoggingBehavior<,>));
});
```

Or register them explicitly, which is required for trimmed and Native AOT apps:

```csharp
services.AddMediator(options => options
    .AddRequestHandler<GetUserQuery, UserDto, GetUserQueryHandler>()
    .AddNotificationHandler<UserCreated, SendWelcomeEmailHandler>()
    .AddBehavior<GetUserQuery, UserDto, CachingBehavior>());
```

`AddMediator` validates registrations at startup: duplicate handlers, components that could never run, scanned types that fail to load, and a singleton mediator with scoped components all fail with a message that explains the fix.

Then resolve `IMediator`, `ISender`, or `IPublisher` from a scope.

Documentation: [dependency injection](https://github.com/nerdigy/mediator/blob/main/docs/guide/dependency-injection.md) · [Native AOT](https://github.com/nerdigy/mediator/blob/main/docs/guide/native-aot.md) · [changelog](https://github.com/nerdigy/mediator/blob/main/CHANGELOG.md)
