using Typewriter.Abstractions;

namespace Typewriter.Engine;

/// <summary>
/// Formats the C#-facing name of a type reference the way Typewriter v3.0.1 did.
/// </summary>
/// <remarks>
/// <para>
/// v3.0.1 surfaced nullability in the C# name on two levels. <c>RoslynTypeMetadata.Name</c> and
/// <c>RoslynTypeMetadata.FullName</c> appended <c>"?"</c> whenever <c>IsNullable</c> was set, and
/// <c>Helpers.GetOriginalName</c> additionally mapped well-known BCL types to their C# keyword
/// (<c>System.Int32</c> to <c>int</c>) before re-appending the suffix. Templates therefore read
/// <c>$OriginalName</c> as a name they could paste straight back into C# source.
/// </para>
/// <para>
/// The v4 rewrite unwraps <c>Nullable&lt;T&gt;</c> to its underlying type and keeps nullability
/// only on <see cref="TypeMetadataReference.IsNullable"/>, so the suffix disappeared. That loss is
/// silent -- emitting <c>FacilityId</c> where <c>FacilityId?</c> was meant still compiles -- and it
/// strips optionality from generated API clients. This helper restores the v3 contract in one place
/// so every template-facing surface stays consistent.
/// </para>
/// <para>
/// The suffix is deliberately confined to the C#-facing name. <c>Type.Name</c> is the TypeScript
/// name and expresses nullability through the <c>StrictNullGeneration</c> <c>" | null"</c>
/// convention, and <see cref="TypeMetadataReference.FullName"/> stays unsuffixed because it is the
/// key used for metadata lookups and ordinal type comparisons throughout the engine.
/// </para>
/// <para>
/// This asymmetry is intentional and matches v3.0.1 exactly. In v3 the Roslyn metadata layer
/// appended the suffix to both <c>Name</c> and <c>FullName</c>, and the template-facing
/// <c>TypeImpl.Name</c> then trimmed it back off via <c>Helpers.GetTypeScriptName</c>:
/// <code>
/// return metadata.IsNullable
///     ? settings.StrictNullGeneration ? $"{metadata.Name.TrimEnd('?')} | null" : $"{metadata.Name.TrimEnd('?')}"
///     : metadata.Name;
/// </code>
/// So <c>$Name</c> rendered without the suffix while <c>$OriginalName</c> and <c>$FullName</c> kept
/// it. A trailing <c>?</c> is not valid TypeScript in that position, whereas <c>| null</c> is the
/// TypeScript spelling of the same idea.
/// </para>
/// </remarks>
internal static class CSharpTypeNameFormatter
{
    /// <summary>
    /// The v3.0.1 <c>Helpers._primitiveTypes</c> map, reproduced verbatim. It is intentionally
    /// case-insensitive and intentionally includes the non-keyword types (<c>DateTime</c>,
    /// <c>Guid</c>, ...) that v3 echoed back under their short name.
    /// </summary>
    private static readonly Dictionary<string, string> PrimitiveTypeNames =
        new(comparer: StringComparer.OrdinalIgnoreCase)
        {
            { "System.Boolean", "bool" },
            { "System.Byte", "byte" },
            { "System.Char", "char" },
            { "System.Decimal", "decimal" },
            { "System.Double", "double" },
            { "System.Int16", "short" },
            { "System.Int32", "int" },
            { "System.Int64", "long" },
            { "System.SByte", "sbyte" },
            { "System.Single", "float" },
            { "System.String", "string" },
            { "System.UInt16", "ushort" },
            { "System.UInt32", "uint" },
            { "System.UInt64", "ulong" },
            { "System.DateTime", "DateTime" },
            { "System.DateTimeOffset", "DateTimeOffset" },
            { "System.Guid", "Guid" },
            { "System.TimeSpan", "TimeSpan" },
        };

    /// <summary>
    /// Gets the v3.0.1-compatible C# name of <paramref name="type"/>, including the trailing
    /// <c>"?"</c> when the type is nullable. This is the value exposed to templates as
    /// <c>$OriginalName</c>.
    /// </summary>
    /// <param name="type">The type reference to name.</param>
    /// <returns>The C# name, suffixed with <c>"?"</c> when the type is nullable.</returns>
    public static string GetOriginalName(TypeMetadataReference type)
    {
        // v3 trimmed a pre-existing '?' before probing the primitive map because its FullName
        // already carried the suffix. v4 full names never do, but trimming keeps the lookup
        // correct for callers that construct metadata by hand.
        var fullName = TrimNullableSuffix(name: type.FullName);
        var name = PrimitiveTypeNames.TryGetValue(key: fullName, value: out var primitiveName)
            ? primitiveName
            : type.Name;

        return AppendNullableSuffix(name: name, isNullable: type.IsNullable);
    }

    /// <summary>
    /// Appends the nullable <c>"?"</c> suffix to <paramref name="name"/> when
    /// <paramref name="isNullable"/> is set, without producing a doubled <c>"??"</c> for names that
    /// already carry the suffix.
    /// </summary>
    /// <param name="name">The name to suffix.</param>
    /// <param name="isNullable">Whether the type is nullable.</param>
    /// <returns>The name, suffixed with <c>"?"</c> when required.</returns>
    public static string AppendNullableSuffix(string name, bool isNullable)
    {
        if (!isNullable
            || name.Length == 0
            || name[name.Length - 1] == '?')
        {
            return name;
        }

        return name + "?";
    }

    /// <summary>
    /// Removes a trailing nullable <c>"?"</c> so the underlying name can be used for lookups and
    /// ordinal comparisons.
    /// </summary>
    /// <param name="name">The name to trim.</param>
    /// <returns>The name without any trailing <c>"?"</c>.</returns>
    public static string TrimNullableSuffix(string name) => name.TrimEnd('?');
}
