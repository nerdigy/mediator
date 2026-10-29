using System.Diagnostics;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator;
using Nerdigy.Mediator.Contracts;
using Nerdigy.Mediator.DependencyInjection;

// Publish with Native AOT and run: every check prints PASS or FAIL, and the exit code is the number of failures.
// The checks cover the shapes that need statically registered dispatch code: struct requests, interface-typed struct
// notifications, value-type responses, exception hierarchies, open behaviors, streams, and telemetry.
using var listener = new ActivityListener
{
    ShouldListenTo = static source => source.Name == MediatorTelemetry.ActivitySourceName,
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(listener);

var services = new ServiceCollection();
services.AddSingleton<Log>();
services.AddMediator(options => options
    .AddRequestHandler<ClassQuery, string, ClassQueryHandler>()
    .AddRequestHandler<StructQuery, int, StructQueryHandler>()
    .AddRequestHandler<VoidCommand, VoidCommandHandler>()
    .AddRequestHandler<FailingQuery, int, FailingQueryHandler>()
    .AddExceptionHandler<FailingQuery, int, Exception, FailingQueryFallback>()
    .AddExceptionAction<FailingQuery, Exception, FailingQueryAction>()
    .AddStreamRequestHandler<CountStream, int, CountStreamHandler>()
    .AddNotificationHandler<StructNote, StructNoteHandler>()
    .AddBehavior<ClassQuery, string, LoggingBehavior<ClassQuery, string>>()
    .UseNotificationPublishStrategy(NotificationPublishStrategy.Parallel));

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using var scope = provider.CreateAsyncScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
var log = scope.ServiceProvider.GetRequiredService<Log>();
var failures = 0;

await Check("class request", async () => await mediator.Send(new ClassQuery("ping")) == "pong:ping" && log.Contains("behavior:ClassQuery"));
await Check("struct request", async () => await mediator.Send(new StructQuery(20)) == 21);
await Check("struct request as IRequest<int>", async () => await mediator.Send((IRequest<int>)new StructQuery(1)) == 2);
await Check("void request", async () =>
{
    await mediator.Send(new VoidCommand());

    return log.Contains("void");
});
await Check("void request as Send<Unit>", async () => await mediator.Send<Unit>(new VoidCommand()) == Unit.Value);
await Check("exception hierarchy", async () => await mediator.Send(new FailingQuery()) == -1 && log.Contains("fallback:FileNotFoundException"));
await Check("stream", async () =>
{
    var total = 0;

    await foreach (var value in mediator.CreateStream(new CountStream(3)))
    {
        total += value;
    }

    return total == 6;
});
await Check("struct notification", async () =>
{
    await mediator.Publish(new StructNote(1));

    return log.Contains("note:1");
});
await Check("struct notification as INotification", async () =>
{
    await mediator.Publish((INotification)new StructNote(2));

    return log.Contains("note:2");
});

// Microsoft.Extensions.DependencyInjection can only close open generics over reference types without dynamic code,
// so open behaviors need a container whose requests and responses are all classes.
var openServices = new ServiceCollection();
openServices.AddSingleton<Log>();
openServices.AddMediator(options => options
    .AddRequestHandler<ClassQuery, string, ClassQueryHandler>()
    .AddOpenBehavior(typeof(LoggingBehavior<,>)));
await using var openProvider = openServices.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using var openScope = openProvider.CreateAsyncScope();
var openLog = openScope.ServiceProvider.GetRequiredService<Log>();

await Check("open behavior", async () =>
    await openScope.ServiceProvider.GetRequiredService<IMediator>().Send(new ClassQuery("ping")) == "pong:ping"
    && openLog.Contains("behavior:ClassQuery"));

Console.WriteLine(failures == 0 ? "All AOT smoke checks passed." : $"{failures} AOT smoke check(s) failed.");

return failures;

async Task Check(string name, Func<Task<bool>> check)
{
    try
    {
        var passed = await check();
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
        failures += passed ? 0 : 1;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {name}: {exception}");
        failures++;
    }
}

/// <summary>
/// Records what ran.
/// </summary>
internal sealed class Log
{
    private readonly List<string> _events = [];

    /// <summary>
    /// Records an event.
    /// </summary>
    /// <param name="value">The event.</param>
    public void Add(string value)
    {
        lock (_events)
        {
            _events.Add(value);
        }
    }

    /// <summary>
    /// Determines whether an event was recorded.
    /// </summary>
    /// <param name="value">The event.</param>
    /// <returns><see langword="true"/> when the event was recorded.</returns>
    public bool Contains(string value)
    {
        lock (_events)
        {
            return _events.Contains(value);
        }
    }
}

/// <summary>
/// A class request.
/// </summary>
/// <param name="Message">The message.</param>
internal sealed record ClassQuery(string Message) : IRequest<string>;

/// <summary>
/// A struct request with a value-type response.
/// </summary>
/// <param name="Value">The value.</param>
internal readonly record struct StructQuery(int Value) : IRequest<int>;

/// <summary>
/// A void request.
/// </summary>
internal sealed record VoidCommand : IRequest;

/// <summary>
/// A struct request whose handler fails with a derived exception type.
/// </summary>
internal readonly record struct FailingQuery : IRequest<int>;

/// <summary>
/// A stream request.
/// </summary>
/// <param name="Count">How many values to stream.</param>
internal sealed record CountStream(int Count) : IStreamRequest<int>;

/// <summary>
/// A struct notification.
/// </summary>
/// <param name="Value">The value.</param>
internal readonly record struct StructNote(int Value) : INotification;

/// <summary>
/// Handles <see cref="ClassQuery"/>.
/// </summary>
internal sealed class ClassQueryHandler : IRequestHandler<ClassQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(ClassQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"pong:{request.Message}");
    }
}

