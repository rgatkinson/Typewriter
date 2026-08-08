using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Typewriter.Abstractions;
using Typewriter.Configuration;

namespace Typewriter.Engine;

public sealed class TypewriterGenerator : ITypewriterGenerator
{
    private const char IncludedProjectCacheKeySeparator = '\0';
    private const int MaxTemplateDocumentCacheEntries = 256;
    private static readonly ConcurrentDictionary<TemplateDocumentCacheKey, TemplateDocumentCacheEntry> TemplateDocumentCache = new();
    private static readonly ConcurrentQueue<TemplateDocumentCacheKey> TemplateDocumentCacheOrder = new();
    private readonly IGeneratedFileWriter _fileWriter;
    private readonly IProjectMetadataProvider _metadataProvider;
    private readonly GeneratedFilePlanner _planner;
    private readonly ITemplateDiscovery _templateDiscovery;
    private readonly TemplateRenderer _templateRenderer;

    public TypewriterGenerator(
        ITemplateDiscovery templateDiscovery,
        IProjectMetadataProvider metadataProvider,
        IGeneratedFileWriter fileWriter)
        : this(
            templateDiscovery: templateDiscovery,
            metadataProvider: metadataProvider,
            fileWriter: fileWriter,
            templateRenderer: new TemplateRenderer(typeMapper: new TypeScriptTypeMapper()),
            planner: new GeneratedFilePlanner())
    {
    }

    public TypewriterGenerator(
        ITemplateDiscovery templateDiscovery,
        IProjectMetadataProvider metadataProvider,
        IGeneratedFileWriter fileWriter,
        TemplateRenderer templateRenderer,
        GeneratedFilePlanner planner)
    {
        _templateDiscovery = templateDiscovery;
        _metadataProvider = metadataProvider;
        _fileWriter = fileWriter;
        _templateRenderer = templateRenderer;
        _planner = planner;
    }

