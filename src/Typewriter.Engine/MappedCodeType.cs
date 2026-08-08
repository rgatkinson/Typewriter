namespace Typewriter.Engine;

using System.Runtime.CompilerServices;

/// <summary>
/// The code model <c>Type</c> produced for a type <em>reference</em> (a use of a type, as opposed
/// to its declaration).
/// </summary>
/// <remarks>
/// <para>
/// This class is where the declaration-vs-reference split is reconciled. The reference-level facts
/// (nullability, collection-ness, type arguments, the mapped TypeScript name) come from the
/// reference itself and are set directly by the factory. The <em>declared</em> members -- properties,
/// methods, base class, nested types, and so on -- are not available on a reference at all; they
/// live on the declaration. When that declaration is part of the compilation, the factory supplies
/// it via <see cref="DeclaredMembers"/> and this class projects it onto the code model surface,
/// restoring the unified view that v3 templates were written against.
/// </para>
/// <para>
/// Two properties of the design matter, and they are in tension:
/// </para>
/// <para>
/// <b>Laziness is required for correctness.</b> The declared members cannot be materialised during
/// construction. Doing so does not terminate: building a type reference would build its methods,
/// whose parameters are themselves type references, which would build their methods, without
/// bound. That was an observed stack overflow, not a hypothetical one. Deferring the work until a
/// template actually walks a collection breaks the cycle, because template traversal is finite
/// where eager construction is not.
/// </para>
/// <para>
/// <b>Caching is required for performance.</b> Laziness alone means every access re-runs the
/// factory, so a template looping over a referenced type's members rebuilds the whole collection on
/// each iteration -- quadratic behaviour on large models. Memoising the result is safe precisely
/// because of the ordering: by the time a factory returns, the recursion it might have triggered
/// has already terminated, so there is no partially-constructed state to cache.
/// </para>
/// <para>
/// When <see cref="DeclaredMembers"/> is <see langword="null"/> the inherited empty/null defaults
/// stand. That is the correct answer, not a degraded one: the type is external to the compilation
/// (BCL, third-party assembly) and genuinely has no declaration for us to project.
/// </para>
/// </remarks>
internal sealed class MappedCodeType : Typewriter.CodeModel.Type
{
    // Memoisation backing fields for the collection-valued members. Null means "not yet
    // materialised"; a collection is never legitimately null, so a plain field suffices here.
    private Typewriter.CodeModel.IConstantCollection? _constants;
    private Typewriter.CodeModel.IDelegateCollection? _delegates;
    private Typewriter.CodeModel.IFieldCollection? _fields;
    private Typewriter.CodeModel.IInterfaceCollection? _interfaces;
    private Typewriter.CodeModel.IMethodCollection? _methods;
    private Typewriter.CodeModel.IPropertyCollection? _properties;
    private Typewriter.CodeModel.IStaticReadOnlyFieldCollection? _staticReadOnlyFields;
    private Typewriter.CodeModel.IClassCollection? _nestedClasses;
    private Typewriter.CodeModel.IEnumCollection? _nestedEnums;
    private Typewriter.CodeModel.IInterfaceCollection? _nestedInterfaces;
    private Typewriter.CodeModel.IRecordCollection? _nestedRecords;
    private Typewriter.CodeModel.IStructCollection? _nestedStructs;
    private Typewriter.CodeModel.ITypeParameterCollection? _typeParameters;

    // The members below legitimately resolve to null: a type may have no base class, no containing
    // type, and no doc comment. A plain null field therefore cannot distinguish "not yet computed"
    // from "computed, and the answer is null", and would re-run the factory on every access.
    // Boxing the cached result restores that distinction so the factory runs at most once.
    private StrongBox<Typewriter.CodeModel.Class?>? _baseClass;
    private StrongBox<Typewriter.CodeModel.Class?>? _containingClass;
    private StrongBox<Typewriter.CodeModel.DocComment?>? _docComment;

    public bool UseResolvedDefault { get; init; }

