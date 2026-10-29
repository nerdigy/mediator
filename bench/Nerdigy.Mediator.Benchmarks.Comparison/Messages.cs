namespace Nerdigy.Mediator.Benchmarks.Comparison.Nerd
{
    using Nerdigy.Mediator.Contracts;

    public interface IPiped;

    public sealed record Ping(string Payload) : IRequest<string>;

    public sealed record PipedPing(string Payload) : IRequest<string>, IPiped;

    public sealed record Note(string Payload) : INotification;

    public sealed class PingHandler : IRequestHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Payload);
    }

    public sealed class PipedPingHandler : IRequestHandler<PipedPing, string>
    {
        public Task<string> Handle(PipedPing request, CancellationToken cancellationToken) => Task.FromResult(request.Payload);
    }

    public sealed class PassThrough<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>, IPiped
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    public sealed class NoteHandler1 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NoteHandler2 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NoteHandler3 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NoteHandler4 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

namespace Nerdigy.Mediator.Benchmarks.Comparison.MediatRTypes
{
    using MediatR;

    public interface IPiped;

    public sealed record Ping(string Payload) : IRequest<string>;

    public sealed record PipedPing(string Payload) : IRequest<string>, IPiped;

    public sealed record Note(string Payload) : INotification;

    public sealed class PingHandler : IRequestHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Payload);
    }

    public sealed class PipedPingHandler : IRequestHandler<PipedPing, string>
    {
        public Task<string> Handle(PipedPing request, CancellationToken cancellationToken) => Task.FromResult(request.Payload);
    }

    public sealed class PassThrough<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull, IPiped
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    public sealed class NoteHandler1 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NoteHandler2 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NoteHandler3 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NoteHandler4 : INotificationHandler<Note>
    {
        public Task Handle(Note notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

namespace Nerdigy.Mediator.Benchmarks.Comparison.Mart
{
    using global::Mediator;

    public interface IPiped;

    public sealed record Ping(string Payload) : IRequest<string>;

    public sealed record PipedPing(string Payload) : IRequest<string>, IPiped;

    public sealed record Note(string Payload) : INotification;

    public sealed class PingHandler : IRequestHandler<Ping, string>
    {
        public ValueTask<string> Handle(Ping request, CancellationToken cancellationToken) => new(request.Payload);
    }

    public sealed class PipedPingHandler : IRequestHandler<PipedPing, string>
    {
        public ValueTask<string> Handle(PipedPing request, CancellationToken cancellationToken) => new(request.Payload);
    }

    public sealed class PassThrough<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
        where TMessage : notnull, IMessage, IPiped
    {
        public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken) => next(message, cancellationToken);
    }

    public sealed class NoteHandler1 : INotificationHandler<Note>
    {
        public ValueTask Handle(Note notification, CancellationToken cancellationToken) => default;
    }

    public sealed class NoteHandler2 : INotificationHandler<Note>
    {
        public ValueTask Handle(Note notification, CancellationToken cancellationToken) => default;
    }

    public sealed class NoteHandler3 : INotificationHandler<Note>
    {
        public ValueTask Handle(Note notification, CancellationToken cancellationToken) => default;
    }

    public sealed class NoteHandler4 : INotificationHandler<Note>
    {
        public ValueTask Handle(Note notification, CancellationToken cancellationToken) => default;
    }
}