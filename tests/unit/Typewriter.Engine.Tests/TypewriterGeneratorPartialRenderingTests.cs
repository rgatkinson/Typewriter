using Typewriter.Abstractions;
using Xunit;

namespace Typewriter.Engine.Tests;

/// <summary>
/// Covers <c>PartialRenderingMode</c> handling for types declared across several files.
/// </summary>
/// <remarks>
/// <para>
/// A partial type appears in the metadata of every file that declares it, and each of those
/// copies carries the complete, merged member list. The two rendering modes disagree about what
/// to do with that:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Combined</c> emits the whole type once, on its primary declaring file.
/// </description></item>
/// <item><description>
/// <c>Partial</c> (the default) emits one output per declaring file, each containing only the
/// members that file declares.
/// </description></item>
/// </list>
/// <para>
/// Both modes previously regressed into emitting the full type once per declaring file, producing
/// N identical copies, so these tests pin the member-level behaviour rather than just file counts.
/// </para>
/// </remarks>
public sealed class TypewriterGeneratorPartialRenderingTests
{
    private const string PartialTypeFullName = "Sample.Orders";

    [Fact]
    public async Task CombinedModeRendersPartialTypeOnceOnPrimaryFile()
    {
        var directory = CreateProjectDirectory();
        try
        {
            var harness = PartialTypeHarness.Create(directory: directory);

            var result = await harness.GenerateAsync(
                templateBody: """
                              ${
                                  Template(Settings settings)
                                  {
                                      settings.PartialRenderingMode = PartialRenderingMode.Combined;
                                  }
                              }
                              $Classes[export class $Name {$Methods[
                                  $Name();]
                              }
                              ]
                              """);

            result.Success.Should().BeTrue(because: PartialTypeHarness.DescribeDiagnostics(result: result));

            // Exactly one output: the type is emitted only on its primary (alphabetically first)
            // declaring file. Regression guard: previously every declaring file emitted a full copy.
            result.GeneratedFiles.Should().ContainSingle();
            result.GeneratedFiles[0].Path.Should().Be(Path.Combine(path1: directory, path2: "Orders.ts"));

            // Combined keeps the merged member list, so every member appears exactly once.
            var content = result.GeneratedFiles[0].Content;
            content.Should().Contain("Place()");
            content.Should().Contain("Cancel()");
            content.Should().Contain("Refund()");
            CountOccurrences(haystack: content, needle: "export class Orders").Should().Be(1);
            CountOccurrences(haystack: content, needle: "Place()").Should().Be(1);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(directory: directory);
        }
    }

    [Fact]
    public async Task PartialModeRendersEachDeclaringFileWithOnlyItsOwnMembers()
    {
        var directory = CreateProjectDirectory();
        try
        {
            var harness = PartialTypeHarness.Create(directory: directory);

            // No PartialRenderingMode set, so the Partial default applies.
            var result = await harness.GenerateAsync(
                templateBody: """
                              $Classes[export class $Name {$Methods[
                                  $Name();]
                              }
                              ]
                              """);

            result.Success.Should().BeTrue(because: PartialTypeHarness.DescribeDiagnostics(result: result));

            // One output per declaring file.
            result.GeneratedFiles.Select(selector: file => Path.GetFileName(path: file.Path))
                .Order(comparer: StringComparer.OrdinalIgnoreCase)
                .Should().Equal("Orders.ts", "OrdersCancel.ts", "OrdersRefund.ts");

            // Each output carries only the members declared in its own file: together they cover
            // the type exactly once, with no member repeated across outputs.
            GetContent(result: result, fileName: "Orders.ts").Should().Contain("Place()");
            GetContent(result: result, fileName: "Orders.ts").Should().NotContain("Cancel()");
            GetContent(result: result, fileName: "Orders.ts").Should().NotContain("Refund()");

            GetContent(result: result, fileName: "OrdersCancel.ts").Should().Contain("Cancel()");
            GetContent(result: result, fileName: "OrdersCancel.ts").Should().NotContain("Place()");

            GetContent(result: result, fileName: "OrdersRefund.ts").Should().Contain("Refund()");
            GetContent(result: result, fileName: "OrdersRefund.ts").Should().NotContain("Place()");
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(directory: directory);
        }
    }

