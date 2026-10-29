using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

using Nerdigy.Mediator.Contracts;

namespace Nerdigy.Mediator;

/// <summary>
/// Provides rules shared by request and stream exception processing.
/// </summary>
internal static class ExceptionProcessing
{
    /// <summary>
    /// Determines whether an exception is the caller's own cancellation, which exception handlers must not turn into a result.
    /// </summary>
    /// <param name="exception">The thrown exception.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the caller canceled and the exception is a cancellation.</returns>
    public static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Throws the original exception, or a <see cref="MediatorExceptionProcessingException"/> when processing it failed.
    /// </summary>
    /// <param name="exception">The exception the request failed with.</param>
    /// <param name="processingFailures">The exceptions thrown while processing it, if any.</param>
    [DoesNotReturn]
    public static void Throw(Exception exception, List<Exception>? processingFailures)
    {
        if (processingFailures is { Count: > 0 })
        {
            throw new MediatorExceptionProcessingException(exception, processingFailures);
        }

        ExceptionDispatchInfo.Throw(exception);
    }

    /// <summary>
    /// Enumerates an exception type and its base exception types from most specific to least specific.
    /// </summary>
    /// <param name="exceptionType">The concrete thrown exception type.</param>
    /// <returns>An ordered sequence of compatible exception types.</returns>
    public static IEnumerable<Type> EnumerateExceptionTypeHierarchy(Type exceptionType)
    {
        for (var currentType = exceptionType; currentType is not null && typeof(Exception).IsAssignableFrom(currentType); currentType = currentType.BaseType)
        {
            yield return currentType;
        }
    }
}

/// <summary>
/// Tracks which exception component implementations already ran for one exception.
/// </summary>
/// <remarks>
/// Exception components are resolved for each level of the exception's type hierarchy. An open generic component such
/// as <c>LogAll&lt;TRequest, TException&gt;</c> closes at every level, and a class can implement several levels. Each
/// implementation runs once, at the most specific level it matches, like a <c>catch</c> block.
/// </remarks>
internal sealed class InvokedComponentSet
{
    private HashSet<Type>? _implementations;

    /// <summary>
    /// Records a component and reports whether its implementation has not run yet.
    /// </summary>
    /// <param name="component">The resolved component.</param>
    /// <returns><see langword="true"/> when the component should run; <see langword="false"/> when its implementation already ran.</returns>
    public bool Add(object component)
    {
        var implementationType = component.GetType();

        if (implementationType.IsGenericType)
        {
            implementationType = implementationType.GetGenericTypeDefinition();
        }

        return (_implementations ??= []).Add(implementationType);
    }
}

/// <summary>
/// Runs exception actions for a request or stream request type.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
internal static class ExceptionActionProcessor<TRequest>
    where TRequest : IBaseRequest
{
    private static readonly ConcurrentDictionary<Type, ExceptionActionInvoker<TRequest>[]> s_invokers = new();

    /// <summary>
    /// Runs every exception action that matches the exception, even when an earlier action fails.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve actions.</param>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception the request failed with.</param>
    /// <param name="processingFailures">Failures collected earlier in exception processing, if any.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running actions.</param>
    /// <returns><paramref name="processingFailures"/> with any action failures added.</returns>
    public static async Task<List<Exception>?> Execute(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        List<Exception>? processingFailures,
        CancellationToken cancellationToken)
    {
        var invokers = s_invokers.GetOrAdd(exception.GetType(), static exceptionType => BuildInvokers(exceptionType));
        var invoked = new InvokedComponentSet();

        foreach (var invoker in invokers)
        {
            processingFailures = await invoker.ExecuteAll(
                serviceProvider,
                request,
                exception,
                invoked,
                processingFailures,
                cancellationToken).ConfigureAwait(false);
        }

        return processingFailures;
    }

    /// <summary>
    /// Builds action invokers for the exception type hierarchy.
    /// </summary>
    /// <param name="exceptionType">The concrete thrown exception type.</param>
    /// <returns>Action invokers ordered from most specific to least specific exception type.</returns>
    private static ExceptionActionInvoker<TRequest>[] BuildInvokers(Type exceptionType)
    {
        return
        [
            .. ExceptionProcessing.EnumerateExceptionTypeHierarchy(exceptionType)
                .Select(static type => type == typeof(Exception)
                    ? new ExceptionActionInvoker<TRequest, Exception>()
                    : GenericInvokerFactory.Create<ExceptionActionInvoker<TRequest>>(
                        typeof(ExceptionActionInvoker<,>),
                        typeof(TRequest),
                        type))
        ];
    }
}

/// <summary>
/// Runs the exception actions registered for one exception type.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
internal abstract class ExceptionActionInvoker<TRequest>
    where TRequest : IBaseRequest
{
    /// <summary>
    /// Runs every action registered for this exception type that has not already run.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve actions.</param>
    /// <param name="request">The request that failed.</param>
    /// <param name="exception">The exception the request failed with.</param>
    /// <param name="invoked">The implementations that already ran for this exception.</param>
    /// <param name="processingFailures">Failures collected so far, if any.</param>
    /// <param name="cancellationToken">A cancellation token that can be observed while running actions.</param>
    /// <returns><paramref name="processingFailures"/> with any action failures added.</returns>
    public abstract Task<List<Exception>?> ExecuteAll(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        InvokedComponentSet invoked,
        List<Exception>? processingFailures,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the exception actions registered for <typeparamref name="TException"/>.
/// </summary>
/// <typeparam name="TRequest">The concrete request type.</typeparam>
/// <typeparam name="TException">The exception type at this level of the hierarchy.</typeparam>
internal sealed class ExceptionActionInvoker<TRequest, TException> : ExceptionActionInvoker<TRequest>
    where TRequest : IBaseRequest
    where TException : Exception
{
    /// <inheritdoc />
    public override async Task<List<Exception>?> ExecuteAll(
        IServiceProvider serviceProvider,
        TRequest request,
        Exception exception,
        InvokedComponentSet invoked,
        List<Exception>? processingFailures,
        CancellationToken cancellationToken)
    {
        IRequestExceptionAction<TRequest, TException>[] actions;

        try
        {
            actions = ServiceProviderUtilities.GetServices<IRequestExceptionAction<TRequest, TException>>(serviceProvider);
        }
        catch (Exception resolutionFailure)
        {
            (processingFailures ??= []).Add(resolutionFailure);

            return processingFailures;
        }

        foreach (var action in actions)
        {
            if (!invoked.Add(action))
            {
                continue;
            }

            try
            {
                await action.Execute(request, (TException)exception, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception actionFailure)
            {
                (processingFailures ??= []).Add(actionFailure);
            }
        }

        return processingFailures;
    }
}