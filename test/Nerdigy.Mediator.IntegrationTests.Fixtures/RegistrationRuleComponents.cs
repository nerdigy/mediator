using System.Runtime.CompilerServices;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator.IntegrationTests.Fixtures;

/// <summary>
/// Marks requests that should be audited.
/// </summary>
public interface IAuditable : IRequest<Unit>;

/// <summary>
/// A behavior closed over an interface request type, which the mediator would never run.
/// </summary>
public sealed class AuditBehavior : IPipelineBehavior<IAuditable, Unit>
{
    /// <inheritdoc />
    public Task<Unit> Handle(IAuditable request, RequestHandlerDelegate<Unit> next, CancellationToken cancellationToken)
    {
        return next(cancellationToken);
    }
}

/// <summary>
/// A preprocessor closed over an interface request type, which the mediator would never run.
/// </summary>
public sealed class AuditPreProcessor : IRequestPreProcessor<IAuditable>
{
    /// <inheritdoc />
    public Task Process(IAuditable request, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// A notification handler closed over <see cref="INotification"/>, which the mediator would never run.
/// </summary>
public sealed class AllNotificationsHandler : INotificationHandler<INotification>
{
    /// <inheritdoc />
    public Task Handle(INotification notification, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// A void-style command.
/// </summary>
public sealed record VoidCommand : IRequest;

/// <summary>
/// Handles a void command through <c>IRequestHandler&lt;TRequest, Unit&gt;</c>, which void dispatch never uses.
/// </summary>
public sealed class VoidCommandUnitHandler : IRequestHandler<VoidCommand, Unit>
{
    /// <inheritdoc />
    public Task<Unit> Handle(VoidCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(Unit.Value);
    }
}

/// <summary>
/// A query with more than one handler.
/// </summary>
public sealed record RuleQuery : IRequest<string>;

/// <summary>
/// The first handler for <see cref="RuleQuery"/>.
/// </summary>
public sealed class RuleQueryHandlerA : IRequestHandler<RuleQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(RuleQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult("A");
    }
}

/// <summary>
/// The second handler for <see cref="RuleQuery"/>.
/// </summary>
public sealed class RuleQueryHandlerB : IRequestHandler<RuleQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(RuleQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult("B");
    }
}

/// <summary>
/// Records the order components run in.
/// </summary>
public sealed class RuleEventLog
{
    /// <summary>
    /// Gets the recorded events.
    /// </summary>
    public List<string> Events { get; } = [];
}

/// <summary>
/// A behavior whose name sorts last, for checking that scanned behaviors run in order of full type name.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ZRuleQueryBehavior(RuleEventLog log) : IPipelineBehavior<RuleQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(RuleQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        log.Events.Add("Z");

        return next(cancellationToken);
    }
}

/// <summary>
/// A behavior whose name sorts first, for checking that scanned behaviors run in order of full type name.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ARuleQueryBehavior(RuleEventLog log) : IPipelineBehavior<RuleQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(RuleQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        log.Events.Add("A");

        return next(cancellationToken);
    }
}

/// <summary>
/// Represents a query registered explicitly.
/// </summary>
public sealed record ExplicitQuery : IRequest<string>;

/// <summary>
/// Represents a void command registered explicitly.
/// </summary>
public sealed record ExplicitCommand : IRequest;

/// <summary>
/// Represents a stream request registered explicitly.
/// </summary>
public sealed record ExplicitStream : IStreamRequest<int>;

/// <summary>
/// Represents a notification registered explicitly.
/// </summary>
public sealed record ExplicitNotification : INotification;

/// <summary>
/// Handles <see cref="ExplicitQuery"/>.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitQueryHandler(RuleEventLog log) : IRequestHandler<ExplicitQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(ExplicitQuery request, CancellationToken cancellationToken)
    {
        log.Events.Add("query");

        return Task.FromResult("query");
    }
}

/// <summary>
/// Handles <see cref="ExplicitCommand"/> by failing.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitCommandHandler(RuleEventLog log) : IRequestHandler<ExplicitCommand>
{
    /// <inheritdoc />
    public Task Handle(ExplicitCommand request, CancellationToken cancellationToken)
    {
        log.Events.Add("command");

        throw new InvalidOperationException("command failure");
    }
}

