namespace Typewriter.Abstractions;

public sealed record GeneratedFile(
    string Path,
    string Content,
    bool Changed,
    bool? Utf8Bom = null)
{
    public string? Diff { get; init; }

    /// <summary>
    /// Gets the template-level override for <c>output.insertFinalNewline</c>, or
    /// <see langword="null"/> when the configured value applies.
    /// </summary>
    public bool? InsertFinalNewline { get; init; }
}
