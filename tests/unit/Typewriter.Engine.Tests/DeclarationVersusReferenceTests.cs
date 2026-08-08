using Typewriter.Abstractions;
using Typewriter.Engine;
using Xunit;

namespace Typewriter.Engine.Tests;

/// <summary>
/// Regression coverage for the declaration-vs-reference split.
/// </summary>
/// <remarks>
/// <para>
/// v3 exposed a single <c>Type</c> abstraction carrying both reference-level facts and declared
/// members, so templates were written to reach through a type <em>reference</em> straight to
/// <em>declared</em> members -- <c>$Property.Type.Properties</c>, <c>$Parameter.Type.BaseClass</c>,
/// and so on. v4 split declarations from references and the reference side stopped answering.
/// </para>
/// <para>
/// The failure mode is what makes this worth pinning down thoroughly: nothing threw, nothing was
/// diagnosed, and the loops simply produced no output. Generated files were quietly incomplete.
/// The bug was also fixed and re-broken more than once, because the declaration path and the
/// reference path were parallel hand-maintained lists and members kept being added to only one.
/// </para>
/// <para>
/// These tests therefore assert on the whole member surface rather than the one or two members that
/// happened to be noticed in the field, so that a member restored on the declaration path but
/// forgotten on the reference path fails here rather than in someone's generated output.
/// </para>
/// </remarks>
public sealed class DeclarationVersusReferenceTests
{
    // Each declared member is exercised individually from a reference reached via
    // `$Properties[$Type[...]]`, which is the shape real templates use. Driving them as a theory
    // rather than one combined assertion means a regression names the member that broke instead of
    // failing on whichever happens to be checked first.
    [Theory]
    [InlineData("$BaseClass[$Name]", "EntityBase")]
    [InlineData("$DocComment[$Summary]", "A customer.")]
    [InlineData("$Interfaces[$Name]", "IAuditable")]
    [InlineData("$Properties[$Name]", "IdName")]
    [InlineData("$Methods[$Name]", "Deactivate")]
    [InlineData("$Constants[$Name]", "MaxNameLength")]
    [InlineData("$NestedClasses[$Name]", "Address")]
    [InlineData("$NestedEnums[$Name]", "Tier")]
    [InlineData("$TypeParameters[$Name]", "T")]
    public void ReferencedTypeExposesDeclaredMember(string memberExpression, string expected)
    {
        var metadata = DeclarationVersusReferenceFixtures.CreateProjectMetadata();

        var template = "$Classes(Order)[$Properties(Customer)[$Type[<" + memberExpression + ">]]]";

        var output = Render(metadata: metadata, template: template);

        // The angle brackets pin down that the member expression itself resolved: an unresolved
        // identifier renders as literal template text, which would show up inside them.
        output.Should().Contain("<" + expected + ">");
    }

    // The regression that started this: a template walking a referenced type's properties emitted
    // nothing at all rather than failing, so the omission survived into generated output unnoticed.
    [Fact]
    public void ReferencedTypePropertiesAreNotSilentlyEmpty()
    {
        var metadata = DeclarationVersusReferenceFixtures.CreateProjectMetadata();

        const string template = "$Classes(Order)[$Properties(Customer)[$Type[$Properties[<$Name>]]]]";

        var output = Render(metadata: metadata, template: template);

        output.Should().Contain("<Id>");
        output.Should().Contain("<Name>");
        output.Should().NotBeNullOrWhiteSpace();
    }

    // Types outside the compilation genuinely have no declaration to project, so producing no
    // declared members is correct rather than a degraded result. This guards against "fixing" the
    // gap by fabricating members for types we know nothing about.
    //
    // Note what the reference-side identifier does here: it stays unresolved and renders as literal
    // template text. That is the engine's existing behaviour for unresolved identifiers, and it is
    // pinned deliberately -- it is at least visible in the output, unlike the silent empty result
    // that made the original declaration-vs-reference bug so hard to notice.
    [Fact]
    public void ReferenceToTypeOutsideCompilationYieldsNoDeclaredMembers()
    {
        var metadata = DeclarationVersusReferenceFixtures.CreateProjectMetadata();

        const string template = "$Classes(Order)[$Properties(ExternalLink)[$Type[<$Properties[!$Name!]>]]]";

        var output = Render(metadata: metadata, template: template);

        // No members are invented for System.Uri...
        output.Should().NotContain("!Id!");
        output.Should().NotContain("!Name!");

        // ...and the unresolved member expression is left visible rather than silently swallowed.
        output.Should().Contain("$Properties");
    }

    // Laziness is what keeps construction terminating, but it means an uncached factory re-runs on
    // every access. This walks the same referenced type repeatedly, which is the access pattern
    // that made rendering quadratic before the results were memoised.
    [Fact]
    public void RepeatedAccessToReferencedMembersRendersConsistently()
    {
        var metadata = DeclarationVersusReferenceFixtures.CreateProjectMetadata();

        const string template = "$Classes(Order)[$Properties(Customer)[$Type[$Properties[<$Name>]]$Type[$Properties[<$Name>]]$Type[$Properties[<$Name>]]]]";

        var output = Render(metadata: metadata, template: template);

        // Same result each time round; a factory that rebuilt divergent state would break this.
        CountOccurrences(value: output, token: "<Id>").Should().Be(3);
        CountOccurrences(value: output, token: "<Name>").Should().Be(3);
    }

    // A type whose property is typed as itself is the shape that used to overflow the stack:
    // building the reference built its members, whose types were references, and so on. Rendering
    // it at all is the assertion.
    [Fact]
    public void SelfReferentialTypeDoesNotRecurseWithoutBound()
    {
        var node = new TypeMetadata(
            Name: "Node",
            FullName: "Sample.Node",
            Namespace: "Sample",
            Kind: TypeMetadataKind.Class,
            Accessibility: MetadataAccessibility.Public,
            Properties:
            [
                new PropertyMetadata(
                    Name: "Parent",
                    FullName: "Sample.Node.Parent",
                    Type: DeclarationVersusReferenceFixtures.Reference(name: "Node", fullName: "Sample.Node"),
                    Accessibility: MetadataAccessibility.Public,
                    HasGetter: true,
                    HasSetter: true,
                    IsRequired: false,
                    Attributes: []),
            ],
            Attributes: [],
            BaseTypes: [],
            EnumValues: [],
            IsNullableAware: true);

        var metadata = new ProjectMetadata(
            ProjectPath: "Sample.csproj",
            SourceFiles: [],
            Types: [node],
            Diagnostics: []);

        const string template = "$Classes(Node)[$Properties(Parent)[$Type[$Properties[<$Name>]]]]";

        var output = Render(metadata: metadata, template: template);

        output.Should().Contain("<Parent>");
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = value.IndexOf(value: token, comparisonType: StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = value.IndexOf(value: token, startIndex: index + token.Length, comparisonType: StringComparison.Ordinal);
        }

        return count;
    }

    private static string Render(ProjectMetadata metadata, string template)
    {
        var diagnostics = new List<GenerationDiagnostic>();
        var renderer = new TemplateRenderer(typeMapper: new TypeScriptTypeMapper());
        var document = TemplateDocument.Parse(
            template: new TemplateFile(Path: "models.tst", Content: template),
            diagnostics: diagnostics);

        return renderer.Render(template: document, metadata: metadata, diagnostics: diagnostics);
    }
}
