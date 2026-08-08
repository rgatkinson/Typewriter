using Typewriter.Abstractions;

namespace Typewriter.Engine.Tests;

/// <summary>
/// Shared metadata fixtures for the declaration-vs-reference regression tests.
/// </summary>
/// <remarks>
/// The model deliberately populates every declared-member category so that a member restored on
/// the declaration path but forgotten on the reference path is caught by the tests. It also
/// includes a property whose type has no declaration in the project, pinning the correct empty
/// result for types genuinely outside the compilation.
/// </remarks>
internal static class DeclarationVersusReferenceFixtures
{
    internal static TypeMetadataReference Reference(string name, string fullName)
    {
        return new TypeMetadataReference(
            Name: name,
            FullName: fullName,
            Namespace: "Sample",
            IsNullable: false,
            IsCollection: false,
            IsDictionary: false,
            IsEnum: false,
            IsPrimitive: false,
            IsDateLike: false,
            ElementType: null,
            TypeArguments: []);
    }

    // Builds an Order whose Customer property references a Customer declaration that is rich in
    // every declared-member category, plus a Reference property pointing outside the compilation.
    internal static ProjectMetadata CreateProjectMetadata()
    {
        var stringReference = new TypeMetadataReference(
            Name: "string",
            FullName: "System.String",
            Namespace: "System",
            IsNullable: false,
            IsCollection: false,
            IsDictionary: false,
            IsEnum: false,
            IsPrimitive: true,
            IsDateLike: false,
            ElementType: null,
            TypeArguments: []);

        var intReference = new TypeMetadataReference(
            Name: "int",
            FullName: "System.Int32",
            Namespace: "System",
            IsNullable: false,
            IsCollection: false,
            IsDictionary: false,
            IsEnum: false,
            IsPrimitive: true,
            IsDateLike: false,
            ElementType: null,
            TypeArguments: []);

        var auditable = new TypeMetadata(
            Name: "IAuditable",
            FullName: "Sample.IAuditable",
            Namespace: "Sample",
            Kind: TypeMetadataKind.Interface,
            Accessibility: MetadataAccessibility.Public,
            Properties: [],
            Attributes: [],
            BaseTypes: [],
            EnumValues: [],
            IsNullableAware: true);

        var entityBase = new TypeMetadata(
            Name: "EntityBase",
            FullName: "Sample.EntityBase",
            Namespace: "Sample",
            Kind: TypeMetadataKind.Class,
            Accessibility: MetadataAccessibility.Public,
            Properties: [],
            Attributes: [],
            BaseTypes: [],
            EnumValues: [],
            IsNullableAware: true);

        var nestedAddress = new TypeMetadata(
            Name: "Address",
            FullName: "Sample.Customer.Address",
            Namespace: "Sample",
            Kind: TypeMetadataKind.Class,
            Accessibility: MetadataAccessibility.Public,
            Properties: [],
            Attributes: [],
            BaseTypes: [],
            EnumValues: [],
            IsNullableAware: true);

        var nestedTier = new TypeMetadata(
            Name: "Tier",
            FullName: "Sample.Customer.Tier",
            Namespace: "Sample",
            Kind: TypeMetadataKind.Enum,
            Accessibility: MetadataAccessibility.Public,
            Properties: [],
            Attributes: [],
            BaseTypes: [],
            EnumValues: [],
            IsNullableAware: true);

        var customer = new TypeMetadata(
            Name: "Customer",
            FullName: "Sample.Customer",
            Namespace: "Sample",
            Kind: TypeMetadataKind.Class,
            Accessibility: MetadataAccessibility.Public,
            Properties:
            [
                new PropertyMetadata(
                    Name: "Id",
                    FullName: "Sample.Customer.Id",
                    Type: intReference,
                    Accessibility: MetadataAccessibility.Public,
                    HasGetter: true,
                    HasSetter: true,
                    IsRequired: false,
                    Attributes: []),
                new PropertyMetadata(
                    Name: "Name",
                    FullName: "Sample.Customer.Name",
                    Type: stringReference,
                    Accessibility: MetadataAccessibility.Public,
                    HasGetter: true,
                    HasSetter: true,
                    IsRequired: false,
                    Attributes: []),
            ],
            Attributes: [],
            BaseTypes: [Reference(name: "EntityBase", fullName: "Sample.EntityBase"), Reference(name: "IAuditable", fullName: "Sample.IAuditable")],
            EnumValues: [],
            IsNullableAware: true)
        {
            DocComment = new DocCommentMetadata(Summary: "A customer.", Returns: string.Empty, Parameters: []),
            Methods =
            [
                new MethodMetadata(
                    Name: "Deactivate",
                    FullName: "Sample.Customer.Deactivate",
                    ReturnType: stringReference,
                    Accessibility: MetadataAccessibility.Public,
                    IsStatic: false,
                    IsAbstract: false,
                    IsGeneric: false,
                    Parameters: [],
                    Attributes: [],
                    ParentTypeFullName: "Sample.Customer"),
            ],
            Constants =
            [
                new ConstantMetadata(
                    Name: "MaxNameLength",
                    FullName: "Sample.Customer.MaxNameLength",
                    Accessibility: MetadataAccessibility.Public,
                    Type: intReference,
                    Value: "128",
                    Attributes: [],
                    ParentTypeFullName: "Sample.Customer"),
            ],
            TypeParameters = [new TypeParameterMetadata(Name: "T")],
            NestedClasses = [nestedAddress],
            NestedEnums = [nestedTier],
        };

        var order = new TypeMetadata(
            Name: "Order",
            FullName: "Sample.Order",
            Namespace: "Sample",
            Kind: TypeMetadataKind.Class,
            Accessibility: MetadataAccessibility.Public,
            Properties:
            [
                new PropertyMetadata(
                    Name: "Customer",
                    FullName: "Sample.Order.Customer",
                    Type: Reference(name: "Customer", fullName: "Sample.Customer"),
                    Accessibility: MetadataAccessibility.Public,
                    HasGetter: true,
                    HasSetter: true,
                    IsRequired: false,
                    Attributes: []),

                // Deliberately points at a type with no declaration in this project.
                new PropertyMetadata(
                    Name: "ExternalLink",
                    FullName: "Sample.Order.ExternalLink",
                    Type: new TypeMetadataReference(
                        Name: "Uri",
                        FullName: "System.Uri",
                        Namespace: "System",
                        IsNullable: false,
                        IsCollection: false,
                        IsDictionary: false,
                        IsEnum: false,
                        IsPrimitive: false,
                        IsDateLike: false,
                        ElementType: null,
                        TypeArguments: []),
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

        return new ProjectMetadata(
            ProjectPath: "Sample.csproj",
            SourceFiles: [],
            Types: [order, customer, entityBase, auditable],
            Diagnostics: []);
    }
}
