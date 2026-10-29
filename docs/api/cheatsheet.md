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
public interface IRequest<TResponse> : IBaseRequest;
public interface IRequest : IRequest<Unit>;
public interface IStreamRequest<TResponse> : IBaseRequest;
public interface INotification;
```

## Handler Contracts

```csharp
public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface IRequestHandler<TRequest>
    where TRequest : IRequest
{
    Task Handle(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface IStreamRequestHandler<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface INotificationHandler<TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
```

## Pipeline Contracts

```csharp
// next() keeps the current token; next(token) passes a different one down the pipeline.
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);
```

```csharp
public interface IRequestPreProcessor<TRequest>
    where TRequest : IBaseRequest
{
    Task Process(TRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IRequestPostProcessor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task Process(TRequest request, TResponse response, CancellationToken cancellationToken);
}
```

## Exception Contracts

```csharp
public interface IRequestExceptionHandler<TRequest, TResponse, TException>
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
public interface IRequestExceptionAction<TRequest, TException>
    where TRequest : IBaseRequest
    where TException : Exception
{
    Task Execute(TRequest request, TException exception, CancellationToken cancellationToken);
}
```

```csharp
public interface IStreamRequestExceptionHandler<TRequest, TResponse, TException>
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

```csharp
// Thrown when an exception handler, action, or failed-stream cleanup throws.
public sealed class MediatorExceptionProcessingException : Exception
{
    // InnerException is the original exception.
    public IReadOnlyList<Exception> ProcessingExceptions { get; }
}
```

## DI Registration

```csharp
public static IServiceCollection AddMediator(
    this IServiceCollection services,
    Action<MediatorOptions> configure);

public static IServiceCollection AddMediator(
    this IServiceCollection services,
    params Assembly[] assemblies); // scanning; not trimming-safe

public static IServiceCollection AddMediator(this IServiceCollection services); // no scanning
```

```csharp
public sealed class MediatorOptions
{
    public IReadOnlyCollection<Assembly> AssembliesToScan { get; }
    public ServiceLifetime MediatorLifetime { get; set; } // default Transient
    public ServiceLifetime HandlerLifetime { get; set; }  // default Transient
    public bool IgnoreTypeLoadFailures { get; set; }      // default false

    // Scanning (RequiresUnreferencedCode)
    public MediatorOptions RegisterServicesFromAssembly(Assembly assembly);
    public MediatorOptions RegisterServicesFromAssemblies(params Assembly[] assemblies);
    public MediatorOptions RegisterServicesFromAssemblyContaining<TMarker>();

    // Explicit registration (Native AOT compatible); lifetime defaults to HandlerLifetime
    public MediatorOptions AddRequestHandler<TRequest, TResponse, THandler>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddRequestHandler<TRequest, THandler>(ServiceLifetime? lifetime = null); // void request
    public MediatorOptions AddStreamRequestHandler<TRequest, TResponse, THandler>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddNotificationHandler<TNotification, THandler>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddBehavior<TRequest, TResponse, TBehavior>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddStreamBehavior<TRequest, TResponse, TBehavior>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddOpenBehavior(Type openBehaviorType, ServiceLifetime? lifetime = null);
    public MediatorOptions AddOpenStreamBehavior(Type openBehaviorType, ServiceLifetime? lifetime = null);
    public MediatorOptions AddPreProcessor<TRequest, TProcessor>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddPostProcessor<TRequest, TResponse, TProcessor>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddExceptionHandler<TRequest, TResponse, TException, THandler>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddExceptionAction<TRequest, TException, TAction>(ServiceLifetime? lifetime = null);
    public MediatorOptions AddStreamExceptionHandler<TRequest, TResponse, TException, THandler>(ServiceLifetime? lifetime = null);

    // Notification publisher
    public MediatorOptions UseNotificationPublishStrategy(NotificationPublishStrategy strategy);
    public MediatorOptions UseNotificationPublisher<TPublisher>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TPublisher : class, INotificationPublisher;
    public MediatorOptions UseNotificationPublisher(INotificationPublisher publisher);
}
```

```csharp
public enum NotificationPublishStrategy
{
    Sequential = 0,
    Parallel = 1
}
```

## Runtime Types

```csharp
// Registers dispatch code ahead of time for a hand-built Mediator under Native AOT.
public static class MediatorRegistry
{
    public static void RegisterRequest<TRequest, TResponse>() where TRequest : IRequest<TResponse>;
    public static void RegisterVoidRequest<TRequest>() where TRequest : IRequest;
    public static void RegisterStreamRequest<TRequest, TResponse>() where TRequest : IStreamRequest<TResponse>;
    public static void RegisterNotification<TNotification>() where TNotification : INotification;
}
```

```csharp
public static class MediatorTelemetry
{
    public const string ActivitySourceName = "Nerdigy.Mediator";
    public const string MeterName = "Nerdigy.Mediator";
    public const string OperationDurationInstrument = "nerdigy.mediator.operation.duration";
    public const string ActiveOperationsInstrument = "nerdigy.mediator.operation.active";
    public const string PublishHandlerCountInstrument = "nerdigy.mediator.publish.handlers";
    // Tag names: OperationTag, MessageTypeTag, OutcomeTag, ExceptionHandledTag, HandlerCountTag
}
```
