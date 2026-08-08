namespace Typewriter.Engine;

internal static class TypeScriptTemporalTypes
{
    public static bool IsDateTime(string fullName) =>
        fullName.Equals(value: "System.DateTime", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "System.DateTimeOffset", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.Instant", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.LocalDateTime", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.OffsetDateTime", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.ZonedDateTime", comparisonType: StringComparison.Ordinal);

    public static bool IsDateOnly(string fullName) =>
        fullName.Equals(value: "System.DateOnly", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.LocalDate", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.OffsetDate", comparisonType: StringComparison.Ordinal);

    public static bool IsTimeOnly(string fullName) =>
        fullName.Equals(value: "System.TimeOnly", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.LocalTime", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.OffsetTime", comparisonType: StringComparison.Ordinal);

    public static bool IsDuration(string fullName) =>
        fullName.Equals(value: "System.TimeSpan", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.Duration", comparisonType: StringComparison.Ordinal)
        || fullName.Equals(value: "NodaTime.Period", comparisonType: StringComparison.Ordinal);

    /// <summary>
    /// Determines the value of the legacy <c>Type.IsDate</c> template property.
    /// </summary>
    /// <remarks>
    /// The metadata-level <c>IsDateLike</c> flag deliberately covers every temporal type,
    /// including durations such as <see cref="System.TimeSpan"/>. The legacy template surface
    /// treats durations separately via <c>IsTimeSpan</c>, and templates commonly test
    /// <c>IsDate</c> first, so durations must not report themselves as dates.
    /// </remarks>
    /// <param name="isDateLike">The metadata-level date-like flag for the type.</param>
    /// <param name="fullName">The fully qualified name of the type.</param>
    /// <returns><see langword="true"/> when the type is a date/time but not a duration.</returns>
    public static bool IsLegacyDate(bool isDateLike, string fullName) =>
        isDateLike && !IsDuration(fullName: fullName);

    public static string FormatTimeOnlyInitializer(
        string initializer,
        char stringLiteralCharacter) =>
        initializer.Equals(value: TypeScriptTypeMapper.DefaultTimeOnlyInitializer, comparisonType: StringComparison.Ordinal)
            ? $"{stringLiteralCharacter}00:00:00{stringLiteralCharacter}"
            : initializer;
}
