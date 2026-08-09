namespace Typewriter.Abstractions;

public interface IProjectMetadataProvider
{
    Task<ProjectMetadata> GetMetadataAsync(
        ProjectContext project,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hints that metadata for several projects will be requested, so the provider can warm them together.
    /// </summary>
    /// <remarks>
    /// Purely an optimization; <see cref="GetMetadataAsync" /> remains authoritative and callers stay
    /// correct if this does nothing. Implementations must not let load failures propagate: callers
    /// may over-approximate the project set (for example from a template-literal scan), so a
    /// broken-but-unused project must not fail the caller. Cancellation is the one exception and
    /// should surface as <see cref="OperationCanceledException" />.
    /// </remarks>
#pragma warning disable CC0091 // Default interface members cannot be static.
    Task PrefetchAsync(
        IReadOnlyList<ProjectContext> projects,
        CancellationToken cancellationToken) => Task.CompletedTask;
#pragma warning restore CC0091
}
