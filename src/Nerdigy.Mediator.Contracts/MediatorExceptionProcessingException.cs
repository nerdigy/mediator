namespace Nerdigy.Mediator.Contracts;

/// <summary>
/// The exception thrown when an exception handler, exception action, or stream cleanup fails while the mediator is
/// processing another exception.
/// </summary>
/// <remarks>
/// The exception that the request originally failed with is <see cref="Exception.InnerException"/>. The failures that
/// happened while processing it are in <see cref="ProcessingExceptions"/>. Every exception action still runs when an
/// earlier one fails.
/// </remarks>
public sealed class MediatorExceptionProcessingException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MediatorExceptionProcessingException"/> class.
    /// </summary>
    /// <param name="originalException">The exception the request originally failed with.</param>
    /// <param name="processingExceptions">The exceptions thrown while processing <paramref name="originalException"/>.</param>
    public MediatorExceptionProcessingException(Exception originalException, IReadOnlyList<Exception> processingExceptions)
        : base(CreateMessage(originalException, processingExceptions), originalException)
    {
        ProcessingExceptions = processingExceptions;
    }

    /// <summary>
    /// Gets the exceptions thrown by exception handlers, exception actions, or stream cleanup while processing
    /// <see cref="Exception.InnerException"/>.
    /// </summary>
    public IReadOnlyList<Exception> ProcessingExceptions { get; }

    /// <summary>
    /// Creates the exception message.
    /// </summary>
    /// <param name="originalException">The exception the request originally failed with.</param>
    /// <param name="processingExceptions">The exceptions thrown while processing <paramref name="originalException"/>.</param>
    /// <returns>The exception message.</returns>
    private static string CreateMessage(Exception originalException, IReadOnlyList<Exception> processingExceptions)
    {
        ArgumentNullException.ThrowIfNull(originalException);
        ArgumentNullException.ThrowIfNull(processingExceptions);

        var failures = string.Join("; ", processingExceptions.Select(static exception => $"{exception.GetType().Name}: {exception.Message}"));

        return $"The request failed with {originalException.GetType().Name} ('{originalException.Message}'), and {processingExceptions.Count} " +
            $"exception processing step(s) also failed: {failures}. The original exception is the InnerException.";
    }
}