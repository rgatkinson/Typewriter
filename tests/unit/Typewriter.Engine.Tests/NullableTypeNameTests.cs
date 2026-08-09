using Typewriter.Abstractions;
using Typewriter.Engine;
using Xunit;

namespace Typewriter.Engine.Tests;

// Typewriter v3.0.1 exposed nullability in the C#-facing type names: RoslynTypeMetadata
// appended "?" to both Name and FullName whenever IsNullable was set, so a template reading
// $OriginalName for a `FacilityId?` parameter saw "FacilityId?" and could round-trip the
// declaration back into C#. The v4 rewrite unwraps Nullable<T> to its underlying type and
// records nullability only on the IsNullable flag, so the "?" was silently dropped.
//
// That silence is what makes the regression dangerous: emitting `FacilityId` where
// `FacilityId?` was meant still compiles, so a generated API client quietly loses the ability
// to pass null for a genuinely optional query parameter instead of failing loudly.
//
// These tests pin the restored v3 contract for the template-facing surface.
public sealed class NullableTypeNameTests
{
    [Fact]
    public void RenderAppendsNullableSuffixToOriginalNameForNullableValueTypes()
    {
        var output = RenderPropertyNames(
            properties:
            [
                Property(name: "Id", type: TypeReference(name: "Int32", fullName: "System.Int32", isNullable: false)),
                Property(name: "Count", type: TypeReference(name: "Int32", fullName: "System.Int32", isNullable: true)),
            ]);

        // v3 mapped primitives to their C# keyword and appended "?" for nullables.
        output.Should().Contain("Id=int");
        output.Should().Contain("Count=int?");
    }

    [Fact]
    public void RenderAppendsNullableSuffixToOriginalNameForNullableStronglyTypedIds()
    {
        // The regression that surfaced in practice: a nullable strongly-typed struct id used as
        // an optional [FromQuery] parameter. Dropping the "?" still compiles, so nothing fails
        // until a caller needs to omit the value.
        var output = RenderPropertyNames(
            properties:
            [
                Property(name: "OrgId", type: TypeReference(name: "OrgId", fullName: "Sample.OrgId", isNullable: false)),
                Property(name: "FacilityId", type: TypeReference(name: "FacilityId", fullName: "Sample.FacilityId", isNullable: true)),
            ]);

        output.Should().Contain("OrgId=OrgId");
        output.Should().Contain("FacilityId=FacilityId?");
    }

    [Fact]
    public void RenderAppendsNullableSuffixToOriginalNameForNullableReferenceTypes()
    {
        // v3 derived IsNullable from the nullable annotation as well as from Nullable<T>, so an
        // annotated reference type carried the suffix too.
        var output = RenderPropertyNames(
            properties:
            [
                Property(name: "Required", type: TypeReference(name: "String", fullName: "System.String", isNullable: false)),
                Property(name: "Optional", type: TypeReference(name: "String", fullName: "System.String", isNullable: true)),
            ]);

        output.Should().Contain("Required=string");
        output.Should().Contain("Optional=string?");
    }

    [Fact]
    public void RenderTrimsNullableSuffixFromNameForNonPrimitiveTypes()
    {
        // v3.0.1's Helpers.GetTypeScriptName explicitly called metadata.Name.TrimEnd('?') for
        // nullable types, so $Name never carried the suffix even though $OriginalName and
        // $FullName did. A trailing '?' is not valid TypeScript here; " | null" is the TypeScript
        // spelling of the same idea. This pins that asymmetry.
        var output = RenderTemplate(
            properties:
            [
                Property(name: "FacilityId", type: TypeReference(name: "FacilityId", fullName: "Sample.FacilityId", isNullable: true)),
            ],
            template: "$Classes[$Properties[$Name=$Type[$Name];]]");

        output.Should().Contain("FacilityId=FacilityId | null;");
        output.Should().NotContain("FacilityId?");
    }