    [Fact]
    public async Task PartialModeAssignsMembersWithoutSourceLocationToPrimaryFileOnly()
    {
        var directory = CreateProjectDirectory();
        try
        {
            // A member with no recorded location (for example compiler-synthesized) must still be
            // emitted, exactly once, rather than dropped or repeated on every declaring file.
            var harness = PartialTypeHarness.Create(
                directory: directory,
                extraMethodWithoutLocation: "Synthesized");

            var result = await harness.GenerateAsync(
                templateBody: """
                              $Classes[export class $Name {$Methods[
                                  $Name();]
                              }
                              ]
                              """);

            result.Success.Should().BeTrue(because: PartialTypeHarness.DescribeDiagnostics(result: result));

            var totalOccurrences = result.GeneratedFiles
                .Sum(selector: file => CountOccurrences(haystack: file.Content, needle: "Synthesized()"));
            totalOccurrences.Should().Be(1);

            // It lands on the primary file specifically.
            GetContent(result: result, fileName: "Orders.ts").Should().Contain("Synthesized()");
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(directory: directory);
        }
    }

    [Fact]
    public async Task CombinedModePrimaryFileIsIndependentOfDeclarationOrder()
    {
        // The primary file is FileLocations[0], so the choice must not depend on the order the
        // metadata provider happens to report declarations in. Roslyn returns syntax-tree order,
        // which varies with compilation input order; without a stable sort the output would move
        // between builds. Here the same type is described with its locations reversed, and both
        // arrangements must select the same primary file.
        foreach (var reversed in new[] { false, true })
        {
            var directory = CreateProjectDirectory();
            try
            {
                var harness = PartialTypeHarness.Create(directory: directory, reverseFileLocations: reversed);

                var result = await harness.GenerateAsync(
                    templateBody: """
                                  ${
                                      Template(Settings settings)
                                      {
                                          settings.PartialRenderingMode = PartialRenderingMode.Combined;
                                      }
                                  }
                                  $Classes[export class $Name {
                                  }
                                  ]
                                  """);

                result.Success.Should().BeTrue(because: PartialTypeHarness.DescribeDiagnostics(result: result));
                result.GeneratedFiles.Should().ContainSingle(because: $"reverseFileLocations={reversed}");
                result.GeneratedFiles[0].Path.Should().Be(
                    Path.Combine(path1: directory, path2: "Orders.ts"),
                    because: $"the primary file must be stable; reverseFileLocations={reversed}");
            }
            finally
            {
                await DeleteDirectoryWithRetryAsync(directory: directory);
            }
        }
    }

    [Fact]
    public void EditingNonPrimaryPartialFileMarksAllDeclaringFilesAffected()
    {
        // This is the v3.0.1 _requestRender behaviour. Partial siblings do not reference one
        // another, so the type-reference dependency graph cannot connect them. Under Combined the
        // type is emitted only on its primary file, so editing a non-primary declaration would
        // otherwise produce no render context at all and leave the output stale.
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: "Typewriter.PartialIndex");
        var primaryPath = Path.Combine(path1: directory, path2: "Orders.cs");
        var cancelPath = Path.Combine(path1: directory, path2: "OrdersCancel.cs");
        var refundPath = Path.Combine(path1: directory, path2: "OrdersRefund.cs");

        var metadata = PartialTypeHarness.CreateProjectMetadata(
            directory: directory,
            projectPath: Path.Combine(path1: directory, path2: "App.csproj"));
        var index = ProjectMetadataIndex.Create(metadata: metadata);

        // Edit the last declaring file, which is neither primary nor referenced by the others.
        var affected = index.GetAffectedSourceFiles(changedSourcePaths: [refundPath]);

