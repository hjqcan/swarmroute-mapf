namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// Internal guard helpers shared by the Dispatch domain value records so each record's primary constructor can
/// enforce its invariants in one expression and throw <see cref="ArgumentException"/> on a breach
/// (grukirbs validated-ctor convention). Not part of the public contract.
/// </summary>
internal static class Validation
{
    /// <summary>Returns <paramref name="value"/> if non-null, else throws <see cref="ArgumentNullException"/>.</summary>
    internal static T NotNull<T>(T value, string paramName) where T : class
        => value ?? throw new ArgumentNullException(paramName);

    /// <summary>Returns <paramref name="value"/> if non-null and not blank, else throws <see cref="ArgumentException"/>.</summary>
    internal static string NotNullOrWhiteSpace(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value must be a non-empty, non-whitespace string.", paramName);

        return value;
    }

    /// <summary>Returns <paramref name="value"/> if strictly positive, else throws <see cref="ArgumentException"/>.</summary>
    internal static long Positive(long value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentException($"Value must be > 0, but was {value}.", paramName);

        return value;
    }

    /// <summary>Returns <paramref name="value"/> if non-negative, else throws <see cref="ArgumentException"/>.</summary>
    internal static long NotNegative(long value, string paramName)
    {
        if (value < 0)
            throw new ArgumentException($"Value must be >= 0, but was {value}.", paramName);

        return value;
    }

    /// <summary>Returns <paramref name="value"/> if non-negative, else throws <see cref="ArgumentException"/>.</summary>
    internal static int NotNegative(int value, string paramName)
    {
        if (value < 0)
            throw new ArgumentException($"Value must be >= 0, but was {value}.", paramName);

        return value;
    }

    /// <summary>
    /// Returns <paramref name="deadlineMs"/> unchanged when it is <see langword="null"/> or at/after
    /// <paramref name="earliestStartMs"/>; otherwise throws <see cref="ArgumentException"/>.
    /// </summary>
    internal static long? DeadlineAtOrAfter(long? deadlineMs, long earliestStartMs, string paramName)
    {
        if (deadlineMs is { } d && d < earliestStartMs)
            throw new ArgumentException(
                $"Deadline ({d}) must be >= earliest start ({earliestStartMs}).", paramName);

        return deadlineMs;
    }
}