    [Fact]
    public void RenderKeepsNullableSuffixDistinctFromTypeScriptName()
    {
        // Type.Name is the TypeScript-facing name and is governed by StrictNullGeneration's
        // " | null" convention; OriginalName is the C#-facing name. The nullable suffix belongs
        // only to the latter, so restoring it must not leak a "?" into TypeScript output.
        // StrictNullGeneration is on by default here, which is why $Name renders " | null".
        var output = RenderTemplate(
            properties:
            [
                Property(name: "Count", type: TypeReference(name: "Int32", fullName: "System.Int32", isNullable: true)),
            ],
            template: "$Classes[$Properties[$Name=$Type[$Name|$OriginalName];]]");

        // The TypeScript name expresses nullability as " | null"; the C# name uses "?". Neither
        // convention leaks into the other.
        output.Should().Contain("Count=number | null|int?;");
    }

    [Fact]
    public void RenderKeepsNullableSuffixOnOriginalNameWhenStrictNullGenerationIsDisabled()
    {
        // The two conventions are toggled independently: DisableStrictNullGeneration() suppresses
        // the TypeScript " | null" but says nothing about the C# name. OriginalName must still
        // carry its "?", otherwise turning strict null off would silently reintroduce the very
        // regression this change fixes.
        var output = RenderTemplate(
            properties:
            [
                Property(name: "Count", type: TypeReference(name: "Int32", fullName: "System.Int32", isNullable: true)),
            ],
            template: """
                ${
                    Template(Settings settings)
                    {
                        settings.DisableStrictNullGeneration();
                    }
                }
                $Classes[$Properties[$Name=$Type[$Name|$OriginalName];]]
                """);

        output.Should().Contain("Count=number|int?;");
        output.Should().NotContain("| null");
    }

    [Fact]
    public void RenderDoesNotDoubleAppendNullableSuffix()
    {
        // Guards the obvious implementation slip of appending "?" to a name that already ends in
        // one, which would produce uncompilable "int??".
        var output = RenderPropertyNames(
            properties:
            [
                Property(name: "Count", type: TypeReference(name: "Int32?", fullName: "System.Int32", isNullable: true)),
            ]);

        output.Should().Contain("Count=int?");
        output.Should().NotContain("??");
    }

    [Fact]
    public void RenderKeepsNullableTypeClassificationFlagsIntact()
    {
        // The nullable suffix is presentation only. Classification still keys off the underlying
        // type, so IsGuid/IsDate/IsPrimitive must not regress when the suffix is applied --
        // these are resolved from FullName, which templates also compare ordinally.
        var output = RenderTemplate(
            properties:
            [
                Property(name: "Key", type: TypeReference(name: "Guid", fullName: "System.Guid", isNullable: true)),
                Property(name: "When", type: TypeReference(name: "DateTime", fullName: "System.DateTime", isNullable: true, isDateLike: true)),
            ],
            template: "$Classes[$Properties[$Name=$Type[$OriginalName|$IsGuid|$IsDate|$IsNullable];]]");

        output.Should().Contain("Key=Guid?|true|false|true;");
        output.Should().Contain("When=DateTime?|false|true|true;");
    }

    [Fact]
    public void RenderAppendsNullableSuffixToTemplateFacingFullName()
    {
        // v3.0.1 suffixed FullName as well as OriginalName. v4 keeps the underlying value
        // un-suffixed (it is the metadata lookup key) and applies the suffix when the template
        // reads $FullName.
        var output = RenderTemplate(
            properties:
            [
                Property(name: "OrgId", type: TypeReference(name: "OrgId", fullName: "Sample.OrgId", isNullable: false)),
                Property(name: "FacilityId", type: TypeReference(name: "FacilityId", fullName: "Sample.FacilityId", isNullable: true)),
            ],
            template: "$Classes[$Properties[$Name=$Type[$FullName];]]");

        output.Should().Contain("OrgId=Sample.OrgId;");
        output.Should().Contain("FacilityId=Sample.FacilityId?;");
    }

    [Fact]
    public void RenderKeepsNullableSuffixOutOfNamespaceAndClassNames()
    {
        // Only the type reference is nullable; the declaring class name and namespace must be
        // untouched, so the suffix cannot leak into file names or import paths.
        var output = RenderTemplate(
            properties:
            [
                Property(name: "Count", type: TypeReference(name: "Int32", fullName: "System.Int32", isNullable: true)),
            ],
            template: "$Classes[$Name|$FullName|$Namespace]");

        output.Should().Contain("Sample|Sample.Sample|Sample");
        output.Should().NotContain("Sample?");
    }

