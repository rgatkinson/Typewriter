using System.Text.Json.Serialization;

namespace Typewriter.Abstractions;

public sealed record GenerationConfiguration(string Incremental)
{
    public const string IncrementalAuto = "auto";
    public const string IncrementalOff = "off";

    public static GenerationConfiguration Default { get; } = new(Incremental: IncrementalAuto);

    /// <summary>
    /// Gets a value indicating whether source generators are executed while building project metadata.
    /// </summary>
    /// <remarks>
    /// Generator output is not visible to templates: <c>GetSourceSymbols</c> enumerates only the syntax
    /// trees parsed from disk, so no generated type ever becomes a template entity. Running generators
    /// still affects semantic resolution of hand-written code that references generated symbols, and it
    /// contributes generator diagnostics, so the default remains <see langword="true" />. Disabling this
    /// removes a substantial share of metadata-load time for projects with expensive generators.
    /// </remarks>
    public bool RunSourceGenerators { get; init; } = true;

    [JsonIgnore]
    public bool IsIncrementalEnabled =>
        !IncrementalOff.Equals(value: Incremental, comparisonType: StringComparison.OrdinalIgnoreCase);
}
