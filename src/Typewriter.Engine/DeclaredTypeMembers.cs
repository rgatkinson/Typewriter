namespace Typewriter.Engine;

/// <summary>
/// Single source of truth for the members that a <em>type declaration</em> contributes to the code
/// model.
/// </summary>
/// <remarks>
/// <para>
/// Background: v3 of Typewriter exposed one <c>Type</c> abstraction that carried both the
/// reference-level facts about a type (is it nullable, is it a collection, what are its type
/// arguments) and the members declared on it (its properties, methods, base class, and so on).
/// Templates relied on that unified surface and freely wrote expressions such as
/// <c>$Property.Type.Properties</c> or <c>$Parameter.Type.BaseClass</c>, reaching from a
/// <em>reference</em> straight through to <em>declared</em> members.
/// </para>
/// <para>
/// v4 split the two: <c>TypeMetadata</c> models a declaration, <c>TypeMetadataReference</c> models a
/// use of a type. Only the declaration path populated declared members, so on the reference path
/// those members silently fell back to their empty/null defaults. The failure mode was pernicious
/// precisely because it was silent: a template loop over <c>$Property.Type.Properties</c> simply
/// produced nothing rather than raising an error, so generated output was quietly incomplete.
/// </para>
/// <para>
/// That bug was fixed twice and regressed twice, because the declaration path and the reference
/// path were two hand-maintained parallel lists of member assignments. Adding a member to one and
/// forgetting the other reintroduced the same class of bug. This type exists to remove that failure
/// mode structurally: both paths are built from this one set, so a member cannot be wired into one
/// path and omitted from the other.
/// </para>
/// <para>
/// Members are exposed as factory delegates rather than materialised values because the reference
/// path <em>must</em> defer construction. Building a referenced type's members eagerly does not
/// terminate: constructing a type reference would build its methods, whose parameters are
/// themselves type references, which would build their methods, and so on until the stack is
/// exhausted. (This was a real stack overflow, not a theoretical one.) Deferring means a collection
/// is materialised only when a template actually walks it, which terminates naturally. The
/// declaration path has no such cycle and simply invokes the delegates immediately.
/// </para>
/// </remarks>
internal sealed class DeclaredTypeMembers
{
    /// <summary>
    /// Gets the factory for the type's attributes.
    /// </summary>
    /// <remarks>
    /// This is the one member whose construction genuinely differs between the two paths: the
    /// declaration path builds full attribute models, whereas the reference path builds the
    /// reference-safe variant that does not recurse back into type construction. The difference is
    /// supplied by the caller rather than being decided here.
    /// </remarks>
    public required Func<Typewriter.CodeModel.IAttributeCollection> Attributes { get; init; }

    public required Func<Typewriter.CodeModel.IConstantCollection> Constants { get; init; }

    public required Func<Typewriter.CodeModel.IDelegateCollection> Delegates { get; init; }

    public required Func<Typewriter.CodeModel.IFieldCollection> Fields { get; init; }

    public required Func<Typewriter.CodeModel.IInterfaceCollection> Interfaces { get; init; }

    public required Func<Typewriter.CodeModel.IMethodCollection> Methods { get; init; }

    public required Func<Typewriter.CodeModel.IPropertyCollection> Properties { get; init; }

    public required Func<Typewriter.CodeModel.IStaticReadOnlyFieldCollection> StaticReadOnlyFields { get; init; }

    public required Func<Typewriter.CodeModel.IClassCollection> NestedClasses { get; init; }

    public required Func<Typewriter.CodeModel.IEnumCollection> NestedEnums { get; init; }

    public required Func<Typewriter.CodeModel.IInterfaceCollection> NestedInterfaces { get; init; }

    public required Func<Typewriter.CodeModel.IRecordCollection> NestedRecords { get; init; }

    public required Func<Typewriter.CodeModel.IStructCollection> NestedStructs { get; init; }

    public required Func<Typewriter.CodeModel.ITypeParameterCollection> TypeParameters { get; init; }

    public required Func<Typewriter.CodeModel.Class?> BaseClass { get; init; }

    public required Func<Typewriter.CodeModel.Class?> ContainingClass { get; init; }

    public required Func<Typewriter.CodeModel.DocComment?> DocComment { get; init; }
}