/// <summary>
/// Streams values for <see cref="ExplicitStream"/>.
/// </summary>
public sealed class ExplicitStreamHandler : IStreamRequestHandler<ExplicitStream, int>
{
    /// <inheritdoc />
    public async IAsyncEnumerable<int> Handle(ExplicitStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        yield return 10;
        yield return 20;
    }
}

/// <summary>
/// Handles <see cref="ExplicitNotification"/>.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitNotificationHandler(RuleEventLog log) : INotificationHandler<ExplicitNotification>
{
    /// <inheritdoc />
    public Task Handle(ExplicitNotification notification, CancellationToken cancellationToken)
    {
        log.Events.Add("notification");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Preprocesses <see cref="ExplicitQuery"/>.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitPreProcessor(RuleEventLog log) : IRequestPreProcessor<ExplicitQuery>
{
    /// <inheritdoc />
    public Task Process(ExplicitQuery request, CancellationToken cancellationToken)
    {
        log.Events.Add("pre");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps <see cref="ExplicitQuery"/>.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitBehavior(RuleEventLog log) : IPipelineBehavior<ExplicitQuery, string>
{
    /// <inheritdoc />
    public Task<string> Handle(ExplicitQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        log.Events.Add("behavior");

        return next(cancellationToken);
    }
}

/// <summary>
/// Postprocesses <see cref="ExplicitQuery"/>.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitPostProcessor(RuleEventLog log) : IRequestPostProcessor<ExplicitQuery, string>
{
    /// <inheritdoc />
    public Task Process(ExplicitQuery request, string response, CancellationToken cancellationToken)
    {
        log.Events.Add("post");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Recovers <see cref="ExplicitCommand"/> failures.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitCommandExceptionHandler(RuleEventLog log) : IRequestExceptionHandler<ExplicitCommand, Unit, InvalidOperationException>
{
    /// <inheritdoc />
    public Task Handle(
        ExplicitCommand request,
        InvalidOperationException exception,
        RequestExceptionHandlerState<Unit> state,
        CancellationToken cancellationToken)
    {
        log.Events.Add("command-handled");
        state.SetHandled(Unit.Value);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Observes unhandled <see cref="ExplicitCommand"/> failures. It does not run here, because the handler recovers.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitCommandExceptionAction(RuleEventLog log) : IRequestExceptionAction<ExplicitCommand, Exception>
{
    /// <inheritdoc />
    public Task Execute(ExplicitCommand request, Exception exception, CancellationToken cancellationToken)
    {
        log.Events.Add("command-action");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Wraps <see cref="ExplicitStream"/>.
/// </summary>
/// <param name="log">The event log.</param>
public sealed class ExplicitStreamBehavior(RuleEventLog log) : IStreamPipelineBehavior<ExplicitStream, int>
{
    /// <inheritdoc />
    public IAsyncEnumerable<int> Handle(ExplicitStream request, StreamHandlerDelegate<int> next, CancellationToken cancellationToken)
    {
        log.Events.Add("stream-behavior");

        return next(cancellationToken);
    }
}

/// <summary>
/// A query whose handler records its disposal.
/// </summary>
public sealed record DisposableQuery : IRequest<string>;

/// <summary>
/// Records when handlers are disposed.
/// </summary>
public sealed class DisposalLog
{
    /// <summary>
    /// Gets the number of synchronous disposals.
    /// </summary>
    public int Disposed { get; set; }

    /// <summary>
    /// Gets the number of asynchronous disposals.
    /// </summary>
    public int DisposedAsync { get; set; }
}

/// <summary>
/// A handler that is disposable both ways, to check that the container owns and disposes it.
/// </summary>
/// <param name="log">The disposal log.</param>
public sealed class DisposableQueryHandler(DisposalLog log) : IRequestHandler<DisposableQuery, string>, IDisposable, IAsyncDisposable
{
    /// <inheritdoc />
    public Task<string> Handle(DisposableQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult("done");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        log.Disposed++;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        log.DisposedAsync++;

        return ValueTask.CompletedTask;
    }
}