using Typewriter.Abstractions;

namespace Typewriter.Buildalyzer;

public interface IProjectWorkspaceLoader
{
    Task<ProjectLoadResult> LoadAsync(
        ProjectContext project,
        CancellationToken cancellationToken);

    /// <summary>
    /// Warms the loader for several independent root projects at once.
    /// </summary>
    /// <remarks>
    /// Loaders that evaluate projects out of process pay a large fixed latency per project, so a
    /// caller that already knows it needs several unrelated roots can collapse those latencies by
    /// announcing them together instead of requesting them one at a time. Implementations must treat
    /// this as a pure optimization: it is optional, <see cref="LoadAsync" /> stays authoritative,
    /// and load failures must not propagate out of this method (callers may over-approximate the
    /// project set, so a broken-but-unused project must not fail the caller). Cancellation is the
    /// one exception and should surface as <see cref="OperationCanceledException" />.
    /// </remarks>
#pragma warning disable CC0091 // Default interface members cannot be static.
    Task PrefetchAsync(
        IReadOnlyList<ProjectContext> projects,
        CancellationToken cancellationToken) => Task.CompletedTask;
#pragma warning restore CC0091
}
