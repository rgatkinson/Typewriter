namespace Typewriter.Abstractions;

public sealed record TypeMetadataReference(
    string Name,
    string FullName,
    string Namespace,
    bool IsNullable,
    bool IsCollection,
    bool IsDictionary,
    bool IsEnum,
    bool IsPrimitive,
    bool IsDateLike,
    TypeMetadataReference? ElementType,
    IReadOnlyList<TypeMetadataReference> TypeArguments)
{
    public string AssemblyName { get; init; } = string.Empty;

    public bool IsValueTuple { get; init; }

    /// <summary>
    /// Gets a value indicating whether this reference originated from an awaitable
    /// (<c>Task</c>/<c>ValueTask</c>) type. The reference itself describes the awaited
    /// result type, matching the pre-4.x code model contract.
    /// </summary>
    public bool IsTask { get; init; }

    public IReadOnlyList<FieldMetadata> TupleElements { get; init; } = [];

    public IReadOnlyList<EnumValueMetadata> EnumValues { get; init; } = [];
}
