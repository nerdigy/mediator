# Nerdigy.Mediator.Contracts

Contracts for [Nerdigy.Mediator](https://github.com/nerdigy/mediator): requests, notifications, stream requests, their handlers, pipeline behaviors, processors, and exception components.

Reference this package from projects that only define messages and handlers, so they do not depend on the runtime or a DI container. Applications reference `Nerdigy.Mediator.DependencyInjection`, which includes it.

```bash
dotnet add package Nerdigy.Mediator.Contracts
```

```csharp
using Nerdigy.Mediator.Contracts;

public sealed record GetUserQuery(Guid Id) : IRequest<UserDto>;

public sealed class GetUserQueryHandler : IRequestHandler<GetUserQuery, UserDto>
{
    public Task<UserDto> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new UserDto(request.Id, "Ada"));
    }
}

public sealed record UserDto(Guid Id, string Name);
```

Documentation: [contracts reference](https://github.com/nerdigy/mediator/blob/main/docs/api/contracts.md) · [changelog](https://github.com/nerdigy/mediator/blob/main/CHANGELOG.md)
