namespace Nerdigy.Mediator.Abstractions;

/// <summary>
/// Represents a void-like return value for request types that do not produce a payload.
/// </summary>
public readonly struct Unit : IEquatable<Unit>
{
    /// <summary>
    /// Gets the singleton <see cref="Unit"/> value.
    /// </summary>
    public static readonly Unit Value = default;

    /// <summary>
    /// Determines whether the current value equals the specified object.
    /// </summary>
    /// <param name="obj">The object to compare against.</param>
    /// <returns><see langword="true"/> when the object is a <see cref="Unit"/>; otherwise <see langword="false"/>.</returns>
    public override bool Equals(object? obj)
    {

        return obj is Unit;
    }

    /// <summary>
    /// Determines whether the current value equals another <see cref="Unit"/> value.
    /// </summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns>Always <see langword="true"/> because <see cref="Unit"/> has a single possible value.</returns>
    public bool Equals(Unit other)
    {

        return true;
    }

    /// <summary>
    /// Returns the hash code for the current value.
    /// </summary>
    /// <returns>Always <c>0</c>.</returns>
    public override int GetHashCode()
    {

        return 0;
    }

    /// <summary>
    /// Determines whether two <see cref="Unit"/> values are equal.
    /// </summary>
    /// <param name="left">The first value to compare.</param>
    /// <param name="right">The second value to compare.</param>
    /// <returns>Always <see langword="true"/>.</returns>
    public static bool operator ==(Unit left, Unit right)
    {

        return true;
    }

    /// <summary>
    /// Determines whether two <see cref="Unit"/> values are not equal.
    /// </summary>
    /// <param name="left">The first value to compare.</param>
    /// <param name="right">The second value to compare.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    public static bool operator !=(Unit left, Unit right)
    {

        return false;
    }

    /// <summary>
    /// Returns a string representation of the current value.
    /// </summary>
    /// <returns>The string <c>Unit</c>.</returns>
    public override string ToString()
    {

        return nameof(Unit);
    }
}
