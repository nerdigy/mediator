using System.Diagnostics;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

using Nerdigy.Mediator;
using Nerdigy.Mediator.Contracts;
using Nerdigy.Mediator.DependencyInjection;

// Publish with Native AOT and run: every check prints PASS or FAIL, and the exit code is the number of failures.
// The checks cover the shapes that need statically registered dispatch code: struct requests, interface-typed struct
// notifications, value-type responses, exception hierarchies and actions, open behaviors, streams, direct MediatorRegistry
// use, and telemetry. Activity listeners are process-wide and switch the mediator between two code paths, so the dispatch
// checks run once before a listener is attached and once after.
var services = new ServiceCollection();
services.AddSingleton<Log>();
services.AddTransient<IRequestHandler<UnregisteredQuery, string>, UnregisteredQueryHandler>();
services.AddMediator(options => options
    .AddRequestHandler<ClassQuery, string, ClassQueryHandler>()
    .AddRequestHandler<StructQuery, int, StructQueryHandler>()
    .AddRequestHandler<VoidCommand, VoidCommandHandler>()
    .AddRequestHandler<FailingQuery, int, FailingQueryHandler>()
    .AddExceptionHandler<FailingQuery, int, Exception, FailingQueryFallback>()
    .AddExceptionAction<FailingQuery, Exception, FailingQueryAction>()
    .AddRequestHandler<UnhandledQuery, int, UnhandledQueryHandler>()
    .AddExceptionAction<UnhandledQuery, Exception, UnhandledQueryAction>()
    .AddRequestHandler<ProcessedQuery, int, ProcessedQueryHandler>()
    .AddPreProcessor<ProcessedQuery, ProcessedQueryPreProcessor>()
    .AddPostProcessor<ProcessedQuery, int, ProcessedQueryPostProcessor>()
    .AddStreamRequestHandler<CountStream, int, CountStreamHandler>()
    .AddStreamRequestHandler<LoggedStream, int, LoggedStreamHandler>()
    .AddStreamBehavior<LoggedStream, int, StreamLoggingBehavior<LoggedStream, int>>()
    .AddStreamRequestHandler<ReplacedStream, int, ReplacedStreamHandler>()
    .AddStreamExceptionHandler<ReplacedStream, int, Exception, ReplacedStreamFallback>()
    .AddNotificationHandler<StructNote, StructNoteHandler>()
    .AddBehavior<ClassQuery, string, LoggingBehavior<ClassQuery, string>>()
    .UseNotificationPublishStrategy(NotificationPublishStrategy.Parallel));

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using var scope = provider.CreateAsyncScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
var log = scope.ServiceProvider.GetRequiredService<Log>();
var failures = 0;
var phase = "no listener";

// Microsoft.Extensions.DependencyInjection can only close open generics over reference types without dynamic code,
// so open behaviors need a container whose requests and responses are all classes.
var openServices = new ServiceCollection();
openServices.AddSingleton<Log>();
openServices.AddMediator(options => options
    .AddRequestHandler<ClassQuery, string, ClassQueryHandler>()
    .AddOpenBehavior(typeof(LoggingBehavior<,>))
    .AddStreamRequestHandler<TextStream, string, TextStreamHandler>()
    .AddOpenStreamBehavior(typeof(StreamLoggingBehavior<,>)));
await using var openProvider = openServices.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using var openScope = openProvider.CreateAsyncScope();
var openMediator = openScope.ServiceProvider.GetRequiredService<IMediator>();
var openLog = openScope.ServiceProvider.GetRequiredService<Log>();

// The path documented for applications that do not use the DI package.
MediatorRegistry.RegisterRequest<RegistryQuery, int>();
MediatorRegistry.RegisterVoidRequest<RegistryCommand>();
MediatorRegistry.RegisterStreamRequest<RegistryStream, int>();
MediatorRegistry.RegisterNotification<RegistryNote>();
IMediator handBuilt = new Mediator(new RegistryServiceProvider(log));

await Check("no activity listener is attached", () =>
{
    using var probe = new ActivitySource(MediatorTelemetry.ActivitySourceName);

    return Task.FromResult(!probe.HasListeners());
});
await RunDispatchChecks();

