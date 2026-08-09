using System.Collections.Concurrent;
using System.Diagnostics;
using Typewriter.Abstractions;

namespace Typewriter.Buildalyzer;

/// <summary>
/// Wraps an <see cref="IProjectWorkspaceLoader" /> and pre-loads an entire project-reference graph
/// concurrently, then serves the sequential graph walk from the resulting cache.
/// </summary>
/// <remarks>
/// <para>
/// MSBuild evaluation dominates Typewriter's cold start: on a representative 9-project workspace it
/// accounted for ~19.7 s of a ~27.6 s metadata load, with each <c>analyzer.Build()</c> costing
/// 1.4-3.5 s. Sub-phase timings showed the loads ran strictly sequentially with essentially no
/// overlap, which is why the machine sat at low CPU across many cores: the phase was latency-bound,
/// not throughput-bound.
/// </para>
/// <para>
/// The metadata walk in <c>CSharpProjectMetadataProvider</c> cannot simply be parallelised: it uses
/// non-thread-safe collections and depends on referenced projects being fully built before the
/// referencing compilation is created. Rather than reorder that logic, this decorator performs a
/// separate discovery pass that dispatches every project the moment it is found, throttled only by
/// <c>maxConcurrency</c> — MSBuild design-time evaluation has no cross-project ordering constraint.
/// The sequential walk then finds every project already cached, so it keeps its existing ordering
/// and de-duplication semantics while paying only the critical-path cost of the load.
/// </para>
/// <para>
/// Prefetching is best-effort: a project that fails to load is evicted from the cache and its
/// failure is swallowed by the walk, so the authoritative <see cref="LoadAsync" /> path re-attempts
/// the load and surfaces the real error only for projects that are actually used.
/// </para>
/// </remarks>
public sealed class ParallelPrefetchProjectWorkspaceLoader : IProjectWorkspaceLoader
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProjectLoadResult>> _cache =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    private readonly Lock _versionGate = new();

    private readonly IProjectWorkspaceLoader _inner;
    private readonly int _maxConcurrency;
    private long _cachedVersion = -1;

    public ParallelPrefetchProjectWorkspaceLoader(IProjectWorkspaceLoader inner)
        : this(inner: inner, maxConcurrency: Environment.ProcessorCount)
    {
    }

    public ParallelPrefetchProjectWorkspaceLoader(IProjectWorkspaceLoader inner, int maxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(argument: inner);

        _inner = inner;
        _maxConcurrency = Math.Max(val1: 1, val2: maxConcurrency);
    }

    public async Task<ProjectLoadResult> LoadAsync(
        ProjectContext project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument: project);

        // The cached graph is only valid for a single metadata-invalidation generation. Watch mode
        // and the language server bump this version whenever an input changes, so without this the
        // loader would keep serving pre-change project data and regeneration would appear to hang.
        DiscardCacheIfStale();

        // The first call is the root of the graph walk, so use it to warm every reachable project
        // concurrently. Later calls for referenced projects are then served straight from the cache.
        await PrefetchGraphAsync(roots: [project], cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return await LoadCachedAsync(project: project, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Callers that render several top-level projects previously reached this loader one root at a
    /// time, which produced a series of narrow, strictly sequential fan-outs. On a representative
    /// workspace that meant three waves of 11.9 s, 8.0 s and 8.0 s, where the second and third waves
    /// covered only two projects each and so paid full out-of-process MSBuild latency at a width of
    /// two while most cores idled. Seeding one walk with every root lets unrelated subgraphs overlap,
    /// which reduces the floor to roughly the slowest single project.
    /// </remarks>
    public async Task PrefetchAsync(
        IReadOnlyList<ProjectContext> projects,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument: projects);

        if (projects.Count == 0)
        {
            return;
        }

        DiscardCacheIfStale();
        await PrefetchGraphAsync(roots: projects, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>
    /// Builds the cache identity of a load request.
    /// </summary>
    /// <remarks>
    /// The project path alone is not sufficient. A load result is shaped by the requested target
    /// framework (multi-targeting projects produce a different compilation per framework) and by the
    /// workspace root (which supplies the solution-level global properties), so two requests that
    /// differ in either must not share an entry. <c>RunFullDiagnostics</c> and
    /// <c>RunSourceGenerators</c> are deliberately excluded: they are consumed downstream by the
    /// Roslyn layer (which keys its own cache on them) and have no effect on the MSBuild evaluation
    /// this loader performs.
    /// </remarks>
    /// <param name="project">The load request to build a key for.</param>
    /// <returns>A key that is stable for equivalent requests. Compared with <see cref="StringComparer.OrdinalIgnoreCase" />.</returns>
    private static string CreateCacheKey(ProjectContext project) =>
        string.Join(
            separator: '|',
            Path.GetFullPath(path: project.ProjectPath),
            Path.GetFullPath(path: project.WorkspacePath),
            project.TargetFramework ?? string.Empty);

    private void DiscardCacheIfStale()
    {
        var currentVersion = MetadataCacheInvalidation.CurrentVersion;
        lock (_versionGate)
        {
            if (_cachedVersion == currentVersion)
            {
                return;
            }

            _cache.Clear();
            _cachedVersion = currentVersion;
        }
    }

    private async Task PrefetchGraphAsync(IReadOnlyList<ProjectContext> roots, CancellationToken cancellationToken)
    {
        // Dispatch each newly discovered project immediately instead of draining the graph one
        // breadth-first level at a time. Levels were an artifact of *discovery*, not of dependency:
        // MSBuild design-time evaluation of a project does not require its references to have been
        // evaluated first (that ordering constraint belongs to the Roslyn walk downstream). The old
        // barrier therefore charged the sum of per-level maxima. On a representative 9-project graph
        // that was 3.4 s + 2.9 s + 1.8 s = 8.1 s, where levels 0 and 2 held a single project each and
        // left the machine idle for 5.2 s of it. Fanning out continuously reduces the floor to the
        // single slowest project.
        var prefetchWatch = Stopwatch.StartNew();
        using var walk = new GraphPrefetchWalk(
            owner: this,
            roots: roots,
            maxConcurrency: _maxConcurrency,
            cancellationToken: cancellationToken);
        await walk.RunAsync().ConfigureAwait(continueOnCapturedContext: false);
        LoadProbe.ReportTiming(stage: "prefetch-total", name: Path.GetFileNameWithoutExtension(path: roots[0].ProjectPath), elapsed: prefetchWatch.Elapsed);
    }

    private Task<ProjectLoadResult> LoadCachedAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        var key = CreateCacheKey(project: project);

        // A completion source rather than the task itself: ConcurrentDictionary.GetOrAdd does not
        // guarantee that a value factory runs only once under contention, and every redundant
        // invocation here would be a full out-of-process MSBuild build -- precisely the cost this
        // class exists to remove. Publishing a cheap placeholder first and letting only the thread
        // that actually won the insertion start the load makes single execution explicit.
        var candidate = new TaskCompletionSource<ProjectLoadResult>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = _cache.GetOrAdd(key: key, value: candidate);
        if (ReferenceEquals(objA: entry, objB: candidate))
        {
#pragma warning disable MA0040 // The shared load is deliberately started without a caller token; see LoadAndPublishAsync.
            _ = Task.Run(function: () => LoadAndPublishAsync(key: key, project: project, completion: candidate));
#pragma warning restore MA0040
        }

        // Per-caller cancellation. The shared load itself is started without a caller token (see
        // LoadAndPublishAsync): the first requester's token must not decide the fate of a task that
        // later, unrelated requesters are awaiting. WaitAsync gives each caller its own observation
        // of the one shared load, so cancelling one generation cannot surface as a spurious
        // OperationCanceledException in another.
        return entry.Task.WaitAsync(cancellationToken: cancellationToken);
    }

    private async Task LoadAndPublishAsync(
        string key,
        ProjectContext project,
        TaskCompletionSource<ProjectLoadResult> completion)
    {
        try
        {
            // CancellationToken.None is deliberate: this task is shared by every caller that asks
            // for the same project within a cache generation, so binding it to whichever caller
            // happened to arrive first would let that caller's cancellation cancel the others'
            // work. Callers apply their own token via WaitAsync in LoadCachedAsync. The underlying
            // MsBuildProjectLoader runs a bounded, synchronous design-time build, so an abandoned
            // load completes shortly and populates the cache rather than leaking indefinitely.
            var result = await _inner.LoadAsync(project: project, cancellationToken: CancellationToken.None)
                .ConfigureAwait(continueOnCapturedContext: false);
            completion.SetResult(result: result);
        }
#pragma warning disable CA1031 // The exception is handed to every awaiter through the completion source.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // A faulted load must not stay cached: the cache lives until the next
            // metadata-invalidation version bump, so a transient failure would otherwise poison
            // every later request for this project within the same generation. Evicting lets the
            // next caller retry. Removal happens before the awaiters are released so that a caller
            // reacting to the failure cannot observe the stale entry. The unconditional TryRemove
            // can in principle race with a concurrent Clear+re-add, but removing a fresh entry only
            // costs one redundant load, never correctness.
            _ = _cache.TryRemove(key: key, value: out _);
            completion.SetException(exception: exception);
        }
    }

    /// <summary>
    /// Tracks the in-flight fan-out for a single graph prefetch.
    /// </summary>
    private sealed class GraphPrefetchWalk : IDisposable
    {
        private readonly ParallelPrefetchProjectWorkspaceLoader _owner;
        private readonly List<ProjectContext> _roots = [];
        private readonly CancellationToken _cancellationToken;
        private readonly SemaphoreSlim _throttle;
        private readonly Lock _gate = new();
        private readonly HashSet<string> _discovered;
        private readonly List<Task> _pending = [];

        public GraphPrefetchWalk(
            ParallelPrefetchProjectWorkspaceLoader owner,
            IReadOnlyList<ProjectContext> roots,
            int maxConcurrency,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _cancellationToken = cancellationToken;
            _throttle = new SemaphoreSlim(initialCount: maxConcurrency, maxCount: maxConcurrency);
            _discovered = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            // Roots can legitimately overlap (two rendered projects often share a dependency), so
            // de-duplicate here to keep the one-build-per-project guarantee the cache relies on.
            // The discovery set uses the same identity as the cache, so a project requested under
            // two different target frameworks is correctly treated as two distinct loads.
            _roots.AddRange(
                collection: roots.Where(predicate: root => _discovered.Add(item: CreateCacheKey(project: root))));
        }

        public async Task RunAsync()
        {
            foreach (var root in _roots)
            {
                Dispatch(project: root);
            }

            // Newly dispatched children extend the pending set while earlier tasks are still
            // running, so keep draining until a full pass adds nothing further.
            var completed = 0;
            while (true)
            {
                Task[] snapshot;
                lock (_gate)
                {
                    if (_pending.Count == completed)
                    {
                        break;
                    }

                    snapshot = [.. _pending.GetRange(index: completed, count: _pending.Count - completed)];
                    completed = _pending.Count;
                }

                await Task.WhenAll(tasks: snapshot).ConfigureAwait(continueOnCapturedContext: false);
            }
        }

        public void Dispose() => _throttle.Dispose();

        private void Dispatch(ProjectContext project)
        {
            var task = Task.Run(function: () => LoadAndFanOutAsync(project: project), cancellationToken: _cancellationToken);
            lock (_gate)
            {
                _pending.Add(item: task);
            }
        }

        private async Task LoadAndFanOutAsync(ProjectContext project)
        {
            await _throttle.WaitAsync(cancellationToken: _cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            ProjectLoadResult result;
            try
            {
                result = await _owner.LoadCachedAsync(project: project, cancellationToken: _cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Prefetching is best-effort; the authoritative load path re-raises real failures.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                // Prefetching is purely an optimization: template-literal scans can over-approximate
                // the set of projects, so a broken-but-unused project must not fail the walk (and,
                // via PrefetchAsync, the whole generation). The faulted task has already been
                // evicted from the cache, so the authoritative GetMetadataAsync/LoadAsync path will
                // re-attempt the load and surface the real error for projects that are actually used.
                LoadProbe.Report(
                    message: $"TWLOAD: prefetch-failed {Path.GetFileNameWithoutExtension(path: project.ProjectPath)}: {exception.Message}");
                return;
            }
            finally
            {
                _ = _throttle.Release();
            }

            // Queue references the moment this project resolves, so a deep chain overlaps with
            // unrelated work already in flight rather than waiting for a level barrier.
            foreach (var reference in result.ProjectReferences)
            {
                var referenceContext = new ProjectContext(
                    ProjectPath: reference,
                    WorkspacePath: project.WorkspacePath,
                    TargetFramework: project.TargetFramework ?? result.TargetFramework,
                    RunFullDiagnostics: project.RunFullDiagnostics,
                    RunSourceGenerators: project.RunSourceGenerators);

                lock (_gate)
                {
                    if (!_discovered.Add(item: CreateCacheKey(project: referenceContext)))
                    {
                        continue;
                    }
                }

                Dispatch(project: referenceContext);
            }
        }
    }
}