        affected.Should().BeEquivalentTo(primaryPath, cancelPath, refundPath);
    }

    [Fact]
    public void NonPartialTypesAreNotDraggedInByPartialSiblingExpansion()
    {
        // Sibling expansion must not over-invalidate: an unrelated single-file type stays out of
        // the affected set unless the reference graph genuinely pulls it in.
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: "Typewriter.PartialIndex");
        var unrelatedPath = Path.Combine(path1: directory, path2: "Unrelated.cs");

        var metadata = PartialTypeHarness.CreateProjectMetadata(
            directory: directory,
            projectPath: Path.Combine(path1: directory, path2: "App.csproj"),
            includeUnrelatedType: true);
        var index = ProjectMetadataIndex.Create(metadata: metadata);

        var affected = index.GetAffectedSourceFiles(
            changedSourcePaths: [Path.Combine(path1: directory, path2: "OrdersRefund.cs")]);

        affected.Should().NotContain(unrelatedPath);
    }

    private static string GetContent(GenerationResult result, string fileName) =>
        result.GeneratedFiles
            .Single(predicate: file => string.Equals(
                a: Path.GetFileName(path: file.Path),
                b: fileName,
                comparisonType: StringComparison.OrdinalIgnoreCase))
            .Content;

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var found = haystack.IndexOf(value: needle, startIndex: offset, comparisonType: StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            offset = found + needle.Length;
        }
    }

    private static string CreateProjectDirectory()
    {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "Typewriter.Engine.Tests",
            path3: Guid.NewGuid().ToString(format: "N"));
        Directory.CreateDirectory(path: directory);
        return directory;
    }

    private static async Task DeleteDirectoryWithRetryAsync(string directory)
    {
        const int MaxAttempts = 10;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path: directory))
                {
                    Directory.Delete(path: directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                await Task.Delay(millisecondsDelay: 100).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
            {
                await Task.Delay(millisecondsDelay: 100).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
    }

    /// <summary>
    /// Builds metadata for a single partial class <c>Sample.Orders</c> declared across three
    /// files, mirroring the real-world shape that regressed.
    /// </summary>
    private sealed class PartialTypeHarness
    {
        private readonly string _directory;
        private readonly string _projectPath;
        private readonly ProjectMetadata _metadata;

        private PartialTypeHarness(string directory, string projectPath, ProjectMetadata metadata)
        {
            _directory = directory;
            _projectPath = projectPath;
            _metadata = metadata;
        }

        public static PartialTypeHarness Create(
            string directory,
            bool reverseFileLocations = false,
            string? extraMethodWithoutLocation = null)
        {
            var projectPath = Path.Combine(path1: directory, path2: "App.csproj");
            Directory.CreateDirectory(path: directory);
#pragma warning disable SEC0116 // Test-controlled temp path, not user input.
            File.WriteAllText(path: projectPath, contents: "<Project />");
#pragma warning restore SEC0116

            var metadata = CreateProjectMetadata(
                directory: directory,
                projectPath: projectPath,
                reverseFileLocations: reverseFileLocations,
                extraMethodWithoutLocation: extraMethodWithoutLocation);

            return new PartialTypeHarness(directory: directory, projectPath: projectPath, metadata: metadata);
        }

        public static ProjectMetadata CreateProjectMetadata(
            string directory,
            string projectPath,
            bool reverseFileLocations = false,
            string? extraMethodWithoutLocation = null,
            bool includeUnrelatedType = false)
        {
            var primaryPath = Path.Combine(path1: directory, path2: "Orders.cs");
            var cancelPath = Path.Combine(path1: directory, path2: "OrdersCancel.cs");
            var refundPath = Path.Combine(path1: directory, path2: "OrdersRefund.cs");

            // Sorted case-insensitively, as the metadata provider guarantees. The reversed variant
            // simulates an unsorted provider to prove the consumer does not depend on input order.
            var fileLocations = new[] { primaryPath, cancelPath, refundPath };
            if (reverseFileLocations)
            {
                Array.Reverse(array: fileLocations);
            }

            var methods = new List<MethodMetadata>
            {
                CreateMethod(name: "Place", path: primaryPath),
                CreateMethod(name: "Cancel", path: cancelPath),
                CreateMethod(name: "Refund", path: refundPath),
            };

            if (extraMethodWithoutLocation is not null)
            {
                methods.Add(item: CreateMethod(name: extraMethodWithoutLocation, path: null));
            }

            // The same merged type instance is reported for every declaring file, which is exactly
            // what the real metadata provider does for partial types.
            var partialType = CreateClassMetadata(
                name: "Orders",
                fileLocations: fileLocations,
                methods: methods);

            var sourceFiles = new List<SourceFileMetadata>
            {
                new(Path: primaryPath, Types: [partialType]),
                new(Path: cancelPath, Types: [partialType]),
                new(Path: refundPath, Types: [partialType]),
            };

            var types = new List<TypeMetadata> { partialType };

            if (includeUnrelatedType)
            {
                var unrelatedPath = Path.Combine(path1: directory, path2: "Unrelated.cs");
                var unrelatedType = CreateClassMetadata(
                    name: "Unrelated",
                    fileLocations: [unrelatedPath],
                    methods: []);
                sourceFiles.Add(item: new SourceFileMetadata(Path: unrelatedPath, Types: [unrelatedType]));
                types.Add(item: unrelatedType);
            }

            return new ProjectMetadata(
                ProjectPath: projectPath,
                SourceFiles: sourceFiles,
                Types: types,
                Diagnostics: []);
        }

        public static string DescribeDiagnostics(GenerationResult result) =>
            string.Join(separator: Environment.NewLine, values: result.Diagnostics.Select(selector: diagnostic => diagnostic.Message));

        public Task<GenerationResult> GenerateAsync(string templateBody)
        {
            var templatePath = Path.Combine(path1: _directory, path2: "Models.tst");
            var generator = new TypewriterGenerator(
                templateDiscovery: new StaticTemplateDiscovery(
                    templates: new TemplateFile(Path: templatePath, Content: templateBody)),
                metadataProvider: new StaticMetadataProvider(metadata: _metadata),
                fileWriter: new PassthroughFileWriter());

            return generator.GenerateAsync(
                request: new GenerationRequest(
                    WorkspacePath: _directory,
                    ProjectPath: _projectPath,
                    TemplatePath: templatePath,
                    Mode: GenerationMode.Generate,
                    Configuration: TypewriterConfiguration.Default),
                cancellationToken: CancellationToken.None);
        }

        private static MethodMetadata CreateMethod(string name, string? path) =>
            new(
                Name: name,
                FullName: PartialTypeFullName + "." + name,
                ReturnType: new TypeMetadataReference(
                    Name: "Void",
                    FullName: "System.Void",
                    Namespace: "System",
                    IsNullable: false,
                    IsCollection: false,
                    IsDictionary: false,
                    IsEnum: false,
                    IsPrimitive: true,
                    IsDateLike: false,
                    ElementType: null,
                    TypeArguments: []),
                Accessibility: MetadataAccessibility.Public,
                IsStatic: false,
                IsAbstract: false,
                IsGeneric: false,
                Parameters: [],
                Attributes: [],
                ParentTypeFullName: PartialTypeFullName)
            {
                Location = path is null ? null : new SourceLocation(Path: path, Line: 1, Column: 1),
            };

        private static TypeMetadata CreateClassMetadata(
            string name,
            IReadOnlyList<string> fileLocations,
            IReadOnlyList<MethodMetadata> methods) =>
            new(
                Name: name,
                FullName: "Sample." + name,
                Namespace: "Sample",
                Kind: TypeMetadataKind.Class,
                Accessibility: MetadataAccessibility.Public,
                Properties: [],
                Attributes: [],
                BaseTypes: [],
                EnumValues: [],
                IsNullableAware: true)
            {
                FileLocations = fileLocations,
                Methods = methods,
            };
    }

    private sealed class StaticTemplateDiscovery : ITemplateDiscovery
    {
        private readonly IReadOnlyList<TemplateFile> _templates;

        public StaticTemplateDiscovery(params TemplateFile[] templates)
        {
            _templates = templates;
        }

        public Task<IReadOnlyList<TemplateFile>> FindTemplatesAsync(
            WorkspaceContext workspace,
            GenerationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result: _templates);
    }

    private sealed class StaticMetadataProvider : IProjectMetadataProvider
    {
        private readonly ProjectMetadata _metadata;

        public StaticMetadataProvider(ProjectMetadata metadata)
        {
            _metadata = metadata;
        }

        public Task<ProjectMetadata> GetMetadataAsync(
            ProjectContext project,
            CancellationToken cancellationToken) =>
            Task.FromResult(result: _metadata);
    }

    private sealed class PassthroughFileWriter : IGeneratedFileWriter
    {
        public Task<GeneratedFile> WriteAsync(
            GeneratedFile file,
            GenerationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result: file);
    }
}