using var listener = new ActivityListener
{
    ShouldListenTo = static source => source.Name == MediatorTelemetry.ActivitySourceName,
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity => log.Add($"activity:{activity.OperationName}:{activity.GetTagItem(MediatorTelemetry.MessageTypeTag)}"),
};
ActivitySource.AddActivityListener(listener);
phase = "listener";

await RunDispatchChecks();
await Check("stopped Send activity carries the message type", async () =>
{
    await mediator.Send(new StructQuery(1));

    return log.Contains($"activity:Nerdigy.Mediator.Send:{typeof(StructQuery).FullName}");
});

Console.WriteLine(failures == 0 ? "All AOT smoke checks passed." : $"{failures} AOT smoke check(s) failed.");

return failures;

async Task RunDispatchChecks()
{
    log.Clear();
    openLog.Clear();

    await Check("class request", async () => await mediator.Send(new ClassQuery("ping")) == "pong:ping" && log.Contains("behavior:ClassQuery"));
    await Check("struct request", async () => await mediator.Send(new StructQuery(20)) == 21);
    await Check("struct request as IRequest<int>", async () => await mediator.Send((IRequest<int>)new StructQuery(1)) == 2);
    await Check("void request", async () =>
    {
        await mediator.Send(new VoidCommand());

        return log.Contains("void");
    });
    await Check("void request as Send<Unit>", async () => await mediator.Send<Unit>(new VoidCommand()) == Unit.Value);
    await Check("pre-processor and post-processor", async () =>
        await mediator.Send(new ProcessedQuery(5)) == 6 && log.Contains("pre:5") && log.Contains("post:6"));
    await Check("exception hierarchy", async () => await mediator.Send(new FailingQuery()) == -1 && log.Contains("fallback:FileNotFoundException"));
    await Check("unhandled failure runs the exception action", async () =>
    {
        try
        {
            await mediator.Send(new UnhandledQuery());

            return false;
        }
        catch (FileNotFoundException)
        {
            return log.Contains("unhandled-action:FileNotFoundException");
        }
    });
    await Check("message type with no registered dispatch code", async () =>
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return await mediator.Send(new UnregisteredQuery()) == "unregistered";
        }

        try
        {
            await mediator.Send(new UnregisteredQuery());

            return false;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message.Contains("cannot be dispatched", StringComparison.Ordinal);
        }
    });
    await Check("stream", async () => (await Collect(mediator.CreateStream(new CountStream(3)))).Sum() == 6);
    await Check("stream behavior", async () =>
        (await Collect(mediator.CreateStream(new LoggedStream(2)))).Sum() == 3 && log.Contains("stream-behavior:LoggedStream"));
    await Check("failed stream replaced by an exception handler", async () =>
        (await Collect(mediator.CreateStream(new ReplacedStream()))).SequenceEqual([1, 100, 200])
        && log.Contains("stream-fallback:FileNotFoundException"));
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

    await Check("open behavior", async () =>
        await openMediator.Send(new ClassQuery("ping")) == "pong:ping" && openLog.Contains("behavior:ClassQuery"));
    await Check("open stream behavior", async () =>
        (await Collect(openMediator.CreateStream(new TextStream("a")))).SequenceEqual(["a"]) && openLog.Contains("stream-behavior:TextStream"));

    await Check("MediatorRegistry request", async () => await handBuilt.Send(new RegistryQuery(1)) == 2);
    await Check("MediatorRegistry void request", async () =>
    {
        await handBuilt.Send(new RegistryCommand());

        return log.Contains("registry-void");
    });
    await Check("MediatorRegistry stream", async () => (await Collect(handBuilt.CreateStream(new RegistryStream()))).SequenceEqual([7]));
    await Check("MediatorRegistry notification as INotification", async () =>
    {
        await handBuilt.Publish((INotification)new RegistryNote(3));

        return log.Contains("registry-note:3");
    });
}

static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
{
    var values = new List<T>();

    await foreach (var value in stream)
    {
        values.Add(value);
    }

    return values;
}