/// <summary>
/// Handles <see cref="StructQuery"/>.
/// </summary>
internal sealed class StructQueryHandler : IRequestHandler<StructQuery, int>
{
    /// <inheritdoc />
    public Task<int> Handle(StructQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request.Value + 1);
    }
}

/// <summary>
/// Handles <see cref="VoidCommand"/>.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class VoidCommandHandler(Log log) : IRequestHandler<VoidCommand>
{
    /// <inheritdoc />
    public Task Handle(VoidCommand request, CancellationToken cancellationToken)
    {
        log.Add("void");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Fails with a <see cref="FileNotFoundException"/>, so exception processing walks a three-level hierarchy.
/// </summary>
internal sealed class FailingQueryHandler : IRequestHandler<FailingQuery, int>
{
    /// <inheritdoc />
    public async Task<int> Handle(FailingQuery request, CancellationToken cancellationToken)
    {
        await Task.Yield();

        throw new FileNotFoundException("missing");
    }
}

/// <summary>
/// Recovers <see cref="FailingQuery"/> failures.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class FailingQueryFallback(Log log) : IRequestExceptionHandler<FailingQuery, int, Exception>
{
    /// <inheritdoc />
    public Task Handle(FailingQuery request, Exception exception, RequestExceptionHandlerState<int> state, CancellationToken cancellationToken)
    {
        log.Add($"fallback:{exception.GetType().Name}");
        state.SetHandled(-1);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Observes unhandled <see cref="FailingQuery"/> failures.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class FailingQueryAction(Log log) : IRequestExceptionAction<FailingQuery, Exception>
{
    /// <inheritdoc />
    public Task Execute(FailingQuery request, Exception exception, CancellationToken cancellationToken)
    {
        log.Add("action");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Streams the numbers one to <see cref="CountStream.Count"/>.
/// </summary>
internal sealed class CountStreamHandler : IStreamRequestHandler<CountStream, int>
{
    /// <inheritdoc />
    public async IAsyncEnumerable<int> Handle(CountStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var value = 1; value <= request.Count; value++)
        {
            await Task.Yield();

            yield return value;
        }
    }
}

/// <summary>
/// Handles <see cref="StructNote"/>.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class StructNoteHandler(Log log) : INotificationHandler<StructNote>
{
    /// <inheritdoc />
    public Task Handle(StructNote notification, CancellationToken cancellationToken)
    {
        log.Add($"note:{notification.Value}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// A behavior registered closed for one request, and open in a container that only has class requests and responses.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <param name="log">The event log.</param>
internal sealed class LoggingBehavior<TRequest, TResponse>(Log log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add($"behavior:{typeof(TRequest).Name}");

        return next(cancellationToken);
    }
}