    public async Task<GenerationResult> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argument: request);

        var stopwatch = Stopwatch.StartNew();
        var performanceTrace = GenerationPerformanceTrace.Create();
        var diagnostics = new List<GenerationDiagnostic>();
        var generatedFiles = new List<GeneratedFile>();
        var plannedOutputPaths = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        var resolutionStopwatch = Stopwatch.StartNew();
        var workspace = new WorkspaceContext(RootPath: WorkspaceProjectResolver.ResolveWorkspacePath(request: request));
        var projectPaths = WorkspaceProjectResolver.ResolveProjectPaths(request: request, workspacePath: workspace.RootPath, diagnostics: diagnostics);
        performanceTrace.Add(stage: "Workspace/project resolution", elapsed: resolutionStopwatch.Elapsed);
        if (projectPaths.Count == 0)
        {
            return CreateResult(stopwatch: stopwatch, generatedFiles: generatedFiles, diagnostics: diagnostics, request: request, performanceTrace: performanceTrace);
        }

        var defaultsStopwatch = Stopwatch.StartNew();
        var renderDefaults = TemplateRenderDefaults.FromConfiguration(
            configuration: request.Configuration,
            solutionFullName: ResolveSolutionFullName(workspaceRootPath: workspace.RootPath));
        performanceTrace.Add(stage: "Render default resolution", elapsed: defaultsStopwatch.Elapsed);
        var incrementalChangedSourcePaths = ResolveIncrementalChangedSourcePaths(request: request);
        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GenerateProjectAsync(
                request: request,
                workspace: workspace,
                templateWorkspace: CreateProjectWorkspace(request: request, workspace: workspace, projectPath: projectPath, projectCount: projectPaths.Count),
                projectPath: projectPath,
                renderDefaults: renderDefaults,
                incrementalChangedSourcePaths: incrementalChangedSourcePaths,
                diagnostics: diagnostics,
                generatedFiles: generatedFiles,
                plannedOutputPaths: plannedOutputPaths,
                performanceTrace: performanceTrace,
                cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }

        return CreateResult(stopwatch: stopwatch, generatedFiles: generatedFiles, diagnostics: diagnostics, request: request, performanceTrace: performanceTrace);
    }

    private static void ReportDuplicateOutputPath(
        TemplateDocument template,
        GeneratedFile plannedFile,
        ICollection<GenerationDiagnostic> diagnostics,
        IDictionary<string, string> plannedOutputPaths)
    {
        var normalizedPath = Path.GetFullPath(path: plannedFile.Path);
        if (!plannedOutputPaths.TryGetValue(key: normalizedPath, value: out var firstTemplatePath))
        {
            plannedOutputPaths[key: normalizedPath] = template.Path;
            return;
        }

        var message = string.Equals(a: firstTemplatePath, b: template.Path, comparisonType: StringComparison.OrdinalIgnoreCase)
            ? $"Template '{template.Path}' generates the output file '{normalizedPath}' more than once. The last render wins."
            : $"Output file '{normalizedPath}' is generated by more than one template: '{firstTemplatePath}' and '{template.Path}'. The last render wins.";
        diagnostics.Add(
            item: new GenerationDiagnostic(
                File: template.Path,
                Line: null,
                Column: null,
                Severity: DiagnosticSeverity.Warning,
                Message: message,
                Code: DiagnosticCodes.DuplicateGeneratedOutput));
    }

    private static void ReportUnresolvedIncludedProjects(
        TemplateDocument template,
        IEnumerable<string> unresolvedNames,
        ICollection<GenerationDiagnostic> diagnostics)
    {
        foreach (var unresolvedName in unresolvedNames.Distinct(comparer: StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(
                item: new GenerationDiagnostic(
                    File: template.Path,
                    Line: null,
                    Column: null,
                    Severity: DiagnosticSeverity.Warning,
                    Message: $"Project '{unresolvedName}' passed to Settings.IncludeProject was not found in the workspace.",
                    Code: DiagnosticCodes.IncludedProjectNotFound));
        }
    }

    private static bool ShouldRenderPerSourceFile(
        TemplateDocument template,
        ProjectMetadata project,
        TemplateRenderInspection? inspection = null)
    {
        return template.OutputPath is null
            && inspection?.IsSingleFileMode != true
            && project.SourceFiles.Any(predicate: sourceFile => sourceFile.Types.Count > 0);
    }

    /// <summary>
    /// Resolves the changed source-file paths usable for incremental per-source-file
    /// rendering. Returns <c>null</c> when a full generation is required: unknown
    /// provenance, incremental generation disabled, validation mode, deleted or renamed
    /// inputs, or any changed input that is not a C# source file (templates, project
    /// files, configuration, and other shape inputs always trigger a full generation).
    /// </summary>
    /// <param name="request">The generation request.</param>
    /// <returns>The full paths of the changed source files, or <c>null</c>.</returns>
    private static IReadOnlyCollection<string>? ResolveIncrementalChangedSourcePaths(GenerationRequest request)
    {
        if (request.ChangedInputs is null
            || request.ChangedInputs.Count == 0
            || request.Mode != GenerationMode.Generate
            || !request.Configuration.Generation.IsIncrementalEnabled)
        {
            return null;
        }

        var changedSourcePaths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        foreach (var changedInput in request.ChangedInputs)
        {
            if (changedInput.Kind is ChangedInputKind.Deleted or ChangedInputKind.Renamed
                || !Path.GetExtension(path: changedInput.FullPath).Equals(value: ".cs", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                _ = changedSourcePaths.Add(item: Path.GetFullPath(path: changedInput.FullPath));
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (PathTooLongException)
            {
                return null;
            }
        }

        return changedSourcePaths;
    }

    private static IReadOnlyList<SourceFileRenderContext> FilterSourceFileRenderContexts(
        IReadOnlyList<SourceFileRenderContext> renderContexts,
        ProjectMetadataIndex metadataIndex,
        IReadOnlyCollection<string>? incrementalChangedSourcePaths)
    {
        if (incrementalChangedSourcePaths is null || renderContexts.Count == 0)
        {
            return renderContexts;
        }

        var affectedSourceFiles = metadataIndex.GetAffectedSourceFiles(changedSourcePaths: incrementalChangedSourcePaths);
        if (affectedSourceFiles.Count == 0)
        {
            return [];
        }

        var affectedSourceFileSet = new HashSet<string>(collection: affectedSourceFiles, comparer: StringComparer.OrdinalIgnoreCase);
        return renderContexts
            .Where(predicate: renderContext => affectedSourceFileSet.Contains(item: renderContext.SourceFile.Path))
            .ToArray();
    }

    private static bool RequiresSingleFileModeInspection(TemplateDocument template) =>
        template.OutputPath is null
        && template.CodeBlocks.Any(predicate: block => block.Content.Contains(value: "SingleFileMode", comparisonType: StringComparison.Ordinal));

    private static bool RequiresIncludedProjectsInspection(TemplateDocument template) =>
        template.CodeBlocks.Any(predicate: block => block.Content.Contains(value: "IncludeProject", comparisonType: StringComparison.Ordinal));

    // A template that opts into PartialRenderingMode.Combined must be inspected so the setting is
    // known before render contexts are built. Templates that say nothing keep the Partial default,
    // which needs no inspection.
    private static bool RequiresPartialRenderingModeInspection(TemplateDocument template) =>
        template.CodeBlocks.Any(predicate: block => block.Content.Contains(value: nameof(PartialRenderingMode), comparisonType: StringComparison.Ordinal));

    private static TemplateRenderResult ApplyLegacySourceOutputPath(
        SourceFileMetadata sourceFile,
        TemplateRenderResult renderResult,
        FileNameConvention fileNameConvention)
    {
        if (!string.IsNullOrWhiteSpace(value: renderResult.OutputPath))
        {
            return renderResult;
        }

        var outputFileName = FileNameConventionFormatter.Format(
            value: Path.GetFileNameWithoutExtension(path: sourceFile.Path),
            convention: fileNameConvention)
            + NormalizeOutputExtension(outputExtension: renderResult.OutputExtension);
        var outputPath = string.IsNullOrWhiteSpace(value: renderResult.OutputDirectory)
            ? outputFileName
            : Path.Combine(path1: renderResult.OutputDirectory, path2: outputFileName);

        return renderResult with { OutputPath = outputPath };
    }

    private static string NormalizeOutputExtension(string? outputExtension)
    {
        if (string.IsNullOrWhiteSpace(value: outputExtension))
        {
            return ".ts";
        }

        return outputExtension.StartsWith(value: '.')
            ? outputExtension
            : "." + outputExtension;
    }

    private static ProjectMetadata CreateSourceFileProject(
        ProjectMetadata project,
        SourceFileMetadata sourceFile)
    {
        return new ProjectMetadata(
            ProjectPath: project.ProjectPath,
            SourceFiles: [sourceFile],
            Types: sourceFile.Types,
            Diagnostics: []);
    }

    private static IReadOnlyList<SourceFileRenderContext> CreateSourceFileRenderContexts(
        ProjectMetadata project,
        ProjectMetadataIndex metadataIndex,
        PartialRenderingMode partialRenderingMode)
    {
        var contexts = new List<SourceFileRenderContext>();
        foreach (var sourceFile in project.SourceFiles.Where(predicate: sourceFile => sourceFile.Types.Count > 0))
        {
            var renderedFile = ApplyPartialRenderingMode(
                sourceFile: sourceFile,
                partialRenderingMode: partialRenderingMode);
            if (renderedFile.Types.Count == 0)
            {
                continue;
            }

            var sourceProject = CreateSourceFileProject(project: project, sourceFile: renderedFile);
            contexts.Add(
                item: new SourceFileRenderContext(
                    SourceFile: renderedFile,
                    Project: sourceProject,
                    MetadataIndex: metadataIndex));
        }

        return contexts;
    }

    /// <summary>
    /// Applies the template's <see cref="PartialRenderingMode"/> to a single source file's view
    /// of the types it declares.
    /// </summary>
    /// <param name="sourceFile">The source file whose type view is being narrowed.</param>
    /// <param name="partialRenderingMode">The rendering mode configured by the template.</param>
    /// <returns>The source file, with partial types narrowed to this file's own members.</returns>
    /// <remarks>
    /// <para>
    /// A partial type is declared across several files and appears in the metadata of each of
    /// them, carrying the complete, merged member list every time. That is what
    /// <see cref="PartialRenderingMode.Combined"/> wants, but it is not the default.
    /// </para>
    /// <para>
    /// Under the default <see cref="PartialRenderingMode.Partial"/>, each declaring file must
    /// render only the members it actually declares, so a partial class split across N files
    /// produces N outputs that together cover the type exactly once. Without this filtering every
    /// one of those N outputs repeats the entire type, duplicating members N times.
    /// </para>
    /// <para>
    /// Members are attributed by their recorded source location. Members with no location (for
    /// example compiler-synthesized ones) are kept on the type's primary file so they are emitted
    /// exactly once rather than dropped or duplicated.
    /// </para>
    /// <para>
    /// The "primary file" is chosen by <see cref="GetPrimaryDeclaringFile"/>, which picks the
    /// lowest declaring path case-insensitively so the choice is stable regardless of the order
    /// the metadata provider reports declarations in.
    /// </para>
    /// <para>
    /// Incremental builds depend on partial-sibling invalidation in
    /// <c>ProjectMetadataIndex</c>: editing any declaring file must mark every other declaring file
    /// of that type as affected, otherwise the file that owns the output is never re-rendered.
    /// </para>
    /// </remarks>
    private static SourceFileMetadata ApplyPartialRenderingMode(
        SourceFileMetadata sourceFile,
        PartialRenderingMode partialRenderingMode)
    {
        if (partialRenderingMode == PartialRenderingMode.Combined)
        {
            // Combined renders a partial type exactly once, with all of its members merged. The
            // metadata already carries the merged member list on every declaring file, so the only
            // thing needed here is to stop the other declaring files from emitting a duplicate:
            // the type is kept solely on its primary declaring file. Without this the type is
            // emitted once per declaring file, producing N identical copies rather than one
            // combined output.
            var combinedTypes = sourceFile.Types
                .Where(predicate: type => type.FileLocations.Count <= 1
                    || string.Equals(
                        a: GetPrimaryDeclaringFile(type: type),
                        b: sourceFile.Path,
                        comparisonType: StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return combinedTypes.Length == sourceFile.Types.Count
                ? sourceFile
                : sourceFile with { Types = combinedTypes };
        }

        // Only partial types (declared in more than one file) need filtering; everything else
        // already belongs wholly to this file.
        if (!sourceFile.Types.Any(predicate: type => type.FileLocations.Count > 1))
        {
            return sourceFile;
        }

        var types = sourceFile.Types
            .Select(selector: type => FilterTypeToSourceFile(type: type, path: sourceFile.Path))
            .ToArray();

        return sourceFile with { Types = types };
    }

    /// <summary>
    /// Selects the single declaring file that owns a partial type's combined output and its
    /// members that carry no source location.
    /// </summary>
    /// <param name="type">The type whose declaring files are being considered.</param>
    /// <returns>The primary declaring file, or <see langword="null"/> if the type has none.</returns>
    /// <remarks>
    /// <para>
    /// The choice must be deterministic: it decides which file emits a Combined-mode type and
    /// which file absorbs unattributed members, so an unstable choice would silently move
    /// generated content between output files from one build to the next.
    /// </para>
    /// <para>
    /// The minimum path under <see cref="StringComparer.OrdinalIgnoreCase"/> is used rather than
    /// simply taking the first entry, because <see cref="TypeMetadata.FileLocations"/> ordering
    /// originates in the metadata provider. Roslyn reports declaration locations in syntax-tree
    /// order, which varies with compilation input order. The provider does sort, but relying on
    /// that here would make correctness depend on a contract owned by another layer; computing the
    /// minimum makes this method correct for any ordering. This matches v3.0.1, which ordered with
    /// <c>StringComparer.OrdinalIgnoreCase</c> before taking the first location.
    /// </para>
    /// </remarks>
    private static string? GetPrimaryDeclaringFile(TypeMetadata type)
    {
        if (type.FileLocations.Count == 0)
        {
            return null;
        }

        var primary = type.FileLocations[0];
        for (var index = 1; index < type.FileLocations.Count; index++)
        {
            if (StringComparer.OrdinalIgnoreCase.Compare(x: type.FileLocations[index], y: primary) < 0)
            {
                primary = type.FileLocations[index];
            }
        }

        return primary;
    }

    private static TypeMetadata FilterTypeToSourceFile(TypeMetadata type, string path)
    {
        if (type.FileLocations.Count <= 1)
        {
            return type;
        }

        // The primary file owns any member that cannot be attributed to a specific file, so such
        // members are emitted exactly once rather than repeated on every declaring file.
        var isPrimaryFile = string.Equals(
            a: GetPrimaryDeclaringFile(type: type),
            b: path,
            comparisonType: StringComparison.OrdinalIgnoreCase);

        bool DeclaredHere(SourceLocation? location) =>
            location is null
                ? isPrimaryFile
                : string.Equals(a: location.Path, b: path, comparisonType: StringComparison.OrdinalIgnoreCase);

        return type with
        {
            Properties = type.Properties.Where(predicate: member => DeclaredHere(location: member.Location)).ToArray(),
            Methods = type.Methods.Where(predicate: member => DeclaredHere(location: member.Location)).ToArray(),
            Constants = type.Constants.Where(predicate: member => DeclaredHere(location: member.Location)).ToArray(),
            Fields = type.Fields.Where(predicate: member => DeclaredHere(location: member.Location)).ToArray(),
            StaticReadOnlyFields = type.StaticReadOnlyFields.Where(predicate: member => DeclaredHere(location: member.Location)).ToArray(),
            Events = type.Events.Where(predicate: member => DeclaredHere(location: member.Location)).ToArray(),
        };
    }

    private static TemplateDocument ParseTemplate(
        TemplateFile templateFile,
        ICollection<GenerationDiagnostic> diagnostics)
    {
        if (!TryCreateTemplateDocumentCacheKey(templateFile: templateFile, cacheKey: out var cacheKey))
        {
            return TemplateDocument.Parse(template: templateFile, diagnostics: diagnostics);
        }

        if (!TemplateDocumentCache.TryGetValue(key: cacheKey, value: out var entry)
            || !entry.SourceContent.Equals(value: templateFile.Content, comparisonType: StringComparison.Ordinal))
        {
            var parseDiagnostics = new List<GenerationDiagnostic>();
            entry = new TemplateDocumentCacheEntry(
                Document: TemplateDocument.Parse(template: templateFile, diagnostics: parseDiagnostics),
                Diagnostics: parseDiagnostics.ToArray(),
                SourceContent: templateFile.Content);
            if (TemplateDocumentCache.TryAdd(key: cacheKey, value: entry))
            {
                TemplateDocumentCacheOrder.Enqueue(item: cacheKey);
                TrimTemplateDocumentCache();
            }
            else
            {
                TemplateDocumentCache[key: cacheKey] = entry;
            }
        }

        foreach (var diagnostic in entry.Diagnostics)
        {
            diagnostics.Add(item: diagnostic);
        }

        return entry.Document;
    }

    private static void TrimTemplateDocumentCache()
    {
        while (TemplateDocumentCache.Count > MaxTemplateDocumentCacheEntries)
        {
            if (!TemplateDocumentCacheOrder.TryDequeue(result: out var cacheKey))
            {
                return;
            }

            _ = TemplateDocumentCache.TryRemove(key: cacheKey, value: out _);
        }
    }

    private static bool TryCreateTemplateDocumentCacheKey(
        TemplateFile templateFile,
        out TemplateDocumentCacheKey cacheKey)
    {
        try
        {
            var fullPath = Path.GetFullPath(path: templateFile.Path);
            if (!File.Exists(path: fullPath))
            {
                cacheKey = default!;
                return false;
            }

#pragma warning disable SCS0018
            var file = new FileInfo(fileName: fullPath);
#pragma warning restore SCS0018
            cacheKey = new TemplateDocumentCacheKey(
                Path: fullPath,
                Length: file.Length,
                LastWriteTicks: file.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (ArgumentException ex)
        {
            _ = ex;
        }
        catch (IOException ex)
        {
            _ = ex;
        }
        catch (NotSupportedException ex)
        {
            _ = ex;
        }
        catch (UnauthorizedAccessException ex)
        {
            _ = ex;
        }

        cacheKey = default!;
        return false;
    }

    private static string? ResolveSolutionFullName(string workspaceRootPath)
    {
        var fullPath = Path.GetFullPath(path: workspaceRootPath);
        var isSolutionFile = fullPath.EndsWith(value: ".sln", comparisonType: StringComparison.OrdinalIgnoreCase)
            || fullPath.EndsWith(value: ".slnx", comparisonType: StringComparison.OrdinalIgnoreCase);
        return isSolutionFile && File.Exists(path: fullPath)
            ? fullPath
            : null;
    }

    private static WorkspaceContext CreateProjectWorkspace(
        GenerationRequest request,
        WorkspaceContext workspace,
        string projectPath,
        int projectCount)
    {
        if (!string.IsNullOrWhiteSpace(value: request.TemplateSearchPath))
        {
            var workspaceBasePath = File.Exists(path: workspace.RootPath)
                ? Path.GetDirectoryName(path: workspace.RootPath) ?? Environment.CurrentDirectory
                : workspace.RootPath;
            var templateSearchPath = Path.IsPathRooted(path: request.TemplateSearchPath)
                ? request.TemplateSearchPath
                : Path.Combine(path1: workspaceBasePath, path2: request.TemplateSearchPath);
            return new WorkspaceContext(RootPath: Path.GetFullPath(path: templateSearchPath));
        }

        if (projectCount <= 1 || !string.IsNullOrWhiteSpace(value: request.TemplatePath))
        {
            return workspace;
        }

        return new WorkspaceContext(
            RootPath: Path.GetDirectoryName(path: Path.GetFullPath(path: projectPath))
                      ?? workspace.RootPath);
    }

    private static GenerationResult CreateResult(
        Stopwatch stopwatch,
        IReadOnlyList<GeneratedFile> generatedFiles,
        ICollection<GenerationDiagnostic> diagnostics,
        GenerationRequest request,
        GenerationPerformanceTrace performanceTrace)
    {
        stopwatch.Stop();
        performanceTrace.Add(stage: "Total generation", elapsed: stopwatch.Elapsed);
        performanceTrace.AddDiagnostics(diagnostics: diagnostics, file: request.WorkspacePath ?? request.ProjectPath ?? request.TemplatePath ?? Environment.CurrentDirectory);
        var hasErrors = diagnostics.Any(predicate: diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var hasFailedWarnings = request.Configuration.Diagnostics.FailOnWarning
            && diagnostics.Any(predicate: diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);

        return new GenerationResult(
            Success: !hasErrors && !hasFailedWarnings,
            GeneratedFiles: generatedFiles,
            Diagnostics: diagnostics.ToArray(),
            Duration: stopwatch.Elapsed);
    }

    private static CompiledTemplateFactory? CreateCompiledTemplateFactory(
        TemplateDocument template,
        ICollection<GenerationDiagnostic> diagnostics) =>
        template.CodeBlocks.Count == 0
            ? null
            : TemplateRuntimeCompiler.CompileFactory(template: template, diagnostics: diagnostics);

#pragma warning disable MA0051,S107,S3776
    private async Task GenerateProjectAsync(
        GenerationRequest request,
        WorkspaceContext workspace,
        WorkspaceContext templateWorkspace,
        string projectPath,
        TemplateRenderDefaults renderDefaults,
        IReadOnlyCollection<string>? incrementalChangedSourcePaths,
        ICollection<GenerationDiagnostic> diagnostics,
        ICollection<GeneratedFile> generatedFiles,
        IDictionary<string, string> plannedOutputPaths,
        GenerationPerformanceTrace performanceTrace,
        CancellationToken cancellationToken)
#pragma warning restore MA0051,S107,S3776
    {
        var templateDiscoveryStopwatch = Stopwatch.StartNew();
        var templates = await _templateDiscovery.FindTemplatesAsync(
            workspace: templateWorkspace,
            request: request,
            cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        performanceTrace.Add(stage: $"Template discovery ({Path.GetFileName(path: projectPath)})", elapsed: templateDiscoveryStopwatch.Elapsed);

        if (templates.Count == 0)
        {
            diagnostics.Add(
                item: new GenerationDiagnostic(
                    File: workspace.RootPath,
                    Line: null,
                    Column: null,
                    Severity: DiagnosticSeverity.Warning,
                    Message: $"No .tst templates were found for project: {projectPath}.",
                    Code: null));
            return;
        }

        var metadataStopwatch = Stopwatch.StartNew();
        var project = await _metadataProvider.GetMetadataAsync(
            project: new ProjectContext(
                ProjectPath: projectPath,
                WorkspacePath: workspace.RootPath,
                TargetFramework: request.Configuration.DefaultTargetFramework,
                RunFullDiagnostics: request.Mode == GenerationMode.Validate),
            cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        performanceTrace.Add(stage: $"Metadata load ({Path.GetFileName(path: projectPath)})", elapsed: metadataStopwatch.Elapsed);
        foreach (var diagnostic in project.Diagnostics)
        {
            diagnostics.Add(item: diagnostic);
        }

        if (project.Diagnostics.Any(predicate: diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return;
        }

        var projectIndex = ProjectMetadataIndex.Create(metadata: project);
        var includedProjectCache = new Dictionary<string, TemplateProjectContext>(comparer: StringComparer.OrdinalIgnoreCase);

        // Render contexts are reused across templates that share the project, but the partial
        // splitting baked into them depends on the template's PartialRenderingMode, so the cache
        // is keyed by that mode.
        var sourceFileRenderContextsByMode = new Dictionary<PartialRenderingMode, IReadOnlyList<SourceFileRenderContext>>();
        foreach (var templateFile in templates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var templateDiagnostics = new List<GenerationDiagnostic>();
            var templateParseStopwatch = Stopwatch.StartNew();
            var template = ParseTemplate(templateFile: templateFile, diagnostics: templateDiagnostics);
#pragma warning disable CA2000
            var compiledTemplateFactory = CreateCompiledTemplateFactory(template: template, diagnostics: templateDiagnostics);
#pragma warning restore CA2000
            performanceTrace.Add(stage: $"Template parse/compile ({Path.GetFileName(path: templateFile.Path)})", elapsed: templateParseStopwatch.Elapsed);
            try
            {
                if (templateDiagnostics.Any(predicate: diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    foreach (var templateDiagnostic in templateDiagnostics)
                    {
                        diagnostics.Add(item: templateDiagnostic);
                    }

                    continue;
                }

                TemplateRenderInspection? renderInspection = null;
                if (RequiresSingleFileModeInspection(template: template)
                    || RequiresIncludedProjectsInspection(template: template)
                    || RequiresPartialRenderingModeInspection(template: template))
                {
                    var inspectionDiagnostics = new List<GenerationDiagnostic>();
                    renderInspection = TemplateRenderer.InspectTemplate(
                        template: template,
                        metadata: project,
                        diagnostics: inspectionDiagnostics,
                        defaults: renderDefaults,
                        compiledTemplateFactory: compiledTemplateFactory,
                        metadataIndex: projectIndex);
                    if (inspectionDiagnostics.Any(predicate: diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    {
                        foreach (var inspectionDiagnostic in inspectionDiagnostics)
                        {
                            diagnostics.Add(item: inspectionDiagnostic);
                        }

                        continue;
                    }
                }

                var templateProjectContext = new TemplateProjectContext(Metadata: project, Index: projectIndex);
                if (renderInspection is not null && renderInspection.IncludedProjects.Count > 0)
                {
                    templateProjectContext = await ResolveIncludedProjectsAsync(
                        request: request,
                        workspace: workspace,
                        template: template,
                        project: project,
                        projectIndex: projectIndex,
                        includedProjectNames: renderInspection.IncludedProjects,
                        includedProjectCache: includedProjectCache,
                        diagnostics: diagnostics,
                        cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }

                var templateProject = templateProjectContext.Metadata;
                var templateIndex = templateProjectContext.Index;
                if (ShouldRenderPerSourceFile(template: template, project: templateProject, inspection: renderInspection))
                {
                    foreach (var templateDiagnostic in templateDiagnostics)
                    {
                        diagnostics.Add(item: templateDiagnostic);
                    }

                    IReadOnlyList<SourceFileRenderContext> renderContexts;
                    var partialRenderingMode = renderInspection?.PartialRenderingMode ?? PartialRenderingMode.Partial;
                    if (ReferenceEquals(objA: templateProject, objB: project))
                    {
                        if (!sourceFileRenderContextsByMode.TryGetValue(key: partialRenderingMode, value: out var cachedContexts))
                        {
                            cachedContexts = CreateSourceFileRenderContexts(
                                project: project,
                                metadataIndex: projectIndex,
                                partialRenderingMode: partialRenderingMode);
                            sourceFileRenderContextsByMode[partialRenderingMode] = cachedContexts;
                        }

                        renderContexts = cachedContexts;
                    }
                    else
                    {
                        renderContexts = CreateSourceFileRenderContexts(
                            project: templateProject,
                            metadataIndex: templateIndex,
                            partialRenderingMode: partialRenderingMode);
                    }

                    renderContexts = FilterSourceFileRenderContexts(
                        renderContexts: renderContexts,
                        metadataIndex: templateIndex,
                        incrementalChangedSourcePaths: incrementalChangedSourcePaths);
                    var matchedAnySourceFile = false;
                    foreach (var sourceContext in renderContexts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sourceDiagnostics = new List<GenerationDiagnostic>();
                        var sourceRenderStopwatch = Stopwatch.StartNew();
                        var sourceRenderResult = _templateRenderer.RenderTemplate(
                            template: template,
                            metadata: sourceContext.Project,
                            diagnostics: sourceDiagnostics,
                            defaults: renderDefaults,
                            compiledTemplateFactory: compiledTemplateFactory,
                            metadataIndex: sourceContext.MetadataIndex);
                        performanceTrace.Add(stage: $"Render source file ({Path.GetFileName(path: sourceContext.SourceFile.Path)})", elapsed: sourceRenderStopwatch.Elapsed);
                        foreach (var sourceDiagnostic in sourceDiagnostics)
                        {
                            diagnostics.Add(item: sourceDiagnostic);
                        }

                        if (sourceDiagnostics.Any(predicate: diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                        {
                            continue;
                        }

                        if (sourceRenderResult.RootItemCount == 0)
                        {
                            continue;
                        }

                        matchedAnySourceFile = true;

                        sourceRenderResult = ApplyLegacySourceOutputPath(
                            sourceFile: sourceContext.SourceFile,
                            renderResult: sourceRenderResult,
                            fileNameConvention: request.Configuration.Output.FileNameConvention);
                        await PlanAndWriteAsync(
                            request: request,
                            templateWorkspace: templateWorkspace,
                            template: template,
                            renderResult: sourceRenderResult,
                            diagnostics: diagnostics,
                            generatedFiles: generatedFiles,
                            plannedOutputPaths: plannedOutputPaths,
                            cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                    }

                    if (!matchedAnySourceFile && renderContexts.Count > 0)
                    {
                        diagnostics.Add(
                            item: new GenerationDiagnostic(
                                File: template.Path,
                                Line: null,
                                Column: null,
                                Severity: DiagnosticSeverity.Info,
                                Message: $"Template '{Path.GetFileName(path: template.Path)}' produced no output because no root collection items matched its filters in any of the {renderContexts.Count} source file(s) examined.",
                                Code: DiagnosticCodes.NoMatchingRootItems));
                    }

                    continue;
                }

                var renderStopwatch = Stopwatch.StartNew();
                var renderResult = _templateRenderer.RenderTemplate(
                    template: template,
                    metadata: templateProject,
                    diagnostics: templateDiagnostics,
                    defaults: renderDefaults,
                    compiledTemplateFactory: compiledTemplateFactory,
                    metadataIndex: templateIndex);
                performanceTrace.Add(stage: $"Render template ({Path.GetFileName(path: template.Path)})", elapsed: renderStopwatch.Elapsed);
                foreach (var templateDiagnostic in templateDiagnostics)
                {
                    diagnostics.Add(item: templateDiagnostic);
                }

                await PlanAndWriteAsync(
                    request: request,
                    templateWorkspace: templateWorkspace,
                    template: template,
                    renderResult: renderResult,
                    diagnostics: diagnostics,
                    generatedFiles: generatedFiles,
                    plannedOutputPaths: plannedOutputPaths,
                    cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            finally
            {
                compiledTemplateFactory?.Dispose();
            }
        }
    }

#pragma warning disable S107
    private async Task<TemplateProjectContext> ResolveIncludedProjectsAsync(
        GenerationRequest request,
        WorkspaceContext workspace,
        TemplateDocument template,
        ProjectMetadata project,
        ProjectMetadataIndex projectIndex,
        IReadOnlyList<string> includedProjectNames,
        IDictionary<string, TemplateProjectContext> includedProjectCache,
        ICollection<GenerationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        var unresolvedNames = new List<string>();
        var includedProjectPaths = WorkspaceProjectResolver.ResolveProjectPathsByName(
            workspacePath: workspace.RootPath,
            projectNames: includedProjectNames,
            unresolvedNames: unresolvedNames);
        ReportUnresolvedIncludedProjects(template: template, unresolvedNames: unresolvedNames, diagnostics: diagnostics);

        var currentProjectPath = Path.GetFullPath(path: project.ProjectPath);
        var additionalProjectPaths = includedProjectPaths
            .Where(predicate: includedPath => !includedPath.Equals(value: currentProjectPath, comparisonType: StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (additionalProjectPaths.Length == 0)
        {
            return new TemplateProjectContext(Metadata: project, Index: projectIndex);
        }

        var cacheKey = string.Join(
            separator: IncludedProjectCacheKeySeparator,
            values: additionalProjectPaths.Order(comparer: StringComparer.OrdinalIgnoreCase));
        if (includedProjectCache.TryGetValue(key: cacheKey, value: out var cachedContext))
        {
            return cachedContext;
        }

        var includedMetadata = await LoadIncludedProjectMetadataAsync(
            request: request,
            workspace: workspace,
            includedProjectPaths: additionalProjectPaths,
            diagnostics: diagnostics,
            cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var mergedProject = ProjectMetadataMerger.Merge(project: project, includedProjects: includedMetadata);
        var templateProjectContext = new TemplateProjectContext(
            Metadata: mergedProject,
            Index: ProjectMetadataIndex.Create(metadata: mergedProject));
        includedProjectCache[key: cacheKey] = templateProjectContext;
        return templateProjectContext;
    }

    private async Task<IReadOnlyList<ProjectMetadata>> LoadIncludedProjectMetadataAsync(
        GenerationRequest request,
        WorkspaceContext workspace,
        IReadOnlyList<string> includedProjectPaths,
        ICollection<GenerationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var includedMetadata = new List<ProjectMetadata>();
        foreach (var includedProjectPath in includedProjectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var included = await _metadataProvider.GetMetadataAsync(
                project: new ProjectContext(
                    ProjectPath: includedProjectPath,
                    WorkspacePath: workspace.RootPath,
                    TargetFramework: request.Configuration.DefaultTargetFramework,
                    RunFullDiagnostics: request.Mode == GenerationMode.Validate),
                cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            foreach (var diagnostic in included.Diagnostics)
            {
                diagnostics.Add(item: diagnostic);
            }

            if (included.Diagnostics.Any(predicate: diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                continue;
            }

            includedMetadata.Add(item: included);
        }

        return includedMetadata;
    }

#pragma warning disable S107
    private async Task PlanAndWriteAsync(
        GenerationRequest request,
        WorkspaceContext templateWorkspace,
        TemplateDocument template,
        TemplateRenderResult renderResult,
        ICollection<GenerationDiagnostic> diagnostics,
        ICollection<GeneratedFile> generatedFiles,
        IDictionary<string, string> plannedOutputPaths,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        if (!_planner.TryPlan(
                workspace: templateWorkspace,
                template: template,
                outputPath: renderResult.OutputPath,
                content: renderResult.Content,
                fileNameConvention: request.Configuration.Output.FileNameConvention,
                utf8Bom: renderResult.Utf8Bom,
                generatedFile: out var plannedFile,
                diagnostic: out var diagnostic,
                emitHeader: renderResult.GenerateFileHeader ?? request.Configuration.Output.GenerateFileHeader,
                insertFinalNewline: renderResult.InsertFinalNewline,
                finalNewlineCount: renderResult.FinalNewlineCount))
        {
            if (diagnostic is not null)
            {
                diagnostics.Add(item: diagnostic);
            }

            return;
        }

        if (plannedFile is not null)
        {
            ReportDuplicateOutputPath(template: template, plannedFile: plannedFile, diagnostics: diagnostics, plannedOutputPaths: plannedOutputPaths);
            var generatedFile = await _fileWriter.WriteAsync(
                file: plannedFile,
                request: request,
                cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            generatedFiles.Add(item: generatedFile);
        }
    }

    private sealed class GenerationPerformanceTrace
    {
        private const string EnvironmentVariableName = "TYPEWRITER_TRACE_PERF";
        private readonly List<(string Stage, TimeSpan Elapsed)> _entries = [];

        private GenerationPerformanceTrace(bool enabled)
        {
            Enabled = enabled;
        }

        private bool Enabled { get; }

        public static GenerationPerformanceTrace Create()
        {
            var value = Environment.GetEnvironmentVariable(variable: EnvironmentVariableName);
            var enabled = value is not null
                && (value.Equals(value: "1", comparisonType: StringComparison.OrdinalIgnoreCase)
                    || value.Equals(value: "true", comparisonType: StringComparison.OrdinalIgnoreCase)
                    || value.Equals(value: "yes", comparisonType: StringComparison.OrdinalIgnoreCase));
            return new GenerationPerformanceTrace(enabled: enabled);
        }

        public void Add(
            string stage,
            TimeSpan elapsed)
        {
            if (Enabled)
            {
                _entries.Add(item: (stage, elapsed));
            }
        }

        public void AddDiagnostics(
            ICollection<GenerationDiagnostic> diagnostics,
            string file)
        {
            if (!Enabled)
            {
                return;
            }

            foreach (var entry in _entries)
            {
                diagnostics.Add(
                    item: new GenerationDiagnostic(
                        File: file,
                        Line: null,
                        Column: null,
                        Severity: DiagnosticSeverity.Info,
                        Message: string.Create(
                            provider: CultureInfo.InvariantCulture,
                            handler: $"{entry.Stage}: {entry.Elapsed.TotalMilliseconds:F1} ms"),
                        Code: "TWPERF"));
            }

            diagnostics.Add(
                item: new GenerationDiagnostic(
                    File: file,
                    Line: null,
                    Column: null,
                    Severity: DiagnosticSeverity.Info,
                    Message: "Metadata cache: " + MetadataCacheMetrics.CreateSummary(),
                    Code: "TWPERF"));
        }
    }

    private sealed record SourceFileRenderContext(
        SourceFileMetadata SourceFile,
        ProjectMetadata Project,
        ProjectMetadataIndex MetadataIndex);

    private sealed record TemplateProjectContext(
        ProjectMetadata Metadata,
        ProjectMetadataIndex Index);

    private sealed record TemplateDocumentCacheKey(
        string Path,
        long Length,
        long LastWriteTicks);

    private sealed record TemplateDocumentCacheEntry(
        TemplateDocument Document,
        IReadOnlyList<GenerationDiagnostic> Diagnostics,
        string SourceContent);
}