async Task Check(string name, Func<Task<bool>> check)
{
    try
    {
        var passed = await check();
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} [{phase}] {name}");
        failures += passed ? 0 : 1;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL [{phase}] {name}: {exception}");
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
    /// Forgets every recorded event.
    /// </summary>
    public void Clear()
    {
        lock (_events)
        {
            _events.Clear();
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
/// A struct request whose handler fails with no exception handler registered, so the failure reaches the caller.
/// </summary>
internal readonly record struct UnhandledQuery : IRequest<int>;

/// <summary>
/// A struct request with a pre-processor and a post-processor.
/// </summary>
/// <param name="Value">The value.</param>
internal readonly record struct ProcessedQuery(int Value) : IRequest<int>;

/// <summary>
/// A request whose handler is registered directly in the container, so no dispatch code is registered for it.
/// </summary>
internal sealed record UnregisteredQuery : IRequest<string>;

/// <summary>
/// A struct stream request with a closed stream behavior.
/// </summary>
/// <param name="Count">How many values to stream.</param>
internal readonly record struct LoggedStream(int Count) : IStreamRequest<int>;

/// <summary>
/// A struct stream request whose handler fails after its first item.
/// </summary>
internal readonly record struct ReplacedStream : IStreamRequest<int>;

/// <summary>
/// A stream request with a class response, for the open stream behavior.
/// </summary>
/// <param name="Text">The value to stream.</param>
internal sealed record TextStream(string Text) : IStreamRequest<string>;

/// <summary>
/// A struct request dispatched by a hand-built mediator.
/// </summary>
/// <param name="Value">The value.</param>
internal readonly record struct RegistryQuery(int Value) : IRequest<int>;

/// <summary>
/// A struct void request dispatched by a hand-built mediator.
/// </summary>
internal readonly record struct RegistryCommand : IRequest;

/// <summary>
/// A struct stream request dispatched by a hand-built mediator.
/// </summary>
internal readonly record struct RegistryStream : IStreamRequest<int>;

/// <summary>
/// A struct notification published by a hand-built mediator.
/// </summary>
/// <param name="Value">The value.</param>
internal readonly record struct RegistryNote(int Value) : INotification;

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

/// <summary>
/// Fails with a <see cref="FileNotFoundException"/>, and no exception handler recovers it.
/// </summary>
internal sealed class UnhandledQueryHandler : IRequestHandler<UnhandledQuery, int>
{
    /// <inheritdoc />
    public async Task<int> Handle(UnhandledQuery request, CancellationToken cancellationToken)
    {
        await Task.Yield();

        throw new FileNotFoundException("missing");
    }
}

/// <summary>
/// Observes <see cref="UnhandledQuery"/> failures.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class UnhandledQueryAction(Log log) : IRequestExceptionAction<UnhandledQuery, Exception>
{
    /// <inheritdoc />
    public Task Execute(UnhandledQuery request, Exception exception, CancellationToken cancellationToken)
    {
        log.Add($"unhandled-action:{exception.GetType().Name}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Handles <see cref="ProcessedQuery"/>.
/// </summary>
internal sealed class ProcessedQueryHandler : IRequestHandler<ProcessedQuery, int>
{
    /// <inheritdoc />
    public Task<int> Handle(ProcessedQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request.Value + 1);
    }
}

/// <summary>
/// Records <see cref="ProcessedQuery"/> before its handler runs.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class ProcessedQueryPreProcessor(Log log) : IRequestPreProcessor<ProcessedQuery>
{
    /// <inheritdoc />
    public Task Process(ProcessedQuery request, CancellationToken cancellationToken)
    {
        log.Add($"pre:{request.Value}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Records the <see cref="ProcessedQuery"/> response after its handler runs.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class ProcessedQueryPostProcessor(Log log) : IRequestPostProcessor<ProcessedQuery, int>
{
    /// <inheritdoc />
    public Task Process(ProcessedQuery request, int response, CancellationToken cancellationToken)
    {
        log.Add($"post:{response}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Handles <see cref="UnregisteredQuery"/>. Registered directly in the container.
/// </summary>
internal sealed class UnregisteredQueryHandler : IRequestHandler<UnregisteredQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(UnregisteredQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult("unregistered");
    }
}

/// <summary>
/// Streams the numbers one to <see cref="LoggedStream.Count"/>.
/// </summary>
internal sealed class LoggedStreamHandler : IStreamRequestHandler<LoggedStream, int>
{
    /// <inheritdoc />
    public async IAsyncEnumerable<int> Handle(LoggedStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var value = 1; value <= request.Count; value++)
        {
            await Task.Yield();

            yield return value;
        }
    }
}

/// <summary>
/// Yields one item, then fails with a <see cref="FileNotFoundException"/>.
/// </summary>
internal sealed class ReplacedStreamHandler : IStreamRequestHandler<ReplacedStream, int>
{
    /// <inheritdoc />
    public async IAsyncEnumerable<int> Handle(ReplacedStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        yield return 1;

        throw new FileNotFoundException("missing");
    }
}

/// <summary>
/// Replaces a failed <see cref="ReplacedStream"/>.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class ReplacedStreamFallback(Log log) : IStreamRequestExceptionHandler<ReplacedStream, int, Exception>
{
    /// <inheritdoc />
    public Task Handle(ReplacedStream request, Exception exception, StreamRequestExceptionHandlerState<int> state, CancellationToken cancellationToken)
    {
        log.Add($"stream-fallback:{exception.GetType().Name}");
        state.SetHandled(Replacement());

        return Task.CompletedTask;
    }

    /// <summary>
    /// Produces the replacement stream.
    /// </summary>
    /// <returns>The replacement items.</returns>
    private static async IAsyncEnumerable<int> Replacement()
    {
        await Task.Yield();

        yield return 100;
        yield return 200;
    }
}

/// <summary>
/// Streams <see cref="TextStream.Text"/> once.
/// </summary>
internal sealed class TextStreamHandler : IStreamRequestHandler<TextStream, string>
{
    /// <inheritdoc />
    public async IAsyncEnumerable<string> Handle(TextStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        yield return request.Text;
    }
}

/// <summary>
/// A stream behavior registered closed for one request, and open in a container that only has class requests and responses.
/// </summary>
/// <typeparam name="TRequest">The stream request type.</typeparam>
/// <typeparam name="TResponse">The streamed response type.</typeparam>
/// <param name="log">The event log.</param>
internal sealed class StreamLoggingBehavior<TRequest, TResponse>(Log log) : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add($"stream-behavior:{typeof(TRequest).Name}");

        return next(cancellationToken);
    }
}

/// <summary>
/// Handles <see cref="RegistryQuery"/>.
/// </summary>
internal sealed class RegistryQueryHandler : IRequestHandler<RegistryQuery, int>
{
    /// <inheritdoc />
    public Task<int> Handle(RegistryQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request.Value + 1);
    }
}

/// <summary>
/// Handles <see cref="RegistryCommand"/>.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class RegistryCommandHandler(Log log) : IRequestHandler<RegistryCommand>
{
    /// <inheritdoc />
    public Task Handle(RegistryCommand request, CancellationToken cancellationToken)
    {
        log.Add("registry-void");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Streams one value for <see cref="RegistryStream"/>.
/// </summary>
internal sealed class RegistryStreamHandler : IStreamRequestHandler<RegistryStream, int>
{
    /// <inheritdoc />
    public async IAsyncEnumerable<int> Handle(RegistryStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        yield return 7;
    }
}

/// <summary>
/// Handles <see cref="RegistryNote"/>.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class RegistryNoteHandler(Log log) : INotificationHandler<RegistryNote>
{
    /// <inheritdoc />
    public Task Handle(RegistryNote notification, CancellationToken cancellationToken)
    {
        log.Add($"registry-note:{notification.Value}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// A minimal service provider for a mediator built without the DI package.
/// </summary>
/// <param name="log">The event log.</param>
internal sealed class RegistryServiceProvider(Log log) : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new()
    {
        [typeof(IRequestHandler<RegistryQuery, int>)] = new RegistryQueryHandler(),
        [typeof(IRequestHandler<RegistryCommand>)] = new RegistryCommandHandler(log),
        [typeof(IStreamRequestHandler<RegistryStream, int>)] = new RegistryStreamHandler(),
        [typeof(IEnumerable<INotificationHandler<RegistryNote>>)] = new INotificationHandler<RegistryNote>[] { new RegistryNoteHandler(log) },
    };

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        return _services.GetValueOrDefault(serviceType);
    }
}