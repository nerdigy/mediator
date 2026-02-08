# API Cheatsheet

Signature-first reference for LLM and human copy/paste.

## Install

```bash
dotnet add package Nerdigy.Mediator.DependencyInjection
```

## Core Dispatch

```csharp
public interface IMediator : ISender, IPublisher;
```

```csharp
public interface ISender
{
    Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default);

    Task Send(
        IRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);
}
```

```csharp
public interface IPublisher
{
    Task Publish<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
```

## Message Contracts

```csharp
public interface IBaseRequest;
public interface IRequest<out TResponse> : IBaseRequest;
public interface IRequest : IRequest<Unit>;
public interface IStreamRequest<out TResponse> : IBaseRequest;
public interface INotification;
```

## Handler Contracts

```csharp
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface IRequestHandler<in TRequest>
    where TRequest : IRequest
{
    Task Handle(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
```

## Pipeline Contracts

```csharp
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>();
```

```csharp
public interface IRequestPreProcessor<in TRequest>
    where TRequest : IBaseRequest
{
    Task Process(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : IRequest<TResponse>
{
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
```

## Exception Contracts

```csharp
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IRequest<TResponse>
    where TException : Exception
{
    Task Handle(
        TRequest request,
        TException exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : IBaseRequest
    where TException : Exception
{
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}
```

```csharp
public interface IStreamRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : IStreamRequest<TResponse>
    where TException : Exception
{
    Task Handle(
        TRequest request,
        TException exception,
        StreamRequestExceptionHandlerState<TResponse> state,
        CancellationToken cancellationToken);
}
```

```csharp
public sealed class RequestExceptionHandlerState<TResponse>
{
    public bool Handled { get; }
    public TResponse? Response { get; }
    public void SetHandled(TResponse response);
}
```

```csharp
public sealed class StreamRequestExceptionHandlerState<TResponse>
{
    public bool Handled { get; }
    public IAsyncEnumerable<TResponse>? ResponseStream { get; }
    public void SetHandled(IAsyncEnumerable<TResponse> responseStream);
}
```

## DI Registration

```csharp
public static IServiceCollection AddMediator(
    this IServiceCollection services,
    Action<NerdigyMediatorOptions> configure);
```

```csharp
public static IServiceCollection AddMediator(
    this IServiceCollection services,
    params Assembly[] assemblies);
```

```csharp
public sealed class NerdigyMediatorOptions
{
    public ServiceLifetime MediatorLifetime { get; set; }
    public ServiceLifetime HandlerLifetime { get; set; }

    public NerdigyMediatorOptions RegisterServicesFromAssembly(Assembly assembly);
    public NerdigyMediatorOptions RegisterServicesFromAssemblies(params Assembly[] assemblies);
    public NerdigyMediatorOptions RegisterServicesFromAssemblyContaining<TMarker>();
    public NerdigyMediatorOptions AddOpenBehavior(Type openBehaviorType);

    public NerdigyMediatorOptions UseNotificationPublisherStrategy(
        NerdigyMediatorNotificationPublisherStrategy strategy);
    public NerdigyMediatorOptions UseNotificationPublisher<TPublisher>()
        where TPublisher : class, INotificationPublisher;
    public NerdigyMediatorOptions UseNotificationPublisher(INotificationPublisher publisher);
}
```

```csharp
public enum NerdigyMediatorNotificationPublisherStrategy
{
    Sequential = 0,
    Parallel = 1
}
```
