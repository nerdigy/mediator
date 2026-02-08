# LLM Quickstart

This page is optimized for copy/paste use with coding assistants. The goal is to get to a correct first draft with minimal back and forth.

## Copy This Context Into Your Prompt

Use this block as the first message to your LLM before asking for code:

```text
You are implementing mediator-based application code using Nerdigy.Mediator on .NET 10.

Use these packages:
- Nerdigy.Mediator.Abstractions (contracts)
- Nerdigy.Mediator.DependencyInjection (DI registration and scanning)

Use these namespaces:
- Nerdigy.Mediator.Abstractions
- Nerdigy.Mediator.DependencyInjection

Rules:
- Requests: one handler per request type.
- Notifications: zero to many handlers.
- Streams: use IStreamRequest<T> and IStreamRequestHandler<TRequest, T>.
- Respect CancellationToken in every handler.
- Use file-scoped namespaces and public sealed types unless extensibility is required.
- Return Task.FromResult/Task.CompletedTask when no async I/O is performed.
- Keep examples production-ready, compilable, and minimal.
```

## One-Shot Bootstrap Prompt

Use this prompt to scaffold a full vertical slice:

```text
Create a minimal ASP.NET Core endpoint using Nerdigy.Mediator for:
1. A query IRequest<TResponse> and IRequestHandler<TRequest,TResponse>
2. A command IRequest and IRequestHandler<TRequest>
3. A domain event INotification and two INotificationHandler<T>
4. Registration with services.AddMediator(options => options.RegisterServicesFromAssemblyContaining<...>())

Output:
- Complete code for Program.cs
- Message and handler types
- Exact package install commands
- A short "how to test" section with sample HTTP requests
```

## Targeted Prompt Templates

### Add Request/Response

```text
Add a new request/response pair to this project:
- Request name: {{RequestName}}
- Response type: {{ResponseType}}
- Handler dependencies: {{Dependencies}}
- Validation rules: {{Rules}}

Generate:
- IRequest<TResponse> record
- IRequestHandler<TRequest,TResponse> implementation
- Example usage via ISender.Send
```

### Add Notification Fan-Out

```text
Add notification fan-out for {{EventName}} using Nerdigy.Mediator:
- Define INotification event
- Add at least two handlers
- Show where Publish is called in the existing request handler
- Include guidance on Sequential vs Parallel publisher strategy
```

### Add Streaming Endpoint

```text
Implement server-side streaming using Nerdigy.Mediator:
- Define IStreamRequest<T>
- Implement IStreamRequestHandler<TRequest,T>
- Show cancellation propagation
- Add a minimal endpoint that streams results from CreateStream
```

### Add Validation Behavior

```text
Add a reusable open generic pipeline behavior:
- Type: ValidationBehavior<TRequest,TResponse> : IPipelineBehavior<TRequest,TResponse>
- Run before handler and short-circuit with a typed error when validation fails
- Register it with AddOpenBehavior(typeof(ValidationBehavior<,>))
- Show one request that passes and one that fails
```

### Add Exception Recovery

```text
Add typed exception recovery for request {{RequestName}}:
- Implement IRequestExceptionHandler<TRequest,TResponse,TException>
- Use state.SetHandled(...) to return fallback response
- Also add IRequestExceptionAction<TRequest,TException> for logging when unhandled
- Show expected behavior for handled vs unhandled exceptions
```

## Output Contract You Can Require

When quality matters, append this to your prompt:

```text
Before final output:
1. Verify type signatures match Nerdigy.Mediator contracts.
2. Verify code compiles conceptually for .NET 10.
3. Include all using statements.
4. Include exactly one concise explanation per file.
5. Include a final checklist of assumptions.
```

## Fast Validation Commands

```bash
dotnet restore
dotnet build Mediator.slnx -c Release
dotnet test Mediator.slnx -c Release
dotnet format Mediator.slnx --verify-no-changes
```

## Related Pages

- [Decision Matrix](/guide/decision-matrix)
- [Recipe Catalog](/guide/recipes)
- [Troubleshooting](/guide/troubleshooting)
- [API Cheatsheet](/api/cheatsheet)
