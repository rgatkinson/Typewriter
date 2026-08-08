namespace Typewriter.Engine;

using Typewriter.Configuration;

internal sealed record TemplateRenderInspection(
    bool IsSingleFileMode,
    bool UsesOutputFilenameFactory,
    IReadOnlyList<string> IncludedProjects,
    PartialRenderingMode PartialRenderingMode);
