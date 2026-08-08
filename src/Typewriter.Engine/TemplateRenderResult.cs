namespace Typewriter.Engine;

internal sealed record TemplateRenderResult(
    string Content,
    string? OutputPath,
    bool UsesOutputFilenameFactory,
    int RootItemCount,
    bool IsSingleFileMode,
    string OutputExtension,
    string? OutputDirectory,
    bool? Utf8Bom,
    bool? GenerateFileHeader,
    bool? InsertFinalNewline = null,
    int FinalNewlineCount = 1);