    [Fact]
    public void RenderMatchesNullableTypesByTheirUnsuffixedNameInFilters()
    {
        // Existing templates filter on the un-suffixed C# name (for example $Types(int)). Adding
        // the suffix to OriginalName must not stop those filters from matching, so TypeCollection
        // offers both spellings.
        var output = RenderTemplate(
            properties:
            [
                Property(name: "Count", type: TypeReference(name: "Int32", fullName: "System.Int32", isNullable: true)),
            ],
            template: "$Classes[$Properties[$Type(int)[matched:$Name;]]]");

        output.Should().Contain("matched:");
    }

    [Fact]
    public void RenderMatchesNullableTypesByTheirSuffixedNameInFilters()
    {
        // The suffixed spelling must match too, so templates can target only the nullable case.
        var output = RenderTemplate(
            properties:
            [
                Property(name: "Count", type: TypeReference(name: "Int32", fullName: "System.Int32", isNullable: true)),
            ],
            template: "$Classes[$Properties[$Type(int?)[matched:$Name;]]]");

        output.Should().Contain("matched:");
    }

    private static string RenderPropertyNames(IReadOnlyList<PropertyMetadata> properties) =>
        RenderTemplate(properties: properties, template: "$Classes[$Properties[$Name=$Type[$OriginalName];]]");

    private static string RenderTemplate(IReadOnlyList<PropertyMetadata> properties, string template)
    {
        var metadata = new ProjectMetadata(
            ProjectPath: "Sample.csproj",
            SourceFiles: [],
            Types:
            [
                new TypeMetadata(
                    Name: "Sample",
                    FullName: "Sample.Sample",
                    Namespace: "Sample",
                    Kind: TypeMetadataKind.Class,
                    Accessibility: MetadataAccessibility.Public,
                    Properties: properties,
                    Attributes: [],
                    BaseTypes: [],
                    EnumValues: [],
                    IsNullableAware: true),
            ],
            Diagnostics: []);
        var diagnostics = new List<GenerationDiagnostic>();
        var renderer = new TemplateRenderer(typeMapper: new TypeScriptTypeMapper());

        // Parse rather than constructing TemplateDocument directly so templates in these tests can
        // use a ${ Template(Settings settings) } block to vary configuration.
        var document = TemplateDocument.Parse(
            template: new TemplateFile(Path: Path.Combine(path1: Path.GetTempPath(), path2: "models.tst"), Content: template),
            diagnostics: diagnostics);

        var output = renderer.Render(
            template: document,
            metadata: metadata,
            diagnostics: diagnostics);

        diagnostics.Should().BeEmpty();
        return output;
    }

    private static PropertyMetadata Property(string name, TypeMetadataReference type) =>
        new(
            Name: name,
            FullName: "Sample.Sample." + name,
            Type: type,
            Accessibility: MetadataAccessibility.Public,
            HasGetter: true,
            HasSetter: true,
            IsRequired: false,
            Attributes: []);

    private static TypeMetadataReference TypeReference(
        string name,
        string fullName,
        bool isNullable,
        bool isDateLike = false) =>
        new(
            Name: name,
            FullName: fullName,
            Namespace: GetNamespace(fullName: fullName),
            IsNullable: isNullable,
            IsCollection: false,
            IsDictionary: false,
            IsEnum: false,
            IsPrimitive: IsPrimitive(fullName: fullName),
            IsDateLike: isDateLike,
            ElementType: null,
            TypeArguments: []);

    private static string GetNamespace(string fullName)
    {
        var index = fullName.LastIndexOf(value: '.');
        return index < 0 ? string.Empty : fullName[..index];
    }

    private static bool IsPrimitive(string fullName) =>
        fullName is "System.Boolean"
            or "System.Byte"
            or "System.SByte"
            or "System.Int16"
            or "System.UInt16"
            or "System.Int32"
            or "System.UInt32"
            or "System.Int64"
            or "System.UInt64"
            or "System.Single"
            or "System.Double"
            or "System.Decimal"
            or "System.String"
            or "System.Char";
}