    /// <summary>
    /// Gets the declared-member factories carried over from this type's declaration, or
    /// <see langword="null"/> when the declaration is not part of the compilation.
    /// </summary>
    /// <remarks>
    /// This is a single object rather than one property per member by design. The reference path
    /// and the declaration path previously each maintained their own list of member assignments,
    /// and the reference path's list kept falling behind -- which is exactly how the
    /// declaration-vs-reference bug regressed repeatedly. Funnelling both through one shared
    /// <see cref="DeclaredTypeMembers"/> makes it impossible to wire a member into one path and
    /// forget the other.
    /// </remarks>
    public DeclaredTypeMembers? DeclaredMembers { get; init; }

    public override Typewriter.CodeModel.IConstantCollection Constants =>
        _constants ??= Materialise(factory: DeclaredMembers?.Constants, fallback: base.Constants);

    public override Typewriter.CodeModel.IDelegateCollection Delegates =>
        _delegates ??= Materialise(factory: DeclaredMembers?.Delegates, fallback: base.Delegates);

    public override Typewriter.CodeModel.IFieldCollection Fields =>
        _fields ??= Materialise(factory: DeclaredMembers?.Fields, fallback: base.Fields);

    public override Typewriter.CodeModel.IInterfaceCollection Interfaces =>
        _interfaces ??= Materialise(factory: DeclaredMembers?.Interfaces, fallback: base.Interfaces);

    public override Typewriter.CodeModel.IMethodCollection Methods =>
        _methods ??= Materialise(factory: DeclaredMembers?.Methods, fallback: base.Methods);

    public override Typewriter.CodeModel.IPropertyCollection Properties =>
        _properties ??= Materialise(factory: DeclaredMembers?.Properties, fallback: base.Properties);

    public override Typewriter.CodeModel.IStaticReadOnlyFieldCollection StaticReadOnlyFields =>
        _staticReadOnlyFields ??= Materialise(factory: DeclaredMembers?.StaticReadOnlyFields, fallback: base.StaticReadOnlyFields);

    public override Typewriter.CodeModel.IClassCollection NestedClasses =>
        _nestedClasses ??= Materialise(factory: DeclaredMembers?.NestedClasses, fallback: base.NestedClasses);

    public override Typewriter.CodeModel.IEnumCollection NestedEnums =>
        _nestedEnums ??= Materialise(factory: DeclaredMembers?.NestedEnums, fallback: base.NestedEnums);

    public override Typewriter.CodeModel.IInterfaceCollection NestedInterfaces =>
        _nestedInterfaces ??= Materialise(factory: DeclaredMembers?.NestedInterfaces, fallback: base.NestedInterfaces);

    public override Typewriter.CodeModel.IRecordCollection NestedRecords =>
        _nestedRecords ??= Materialise(factory: DeclaredMembers?.NestedRecords, fallback: base.NestedRecords);

    public override Typewriter.CodeModel.IStructCollection NestedStructs =>
        _nestedStructs ??= Materialise(factory: DeclaredMembers?.NestedStructs, fallback: base.NestedStructs);

    public override Typewriter.CodeModel.ITypeParameterCollection TypeParameters =>
        _typeParameters ??= Materialise(factory: DeclaredMembers?.TypeParameters, fallback: base.TypeParameters);

    public override Typewriter.CodeModel.Class? BaseClass =>
        (_baseClass ??= MaterialiseOptional(factory: DeclaredMembers?.BaseClass, fallback: base.BaseClass)).Value;

    public override Typewriter.CodeModel.Class? ContainingClass =>
        (_containingClass ??= MaterialiseOptional(factory: DeclaredMembers?.ContainingClass, fallback: base.ContainingClass)).Value;

    public override Typewriter.CodeModel.DocComment? DocComment =>
        (_docComment ??= MaterialiseOptional(factory: DeclaredMembers?.DocComment, fallback: base.DocComment)).Value;

    // Note the null-conditional invocation. The equivalent `factory is null ? fallback : factory()`
    // reads more naturally but trips analyzer CC0031, which cannot see that the ternary already
    // guards the call.
    private static T Materialise<T>(Func<T>? factory, T fallback)
        where T : class
    {
        return factory?.Invoke() ?? fallback;
    }

    // Returns a box rather than the value itself so the caller can cache a null result and still
    // distinguish it from "not yet computed"; see the field declarations above.
    private static StrongBox<T?> MaterialiseOptional<T>(Func<T?>? factory, T? fallback)
        where T : class
    {
        return new StrongBox<T?>(value: factory?.Invoke() ?? fallback);
    }
}
