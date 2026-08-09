using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Typewriter.Abstractions;
using CodeAttribute = Typewriter.CodeModel.Attribute;
using CodeClass = Typewriter.CodeModel.Class;
using CodeConstant = Typewriter.CodeModel.Constant;
using CodeDelegate = Typewriter.CodeModel.Delegate;
using CodeEnum = Typewriter.CodeModel.Enum;
using CodeEvent = Typewriter.CodeModel.Event;
using CodeField = Typewriter.CodeModel.Field;
using CodeFile = Typewriter.CodeModel.File;
using CodeInterface = Typewriter.CodeModel.Interface;
using CodeMethod = Typewriter.CodeModel.Method;
using CodeParameter = Typewriter.CodeModel.Parameter;
using CodeRecord = Typewriter.CodeModel.Record;
using CodeStaticReadOnlyField = Typewriter.CodeModel.StaticReadOnlyField;
using CodeStruct = Typewriter.CodeModel.Struct;
using CodeType = Typewriter.CodeModel.Type;

namespace Typewriter.Engine;

internal sealed class TemplateCodeModelAdapterFactory
{
    private readonly Typewriter.Configuration.Settings _settings;
    private readonly string _assemblyName;
    private readonly IReadOnlyDictionary<string, MethodMetadata> _methodsByFullName;
    private readonly IReadOnlyDictionary<string, PropertyMetadata> _propertiesByFullName;
    private readonly IReadOnlyDictionary<string, TypeMetadata> _typesByFullName;
    private readonly TypeScriptTypeMapper _typeMapper = new();

    // PERFORMANCE: significant on the warm render path. Re-measure before removing.
    //
    // These caches are not a speculative micro-optimisation -- they were added in response to a CPU
    // profile of a real workload (the Anavasi solution, 146 generated files) and are worth roughly
    // a 7x reduction in *warm* render time:
    //
    //     before caching:  2.368 s   (+/- 0.057 s, 15 iterations)
    //     after caching:   0.318 s   (+/- 0.010 s, 15 iterations)
    //
    // READ THIS BEFORE TRUSTING THE NUMBERS ABOVE. Those figures come from BenchmarkDotNet, and
    // BenchmarkDotNet only ever measures the warm path here: CSharpProjectMetadataProvider holds
    // static caches, so by the second iteration the workload is warm in ways a freshly started CLI
    // process never is. The 7x is real for that path, but it did NOT move end-to-end wall clock:
    //
    //     RegenAnavasi.ps1 before caching:  ~54 s
    //     RegenAnavasi.ps1 after caching:   ~53 s
    //
    // Measured phase split of a cold CLI run (~52 s): ~28 s workspace/metadata load, ~23 s
    // per-template work, <1 s template compilation, ~0.1 s process startup. The ~23 s of cold
    // per-template work is the same logical operation the benchmark reports as 318 ms -- a ~70x
    // gap. Whatever dominates that cold path is therefore NOT the redundant adapter construction
    // these caches eliminate.
    //
    // The lesson, and the reason this comment is long: a large warm-path win can be worth nothing
    // end to end. Validate against a real cold run before claiming a script-level improvement.
    //
    // The cost being avoided is nonetheless real. Rendering adapts the same metadata objects over
    // and over for two independent reasons:
    //
    //   1. CompiledTemplateHelper.TryInvoke probes each *candidate overload* of a template method
    //      and calls TryAdapt once per candidate, so a single template call site can adapt the same
    //      metadata several times before a match is found.
    //   2. Templates naturally revisit the same types through properties, base classes, interfaces,
    //      and type arguments.
    //
    // Without memoisation each of those hits rebuilds an entire object graph from scratch --
    // CreateClass eagerly materialises every constant, delegate, event, field, method, property,
    // static readonly field, and all five nested-type collections. The profile showed the
    // reference-taking CreateType overload at 41% of render CPU and the Type constructor at ~15%
    // self CPU; after caching, both dropped out of the hot path entirely.
    //
    // Correctness notes:
    //   - Both factories are pure functions of their inputs once _settings is fixed, and one factory
    //     instance exists per render, so a per-instance cache cannot leak state across renders or
    //     across differing settings.
    //   - Reference identity is the correct key: the metadata objects are interned by
    //     ProjectMetadataIndex for the lifetime of a render, and structural equality would be both
    //     slower and wrong (two structurally identical references can carry different declaration
    //     context).
    //   - Note which overloads are deliberately NOT cached; see CreateClass and CreateType below.
    //
    // THREADING: these are plain Dictionary instances, which is only safe because rendering is
    // single-threaded per factory instance. One factory is created per compiled template
    // (TemplateRuntimeCompiler.CreateHelper) and the generator renders source files sequentially --
    // there is no Parallel.* or Task.WhenAll on the render path. If rendering is ever parallelised,
    // these become data races: switch to ConcurrentDictionary (or give each render its own factory)
    // at that point rather than assuming the dictionaries are safe.
    private readonly Dictionary<(TypeMetadataReference Reference, FrontendRuntimeTypeKind RuntimeType), CodeType> _typeByReference = new(comparer: TypeReferenceKeyComparer.Instance);
    private readonly Dictionary<TypeMetadata, CodeClass> _classByMetadata = new(comparer: ReferenceEqualityComparer.Instance);

    // Unlike the two caches above, this one measured as a wash (329 ms -> 318 ms, inside the noise
    // band). It is retained because it is correct and effectively free, and it guards against
    // redundant construction on templates whose shape differs from the workload profiled above --
    // but do not expect it to show up in a benchmark, and do not treat its presence as evidence
    // that declaration-path construction is a bottleneck.
    private readonly Dictionary<TypeMetadata, CodeType> _typeByMetadata = new(comparer: ReferenceEqualityComparer.Instance);
    private CodeFile? _cachedFile;
    private ProjectMetadata? _cachedFileProject;

    public TemplateCodeModelAdapterFactory(
        ProjectMetadata metadata,
        string templatePath)
        : this(
            metadata: metadata,
            settings: new Typewriter.Configuration.Settings
            {
                TemplatePath = templatePath,
            })
    {
    }

    public TemplateCodeModelAdapterFactory(
        ProjectMetadata metadata,
        Typewriter.Configuration.Settings settings)
        : this(metadata: metadata, settings: settings, metadataIndex: null)
    {
    }

    public TemplateCodeModelAdapterFactory(
        ProjectMetadata metadata,
        Typewriter.Configuration.Settings settings,
        ProjectMetadataIndex? metadataIndex)
    {
        _settings = settings;
        _assemblyName = Path.GetFileNameWithoutExtension(path: metadata.ProjectPath);
        var index = metadataIndex ?? ProjectMetadataIndex.Create(metadata: metadata);
        _typesByFullName = index.TypesByFullName;
        _methodsByFullName = index.MethodsByFullName;
        _propertiesByFullName = index.PropertiesByFullName;
    }

    public bool TryAdapt(
        object? context,
        System.Type targetType,
        out object? adapted)
    {
        adapted = null;
        if (context is null)
        {
            return !targetType.IsValueType || Nullable.GetUnderlyingType(nullableType: targetType) is not null;
        }

        if (targetType.IsInstanceOfType(o: context))
        {
            adapted = context;
            return true;
        }

        adapted = context switch
        {
            ProjectMetadata project => targetType.IsAssignableFrom(c: typeof(CodeFile)) ? CreateFile(project: project) : null,
            TypeMetadata type => AdaptType(type: type, targetType: targetType),
            PropertyMetadata property => CreateProperty(property: property, parent: null),
            MethodMetadata method => CreateMethod(method: method, parent: null),
            ParameterMetadata parameter => CreateParameter(parameter: parameter, parent: null),
            ConstantMetadata constant => CreateConstant(constant: constant, parent: null),
            FieldMetadata field => CreateField(field: field, parent: null),
            StaticReadOnlyFieldMetadata staticReadOnlyField => CreateStaticReadOnlyField(field: staticReadOnlyField, parent: null),
            EventMetadata @event => CreateEvent(@event: @event, parent: null),
            DelegateMetadata @delegate => CreateDelegate(@delegate: @delegate, parent: null),
            TypeMetadataReference typeReference => CreateType(type: typeReference),
            EnumValueMetadata enumValue => CreateEnumValue(enumValue: enumValue, parent: null),
            AttributeMetadata attribute => CreateAttribute(attribute: attribute, parent: null),
            AttributeArgumentMetadata argument => CreateAttributeArgument(argument: argument),
            TypeMappingContext typeContext => CreateType(type: typeContext.Reference, runtimeType: typeContext.RuntimeType),
            _ => null,
        };

        return adapted is not null && targetType.IsInstanceOfType(o: adapted);
    }

    public CodeFile CreateFile(ProjectMetadata project)
    {
        if (ReferenceEquals(objA: _cachedFileProject, objB: project) && _cachedFile is not null)
        {
            return _cachedFile;
        }

        _cachedFile = (CodeFile)AdaptProject(project: project, targetType: typeof(CodeFile))!;
        _cachedFileProject = project;
        return _cachedFile;
    }

    private static Typewriter.CodeModel.DocComment? CreateDocComment(
        DocCommentMetadata? docComment,
        Typewriter.CodeModel.Item? parent)
    {
        return docComment is null
            ? null
            : new Typewriter.CodeModel.DocComment
            {
                Name = docComment.Summary,
                Parent = parent,
                Summary = docComment.Summary,
                Returns = docComment.Returns,
                Parameters = new Typewriter.CodeModel.ParameterCommentCollection(
                    items: docComment.Parameters.Select(
                        selector: parameter => new Typewriter.CodeModel.ParameterComment
                        {
                            Name = parameter.Name,
                            Description = parameter.Description,
                            Parent = parent,
                        })),
            };
    }

    private static Typewriter.CodeModel.TypeParameterCollection CreateTypeParameters(
        IEnumerable<TypeParameterMetadata> typeParameters,
        Typewriter.CodeModel.Item? parent)
    {
        return new Typewriter.CodeModel.TypeParameterCollection(
            items: typeParameters.Select(
                selector: parameter => new Typewriter.CodeModel.TypeParameter
                {
                    Name = parameter.Name,
                    FullName = parameter.FullName,
                    Parent = parent,
                }));
    }

    private static bool IsTaskLike(string fullName)
    {
        return fullName.Equals(value: "System.Threading.Tasks.Task", comparisonType: StringComparison.Ordinal)
               || fullName.Equals(value: "System.Threading.Tasks.ValueTask", comparisonType: StringComparison.Ordinal);
    }

    private static bool HasAttribute(
        IEnumerable<AttributeMetadata> attributes,
        string name)
    {
        return attributes.Any(
            predicate: attribute => attribute.Name.Equals(value: name, comparisonType: StringComparison.Ordinal)
                                    || attribute.FullName.Equals(value: name, comparisonType: StringComparison.Ordinal)
                                    || attribute.Name.Equals(value: name + "Attribute", comparisonType: StringComparison.Ordinal)
                                    || attribute.FullName.EndsWith(value: "." + name + "Attribute", comparisonType: StringComparison.Ordinal));
    }

    private static string GetLegacyTypeName(string mappedName)
    {
        return mappedName
            .Replace(oldValue: " | null", newValue: string.Empty, comparisonType: StringComparison.Ordinal)
            .Replace(oldValue: "(", newValue: string.Empty, comparisonType: StringComparison.Ordinal)
            .Replace(oldValue: ")", newValue: string.Empty, comparisonType: StringComparison.Ordinal);
    }

    private static bool IsDynamic(TypeMetadataReference type)
    {
        return type.FullName.Equals(value: "dynamic", comparisonType: StringComparison.OrdinalIgnoreCase)
               || type.FullName.Equals(value: "System.Object", comparisonType: StringComparison.Ordinal);
    }

    private static string FormatGenericFullName(TypeMetadata type)
    {
        if (type.TypeParameters.Count == 0
            || type.FullName.Contains(value: '<', comparisonType: StringComparison.Ordinal))
        {
            return type.FullName;
        }

        return string.Concat(
            str0: type.FullName,
            str1: "<",
            str2: string.Join(separator: ", ", values: type.TypeParameters.Select(selector: parameter => parameter.Name)),
            str3: ">");
    }

    private static bool IsPrimitiveType(TypeMetadataReference type)
    {
        var effectiveType = type.IsCollection && type.ElementType is not null
            ? type.ElementType
            : type;
        return effectiveType.IsPrimitive
            || effectiveType.IsDateLike
            || TypeScriptTemporalTypes.IsDateTime(fullName: effectiveType.FullName)
            || TypeScriptTemporalTypes.IsDateOnly(fullName: effectiveType.FullName)
            || TypeScriptTemporalTypes.IsTimeOnly(fullName: effectiveType.FullName)
            || effectiveType.FullName.Equals(value: "System.Guid", comparisonType: StringComparison.Ordinal);
    }

    private static string GetDefaultValue(TypeMetadata type)
    {
        return type.Kind == TypeMetadataKind.Enum
            ? ResolveEnumDefault(enumName: type.Name, enumValues: type.EnumValues) ?? "0"
            : "null";
    }

    private static string? ResolveEnumDefault(
        string enumName,
        IReadOnlyList<EnumValueMetadata> enumValues)
    {
        return enumValues.Count == 0
            ? null
            : $"{enumName}.{enumValues[index: 0].Name}";
    }

    /// <summary>
    /// Projects a generic base type definition onto the arguments supplied at the inheritance site.
    /// </summary>
    /// <param name="definition">The open generic base type definition taken from the type index.</param>
    /// <param name="reference">The closed base type reference recorded on the derived type.</param>
    /// <returns>The definition with member types rewritten in terms of the supplied arguments.</returns>
    /// <remarks>
    /// The type index stores open definitions, so a base such as <c>EntityBaseWithStronglyTypedId&lt;TIdentity&gt;</c>
    /// describes its members in terms of <c>TIdentity</c>. When a derived type inherits from the closed
    /// <c>EntityBaseWithStronglyTypedId&lt;DeviceId&gt;</c>, walking the base class must report the substituted
    /// member types, otherwise inherited members leak the open type parameter name into generated output.
    /// </remarks>
    private static TypeMetadata SubstituteTypeArguments(
        TypeMetadata definition,
        TypeMetadataReference reference)
    {
        if (definition.TypeParameters.Count == 0
            || reference.TypeArguments.Count == 0)
        {
            return definition;
        }

        var substitutions = new Dictionary<string, TypeMetadataReference>(comparer: StringComparer.Ordinal);
        var count = Math.Min(val1: definition.TypeParameters.Count, val2: reference.TypeArguments.Count);
        for (var index = 0; index < count; index++)
        {
            var argument = reference.TypeArguments[index: index];
            var parameterName = definition.TypeParameters[index: index].Name;

            // A type parameter reference is named after the parameter, but its full name is
            // qualified by the declaring type because full names walk the containing type chain.
            // Both spellings are registered so member types match however they were captured.
            substitutions[key: parameterName] = argument;
            substitutions[key: $"{definition.FullName}.{parameterName}"] = argument;
        }

        return definition with
        {
            Properties = definition.Properties
                .Select(selector: property => property with { Type = SubstituteTypeArguments(type: property.Type, substitutions: substitutions) })
                .ToArray(),
            Fields = definition.Fields
                .Select(selector: field => field with { Type = SubstituteTypeArguments(type: field.Type, substitutions: substitutions) })
                .ToArray(),
            Methods = definition.Methods
                .Select(selector: method => SubstituteTypeArguments(method: method, substitutions: substitutions))
                .ToArray(),
        };
    }

    /// <summary>
    /// Rewrites a method's return and parameter types, replacing class-level type parameter
    /// occurrences with their arguments.
    /// </summary>
    /// <param name="method">The method to rewrite.</param>
    /// <param name="substitutions">The type parameter name to argument map.</param>
    /// <returns>The rewritten method, or the original when nothing was substituted.</returns>
    private static MethodMetadata SubstituteTypeArguments(
        MethodMetadata method,
        IReadOnlyDictionary<string, TypeMetadataReference> substitutions)
    {
        // A generic method's own type parameters shadow class-level parameters of the same
        // name, for example T in "T Clone<T>(T source)" on a class Container<T>: those
        // occurrences bind to the method and must not be rewritten to the class argument.
        var effectiveSubstitutions = method.TypeParameters.Count == 0
            ? substitutions
            : substitutions
                .Where(predicate: pair => !method.TypeParameters.Any(predicate: parameter =>
                    string.Equals(a: parameter.Name, b: pair.Key, comparisonType: StringComparison.Ordinal)))
                .ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => pair.Value, comparer: StringComparer.Ordinal);

        if (effectiveSubstitutions.Count == 0)
        {
            return method;
        }

        return method with
        {
            ReturnType = SubstituteTypeArguments(type: method.ReturnType, substitutions: effectiveSubstitutions),
            Parameters = method.Parameters
                .Select(selector: parameter => parameter with { Type = SubstituteTypeArguments(type: parameter.Type, substitutions: effectiveSubstitutions) })
                .ToArray(),
        };
    }

    /// <summary>
    /// Rewrites a type reference, replacing any type parameter occurrences with their arguments.
    /// </summary>
    /// <param name="type">The type reference to rewrite.</param>
    /// <param name="substitutions">The type parameter name to argument map.</param>
    /// <returns>The rewritten reference, or the original when nothing was substituted.</returns>
    private static TypeMetadataReference SubstituteTypeArguments(
        TypeMetadataReference type,
        IReadOnlyDictionary<string, TypeMetadataReference> substitutions)
    {
        // Only a reference that looks like a type parameter may match: it carries no type
        // arguments or element type of its own, and its simple name participates in the full
        // name. This keeps a real type that happens to share a parameter's name (for example a
        // global-namespace class named TIdentity) from being rewritten by the bare-name key.
        var looksLikeTypeParameter = type.TypeArguments.Count == 0
            && type.ElementType is null
            && type.FullName.EndsWith(value: type.Name, comparisonType: StringComparison.Ordinal);
        if (looksLikeTypeParameter
            && substitutions.TryGetValue(key: type.FullName, value: out var substituted))
        {
            // Preserve nullability declared at the use site, for example TIdentity? stays optional.
            return type.IsNullable
                ? substituted with { IsNullable = true }
                : substituted;
        }

        var elementType = type.ElementType is null
            ? null
            : SubstituteTypeArguments(type: type.ElementType, substitutions: substitutions);
        var typeArguments = type.TypeArguments
            .Select(selector: argument => SubstituteTypeArguments(type: argument, substitutions: substitutions))
            .ToArray();

        if (ReferenceEquals(objA: elementType, objB: type.ElementType)
            && typeArguments.SequenceEqual(second: type.TypeArguments))
        {
            return type;
        }

        return type with
        {
            ElementType = elementType,
            TypeArguments = typeArguments,
        };
    }

    private object? AdaptProject(
        ProjectMetadata project,
        System.Type targetType)
    {
        if (!targetType.IsAssignableFrom(c: typeof(CodeFile)))
        {
            return null;
        }

        var filePath = project.SourceFiles.Count == 1
            ? project.SourceFiles[index: 0].Path
            : project.ProjectPath;

        // The collections are built lazily. Template inspection only reads Settings off the
        // compiled host, so adapting every type in the merged metadata up front is pure waste
        // on that path; per-template inspection dominated save latency because of it.
        return new LazyCodeFile(factory: this, project: project)
        {
            Name = Path.GetFileNameWithoutExtension(path: filePath),
            FileName = Path.GetFileName(path: filePath),
            FileNameWithoutExtension = Path.GetFileNameWithoutExtension(path: filePath),
            FullName = filePath,
            Path = filePath,
        };
    }

    private object? AdaptType(
        TypeMetadata type,
        System.Type targetType)
    {
        if (type.Kind == TypeMetadataKind.Class && targetType.IsAssignableFrom(c: typeof(CodeClass)))
        {
            return CreateClass(type: type);
        }

        if (type.Kind == TypeMetadataKind.Record && targetType.IsAssignableFrom(c: typeof(CodeRecord)))
        {
            return CreateRecord(type: type);
        }

        if (type.Kind == TypeMetadataKind.Struct && targetType.IsAssignableFrom(c: typeof(CodeStruct)))
        {
            return CreateStruct(type: type);
        }

        if (type.Kind == TypeMetadataKind.Interface && targetType.IsAssignableFrom(c: typeof(CodeInterface)))
        {
            return CreateInterface(type: type);
        }

        if (type.Kind == TypeMetadataKind.Enum && targetType.IsAssignableFrom(c: typeof(CodeEnum)))
        {
            return CreateEnum(type: type);
        }

        return targetType.IsAssignableFrom(c: typeof(CodeType))
            ? CreateType(type: type)
            : null;
    }

    private CodeClass CreateClass(TypeMetadata type)
    {
        // Hot on the warm render path. This method eagerly materialises the type's entire member
        // graph, and before memoisation it was being re-run for the same type dozens of times per
        // render (see the cache field comments for the measured 7x warm-path impact, and for why
        // that did not translate into an end-to-end win). Keep the cache.
        if (_classByMetadata.TryGetValue(key: type, value: out var cached))
        {
            return cached;
        }

        var created = CreateClass(type: type, reference: null);
        _classByMetadata[type] = created;
        return created;
    }

    // Deliberately NOT cached. This overload projects the class through a specific reference, so the
    // resulting Type differs per reference even for the same declaration. Sharing one instance
    // across references would silently reintroduce the declaration-vs-reference bug class that
    // DeclarationVersusReferenceTests and DeclaredTypeMemberParityTests exist to prevent.
    // Correctness wins over the marginal gain here.
    private CodeClass CreateClass(
        TypeMetadata type,
        TypeMetadataReference? reference)
    {
        var classType = reference is null ? CreateType(type: type) : CreateType(type: reference);
        return new CodeClass
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            BaseClass = CreateBaseClass(type: type),
            ContainingClass = CreateContainingClass(type: type),
            DocComment = CreateDocComment(docComment: type.DocComment, parent: null),
            Interfaces = CreateInterfaces(type: type),
            IsAbstract = type.IsAbstract,
            IsGeneric = classType.IsGeneric,
            IsStatic = type.IsStatic,
            Constants = new Typewriter.CodeModel.ConstantCollection(items: type.Constants.Select(selector: constant => CreateConstant(constant: constant, parent: null))),
            Delegates = new Typewriter.CodeModel.DelegateCollection(items: type.Delegates.Select(selector: @delegate => CreateDelegate(@delegate: @delegate, parent: null))),
            Events = new Typewriter.CodeModel.EventCollection(items: type.Events.Select(selector: @event => CreateEvent(@event: @event, parent: null))),
            Fields = new Typewriter.CodeModel.FieldCollection(items: type.Fields.Select(selector: field => CreateField(field: field, parent: null))),
            Methods = new Typewriter.CodeModel.MethodCollection(items: type.Methods.Select(selector: method => CreateMethod(method: method, parent: null))),
            NestedClasses = new Typewriter.CodeModel.ClassCollection(items: type.NestedClasses.Select(selector: CreateClass)),
            NestedEnums = new Typewriter.CodeModel.EnumCollection(items: type.NestedEnums.Select(selector: CreateEnum)),
            NestedInterfaces = new Typewriter.CodeModel.InterfaceCollection(items: type.NestedInterfaces.Select(selector: CreateInterface)),
            NestedRecords = new Typewriter.CodeModel.RecordCollection(items: type.NestedRecords.Select(selector: CreateRecord)),
            NestedStructs = new Typewriter.CodeModel.StructCollection(items: type.NestedStructs.Select(selector: CreateStruct)),
            Properties = new Typewriter.CodeModel.PropertyCollection(items: type.Properties.Select(selector: property => CreateProperty(property: property, parent: null))),
            StaticReadOnlyFields = new Typewriter.CodeModel.StaticReadOnlyFieldCollection(
                items: type.StaticReadOnlyFields.Select(selector: field => CreateStaticReadOnlyField(field: field, parent: null))),
            Type = classType,
            TypeArguments = classType.TypeArguments,
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeRecord CreateRecord(TypeMetadata type)
    {
        return CreateRecord(type: type, reference: null);
    }

    private CodeRecord CreateRecord(
        TypeMetadata type,
        TypeMetadataReference? reference)
    {
        var recordType = reference is null ? CreateType(type: type) : CreateType(type: reference);
        return new CodeRecord
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            BaseRecord = CreateBaseRecord(type: type),
            ContainingRecord = CreateContainingRecord(type: type),
            DocComment = CreateDocComment(docComment: type.DocComment, parent: null),
            Interfaces = CreateInterfaces(type: type),
            IsAbstract = type.IsAbstract,
            IsGeneric = recordType.IsGeneric,
            Constants = new Typewriter.CodeModel.ConstantCollection(items: type.Constants.Select(selector: constant => CreateConstant(constant: constant, parent: null))),
            Delegates = new Typewriter.CodeModel.DelegateCollection(items: type.Delegates.Select(selector: @delegate => CreateDelegate(@delegate: @delegate, parent: null))),
            Events = new Typewriter.CodeModel.EventCollection(items: type.Events.Select(selector: @event => CreateEvent(@event: @event, parent: null))),
            Fields = new Typewriter.CodeModel.FieldCollection(items: type.Fields.Select(selector: field => CreateField(field: field, parent: null))),
            Methods = new Typewriter.CodeModel.MethodCollection(items: type.Methods.Select(selector: method => CreateMethod(method: method, parent: null))),
            NestedClasses = new Typewriter.CodeModel.ClassCollection(items: type.NestedClasses.Select(selector: CreateClass)),
            NestedEnums = new Typewriter.CodeModel.EnumCollection(items: type.NestedEnums.Select(selector: CreateEnum)),
            NestedInterfaces = new Typewriter.CodeModel.InterfaceCollection(items: type.NestedInterfaces.Select(selector: CreateInterface)),
            NestedRecords = new Typewriter.CodeModel.RecordCollection(items: type.NestedRecords.Select(selector: CreateRecord)),
            NestedStructs = new Typewriter.CodeModel.StructCollection(items: type.NestedStructs.Select(selector: CreateStruct)),
            Properties = new Typewriter.CodeModel.PropertyCollection(items: type.Properties.Select(selector: property => CreateProperty(property: property, parent: null))),
            StaticReadOnlyFields = new Typewriter.CodeModel.StaticReadOnlyFieldCollection(
                items: type.StaticReadOnlyFields.Select(selector: field => CreateStaticReadOnlyField(field: field, parent: null))),
            Type = recordType,
            TypeArguments = recordType.TypeArguments,
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeInterface CreateInterface(TypeMetadata type)
    {
        return CreateInterface(type: type, reference: null);
    }

    private CodeInterface CreateInterface(
        TypeMetadata type,
        TypeMetadataReference? reference)
    {
        var interfaceType = reference is null ? CreateType(type: type) : CreateType(type: reference);
        return new CodeInterface
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            ContainingClass = CreateContainingClass(type: type),
            DocComment = CreateDocComment(docComment: type.DocComment, parent: null),
            Events = new Typewriter.CodeModel.EventCollection(items: type.Events.Select(selector: @event => CreateEvent(@event: @event, parent: null))),
            IsGeneric = interfaceType.IsGeneric,
            Interfaces = CreateInterfaces(type: type),
            Methods = new Typewriter.CodeModel.MethodCollection(items: type.Methods.Select(selector: method => CreateMethod(method: method, parent: null))),
            NestedStructs = new Typewriter.CodeModel.StructCollection(items: type.NestedStructs.Select(selector: CreateStruct)),
            Properties = new Typewriter.CodeModel.PropertyCollection(items: type.Properties.Select(selector: property => CreateProperty(property: property, parent: null))),
            Type = interfaceType,
            TypeArguments = interfaceType.TypeArguments,
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeStruct CreateStruct(TypeMetadata type)
    {
        return CreateStruct(type: type, reference: null);
    }

    private CodeStruct CreateStruct(
        TypeMetadata type,
        TypeMetadataReference? reference)
    {
        var structType = reference is null ? CreateType(type: type) : CreateType(type: reference);
        return new CodeStruct
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            ContainingClass = CreateContainingClass(type: type),
            ContainingStruct = CreateContainingStruct(type: type),
            DocComment = CreateDocComment(docComment: type.DocComment, parent: null),
            Interfaces = CreateInterfaces(type: type),
            IsGeneric = structType.IsGeneric,
            IsStatic = type.IsStatic,
            Constants = new Typewriter.CodeModel.ConstantCollection(items: type.Constants.Select(selector: constant => CreateConstant(constant: constant, parent: null))),
            Delegates = new Typewriter.CodeModel.DelegateCollection(items: type.Delegates.Select(selector: @delegate => CreateDelegate(@delegate: @delegate, parent: null))),
            Events = new Typewriter.CodeModel.EventCollection(items: type.Events.Select(selector: @event => CreateEvent(@event: @event, parent: null))),
            Fields = new Typewriter.CodeModel.FieldCollection(items: type.Fields.Select(selector: field => CreateField(field: field, parent: null))),
            Methods = new Typewriter.CodeModel.MethodCollection(items: type.Methods.Select(selector: method => CreateMethod(method: method, parent: null))),
            NestedClasses = new Typewriter.CodeModel.ClassCollection(items: type.NestedClasses.Select(selector: CreateClass)),
            NestedEnums = new Typewriter.CodeModel.EnumCollection(items: type.NestedEnums.Select(selector: CreateEnum)),
            NestedInterfaces = new Typewriter.CodeModel.InterfaceCollection(items: type.NestedInterfaces.Select(selector: CreateInterface)),
            NestedRecords = new Typewriter.CodeModel.RecordCollection(items: type.NestedRecords.Select(selector: CreateRecord)),
            NestedStructs = new Typewriter.CodeModel.StructCollection(items: type.NestedStructs.Select(selector: CreateStruct)),
            Properties = new Typewriter.CodeModel.PropertyCollection(items: type.Properties.Select(selector: property => CreateProperty(property: property, parent: null))),
            StaticReadOnlyFields = new Typewriter.CodeModel.StaticReadOnlyFieldCollection(
                items: type.StaticReadOnlyFields.Select(selector: field => CreateStaticReadOnlyField(field: field, parent: null))),
            Type = structType,
            TypeArguments = structType.TypeArguments,
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeEnum CreateEnum(TypeMetadata type)
    {
        var enumModel = new CodeEnum
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            ContainingClass = CreateContainingClass(type: type),
            DocComment = CreateDocComment(docComment: type.DocComment, parent: null),
            IsFlags = HasAttribute(attributes: type.Attributes, name: "Flags"),
            Type = CreateType(type: type),
            Values = new Typewriter.CodeModel.EnumValueCollection(items: type.EnumValues.Select(selector: value => CreateEnumValue(enumValue: value, parent: null))),
        };
        return enumModel;
    }

    // Builds the one set of declared-member factories that BOTH the declaration path
    // (CreateType(TypeMetadata)) and the reference path (CreateType(TypeMetadataReference)) consume.
    //
    // This method is the fix for the declaration-vs-reference split bug. Previously each path had
    // its own hand-written list of member assignments; the reference path's list was shorter, so
    // declared members were silently empty when reached through a reference. Because the lists were
    // maintained independently, the bug regressed every time a member was added to one path only.
    // Routing both paths through here makes that class of omission structurally impossible.
    //
    // See DeclaredTypeMembers for why these are delegates rather than materialised values (short
    // version: eager construction on the reference path recurses until the stack overflows).
    private DeclaredTypeMembers CreateDeclaredTypeMembers(
        TypeMetadata type,
        Func<Typewriter.CodeModel.IAttributeCollection> attributes)
    {
        return new DeclaredTypeMembers
        {
            Attributes = attributes,
            BaseClass = () => CreateBaseClass(type: type),
            ContainingClass = () => CreateContainingClass(type: type),
            DocComment = () => CreateDocComment(docComment: type.DocComment, parent: null),
            Interfaces = () => CreateInterfaces(type: type),
            Constants = () => new Typewriter.CodeModel.ConstantCollection(
                items: type.Constants.Select(selector: constant => CreateConstant(constant: constant, parent: null))),
            Delegates = () => new Typewriter.CodeModel.DelegateCollection(
                items: type.Delegates.Select(selector: @delegate => CreateDelegate(@delegate: @delegate, parent: null))),
            Fields = () => new Typewriter.CodeModel.FieldCollection(
                items: type.Fields.Select(selector: field => CreateField(field: field, parent: null))),
            Methods = () => new Typewriter.CodeModel.MethodCollection(
                items: type.Methods.Select(selector: method => CreateMethod(method: method, parent: null))),
            Properties = () => new Typewriter.CodeModel.PropertyCollection(
                items: type.Properties.Select(selector: property => CreateProperty(property: property, parent: null))),
            StaticReadOnlyFields = () => new Typewriter.CodeModel.StaticReadOnlyFieldCollection(
                items: type.StaticReadOnlyFields.Select(selector: field => CreateStaticReadOnlyField(field: field, parent: null))),
            NestedClasses = () => new Typewriter.CodeModel.ClassCollection(items: type.NestedClasses.Select(selector: CreateClass)),
            NestedEnums = () => new Typewriter.CodeModel.EnumCollection(items: type.NestedEnums.Select(selector: CreateEnum)),
            NestedInterfaces = () => new Typewriter.CodeModel.InterfaceCollection(items: type.NestedInterfaces.Select(selector: CreateInterface)),
            NestedRecords = () => new Typewriter.CodeModel.RecordCollection(items: type.NestedRecords.Select(selector: CreateRecord)),
            NestedStructs = () => new Typewriter.CodeModel.StructCollection(items: type.NestedStructs.Select(selector: CreateStruct)),
            TypeParameters = () => CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeType CreateType(TypeMetadata type)
    {
        // Measured as a wash on the profiled workload, unlike the reference overload below which is
        // a major win. Retained as cheap insurance against templates with different access patterns.
        if (_typeByMetadata.TryGetValue(key: type, value: out var cached))
        {
            return cached;
        }

        var created = CreateTypeCore(type: type);

        // Populated after construction for the same reason as the reference overload: the member
        // factories below reach back through ContainingClass and nested types, so an entry published
        // early could be observed half-built.
        _typeByMetadata[type] = created;
        return created;
    }

    private CodeType CreateType(
        TypeMetadataReference type,
        FrontendRuntimeTypeKind runtimeType = FrontendRuntimeTypeKind.Auto)
    {
        // Hottest path in warm template rendering: this was 41% of render CPU before memoisation,
        // the single largest contributor in the profile. Do not remove this cache without
        // re-running the benchmark -- see the cache field comments for the measured numbers and
        // for why the benchmark overstates the end-to-end benefit.
        var cacheKey = (Reference: type, RuntimeType: runtimeType);
        if (_typeByReference.TryGetValue(key: cacheKey, value: out var cached))
        {
            return cached;
        }

        var created = CreateTypeCore(type: type, runtimeType: runtimeType);

        // Assigned after construction rather than before: CreateTypeCore recurses through element
        // types and type arguments, and a self-referential type would otherwise observe a partially
        // built entry. Recursion here terminates on structure (arguments are strictly smaller), so
        // the worst case is that a nested type is built twice before the entry lands.
        _typeByReference[cacheKey] = created;
        return created;
    }

    private CodeType CreateTypeCore(TypeMetadata type)
    {
        // The declaration path invokes the shared factories immediately. Unlike the reference path
        // there is no construction cycle to break here, so there is nothing to defer.
        var members = CreateDeclaredTypeMembers(
            type: type,
            attributes: () => CreateAttributes(attributes: type.Attributes, parent: null));

        return new CodeType
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            FileLocations = type.FileLocations,
            IsDefined = true,
            IsEnum = type.Kind == TypeMetadataKind.Enum,
            IsGeneric = type.TypeParameters.Count > 0 || type.TypeArguments.Count > 0,
            IsStruct = type.Kind == TypeMetadataKind.Struct,
            TypeArguments = new Typewriter.CodeModel.TypeCollection(items: type.TypeArguments.Select(selector: argument => CreateType(type: argument))),
            DefaultValue = GetDefaultValue(type: type),
            Settings = _settings,

            // Declared members, from the shared set.
            Attributes = members.Attributes(),
            BaseClass = members.BaseClass(),
            ContainingClass = members.ContainingClass(),
            DocComment = members.DocComment(),
            Interfaces = members.Interfaces(),
            Constants = members.Constants(),
            Delegates = members.Delegates(),
            Fields = members.Fields(),
            Methods = members.Methods(),
            NestedClasses = members.NestedClasses(),
            NestedEnums = members.NestedEnums(),
            NestedInterfaces = members.NestedInterfaces(),
            NestedRecords = members.NestedRecords(),
            NestedStructs = members.NestedStructs(),
            Properties = members.Properties(),
            StaticReadOnlyFields = members.StaticReadOnlyFields(),
            TypeParameters = members.TypeParameters(),
        };
    }

    private CodeType CreateTypeCore(
        TypeMetadataReference type,
        FrontendRuntimeTypeKind runtimeType)
    {
        var typeArguments = new Typewriter.CodeModel.TypeCollection(
            items: type.TypeArguments.Select(selector: argument => CreateType(type: argument, runtimeType: runtimeType)));
        _typesByFullName.TryGetValue(key: type.FullName, value: out var typeMetadata);
        var mappedName = _typeMapper.Map(
            type: type,
            strictNull: _settings.StrictNullGeneration,
            dateMapping: _settings.GetDateMapping(),
            decimalType: _settings.DecimalTypeGeneration,
            guidType: _settings.GuidTypeGeneration,
            runtimeType: runtimeType);
        var isStruct = typeMetadata?.Kind == TypeMetadataKind.Struct;

        // When the referenced type's declaration is part of the compilation we hand MappedCodeType
        // the shared declared-member factories, restoring the v3 surface so templates can reach
        // through a reference to declared members. Types outside the compilation (BCL, third-party
        // assemblies) have no declaration available, so `members` stays null and MappedCodeType
        // keeps its inherited empty defaults -- which is the correct answer for them, not a bug.
        //
        // These are deliberately passed unevaluated. Invoking them here would recurse without end;
        // see DeclaredTypeMembers for the full explanation.
        var members = typeMetadata is null
            ? null
            : CreateDeclaredTypeMembers(
                type: typeMetadata,
                attributes: () => CreateTypeReferenceAttributes(attributes: typeMetadata.Attributes));

        return new MappedCodeType
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = GetLegacyTypeName(mappedName: mappedName),
            FullName = type.FullName,
            Namespace = type.Namespace,
            Attributes = members is null ? new Typewriter.CodeModel.AttributeCollection() : members.Attributes(),
            ElementType = type.ElementType is null ? null : CreateType(type: type.ElementType, runtimeType: runtimeType),
            IsDate = TypeScriptTemporalTypes.IsLegacyDate(isDateLike: type.IsDateLike, fullName: type.FullName),
            IsDictionary = type.IsDictionary,
            IsDynamic = IsDynamic(type: type),
            IsEnum = type.IsEnum,
            IsEnumerable = type.IsCollection,
            IsGuid = type.FullName.Equals(value: "System.Guid", comparisonType: StringComparison.Ordinal),
            IsGeneric = typeArguments.Count > 0,
            IsNullable = type.IsNullable,
            IsPrimitive = IsPrimitiveType(type: type),
            IsStruct = isStruct,
            IsTask = type.IsTask || IsTaskLike(fullName: type.FullName),
            IsTimeSpan = TypeScriptTemporalTypes.IsDuration(fullName: type.FullName),
            IsValueTuple = type.IsValueTuple,
            OriginalName = CSharpTypeNameFormatter.GetOriginalName(type: type),
            TupleElements = new Typewriter.CodeModel.FieldCollection(items: type.TupleElements.Select(selector: field => CreateField(field: field, parent: null))),
            TypeArguments = typeArguments,
            DefaultValue = GetDefaultValue(type: type, runtimeType: runtimeType),
            Settings = _settings,
            UseResolvedDefault = _settings.DateLibraryGeneration != Typewriter.Configuration.DateLibrary.Legacy
                                 || runtimeType != FrontendRuntimeTypeKind.Auto,
            DeclaredMembers = members,
            FileLocations = typeMetadata?.FileLocations ?? [],
            IsDefined = typeMetadata is not null,
        };
    }

    private Typewriter.CodeModel.Property CreateProperty(
        PropertyMetadata property,
        Typewriter.CodeModel.Item? parent)
    {
        return CreateProperty(property: property, parent: parent, includeParameters: true);
    }

    private Typewriter.CodeModel.Property CreateProperty(
        PropertyMetadata property,
        Typewriter.CodeModel.Item? parent,
        bool includeParameters)
    {
        return new Typewriter.CodeModel.Property
        {
            AssemblyName = ResolveAssemblyName(assemblyName: property.AssemblyName),
            Name = property.Name,
            FullName = property.FullName,
            Parent = parent ?? CreateParentTypeItem(fullName: property.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: property.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: property.DocComment, parent: null),
            HasGetter = property.HasGetter,
            HasSetter = property.HasSetter,
            IsAbstract = property.IsAbstract,
            IsIndexer = property.IsIndexer,
            IsRequired = property.IsRequired,
            IsVirtual = property.IsVirtual,
            Parameters = includeParameters
                ? new Typewriter.CodeModel.ParameterCollection(items: property.Parameters.Select(selector: parameter => CreateParameter(parameter: parameter, parent: null)))
                : new Typewriter.CodeModel.ParameterCollection(),
            Type = CreateType(type: property.Type, runtimeType: FrontendRuntimeTypeResolver.Resolve(attributes: property.Attributes)),
            Value = property.Value ?? string.Empty,
        };
    }

    private CodeMethod CreateMethod(
        MethodMetadata method,
        Typewriter.CodeModel.Item? parent)
    {
        return CreateMethod(method: method, parent: parent, includeParameters: true);
    }

    private CodeMethod CreateMethod(
        MethodMetadata method,
        Typewriter.CodeModel.Item? parent,
        bool includeParameters)
    {
        return new CodeMethod
        {
            AssemblyName = ResolveAssemblyName(assemblyName: method.AssemblyName),
            Name = method.Name,
            FullName = method.FullName,
            Parent = parent ?? CreateParentTypeItem(fullName: method.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: method.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: method.DocComment, parent: null),
            IsAbstract = method.IsAbstract,
            IsGeneric = method.IsGeneric,
            Parameters = includeParameters
                ? new Typewriter.CodeModel.ParameterCollection(items: method.Parameters.Select(selector: parameter => CreateParameter(parameter: parameter, parent: null)))
                : new Typewriter.CodeModel.ParameterCollection(),
            Type = CreateType(type: method.ReturnType),
            TypeParameters = CreateTypeParameters(typeParameters: method.TypeParameters, parent: null),
        };
    }

    private CodeParameter CreateParameter(
        ParameterMetadata parameter,
        Typewriter.CodeModel.Item? parent)
    {
        return new CodeParameter
        {
            AssemblyName = ResolveAssemblyName(assemblyName: parameter.AssemblyName),
            Name = parameter.Name,
            FullName = parameter.FullName,
            Parent = parent
                ?? (Typewriter.CodeModel.Item?)CreateParentMethod(fullName: parameter.ParentMethodFullName)
                ?? CreateParentProperty(fullName: parameter.ParentPropertyFullName),
            Attributes = CreateAttributes(attributes: parameter.Attributes, parent: null),
            DefaultValue = parameter.DefaultValue ?? string.Empty,
            HasDefaultValue = parameter.HasDefaultValue,
            Type = CreateType(type: parameter.Type, runtimeType: FrontendRuntimeTypeResolver.Resolve(attributes: parameter.Attributes)),
        };
    }

    private CodeConstant CreateConstant(
        ConstantMetadata constant,
        Typewriter.CodeModel.Item? parent)
    {
        return new CodeConstant
        {
            AssemblyName = ResolveAssemblyName(assemblyName: constant.AssemblyName),
            Name = constant.Name,
            FullName = constant.FullName,
            Parent = parent ?? CreateParentTypeItem(fullName: constant.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: constant.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: constant.DocComment, parent: null),
            Type = CreateType(type: constant.Type),
            Value = constant.Value ?? string.Empty,
        };
    }

    private CodeField CreateField(
        FieldMetadata field,
        Typewriter.CodeModel.Item? parent)
    {
        return new CodeField
        {
            AssemblyName = ResolveAssemblyName(assemblyName: field.AssemblyName),
            Name = field.Name,
            FullName = field.FullName,
            Parent = parent ?? CreateParentTypeItem(fullName: field.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: field.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: field.DocComment, parent: null),
            Type = CreateType(type: field.Type, runtimeType: FrontendRuntimeTypeResolver.Resolve(attributes: field.Attributes)),
            Value = field.Value ?? string.Empty,
        };
    }

    private CodeStaticReadOnlyField CreateStaticReadOnlyField(
        StaticReadOnlyFieldMetadata field,
        Typewriter.CodeModel.Item? parent)
    {
        return new CodeStaticReadOnlyField
        {
            AssemblyName = ResolveAssemblyName(assemblyName: field.AssemblyName),
            Name = field.Name,
            FullName = field.FullName,
            Parent = parent ?? CreateParentTypeItem(fullName: field.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: field.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: field.DocComment, parent: null),
            Type = CreateType(type: field.Type),
            Value = field.Value ?? string.Empty,
        };
    }

    private CodeEvent CreateEvent(
        EventMetadata @event,
        Typewriter.CodeModel.Item? parent)
    {
        return new CodeEvent
        {
            AssemblyName = ResolveAssemblyName(assemblyName: @event.AssemblyName),
            Name = @event.Name,
            FullName = @event.FullName,
            Parent = parent ?? CreateParentTypeItem(fullName: @event.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: @event.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: @event.DocComment, parent: null),
            Type = CreateType(type: @event.Type),
        };
    }

    private CodeDelegate CreateDelegate(
        DelegateMetadata @delegate,
        Typewriter.CodeModel.Item? parent)
    {
        return new CodeDelegate
        {
            AssemblyName = ResolveAssemblyName(assemblyName: @delegate.AssemblyName),
            Name = @delegate.Name,
            FullName = @delegate.FullName,
            Parent = parent ?? CreateParentTypeItem(fullName: @delegate.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: @delegate.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: @delegate.DocComment, parent: null),
            IsGeneric = @delegate.IsGeneric,
            Parameters = new Typewriter.CodeModel.ParameterCollection(items: @delegate.Parameters.Select(selector: parameter => CreateParameter(parameter: parameter, parent: null))),
            Type = CreateType(type: @delegate.ReturnType),
            TypeParameters = CreateTypeParameters(typeParameters: @delegate.TypeParameters, parent: null),
        };
    }

    private CodeAttribute CreateAttribute(
        AttributeMetadata attribute,
        Typewriter.CodeModel.Item? parent)
    {
        return new CodeAttribute
        {
            AssemblyName = ResolveAssemblyName(assemblyName: attribute.AssemblyName),
            Name = attribute.Name,
            FullName = attribute.FullName,
            Parent = parent,
            Arguments = new Typewriter.CodeModel.AttributeArgumentCollection(items: attribute.Arguments.Select(selector: CreateAttributeArgument)),
            Type = attribute.Type is null ? null : CreateType(type: attribute.Type),
            Value = TemplateAttributeValueFormatter.Format(attribute: attribute),
        };
    }

    private Typewriter.CodeModel.AttributeArgument CreateAttributeArgument(AttributeArgumentMetadata argument)
    {
        return new Typewriter.CodeModel.AttributeArgument
        {
            AssemblyName = ResolveAssemblyName(assemblyName: argument.AssemblyName),
            Name = argument.Name ?? string.Empty,
            Type = argument.Type is null ? null : CreateType(type: argument.Type),
            TypeValue = argument.TypeValue is null ? null : CreateType(type: argument.TypeValue),
            Value = argument.Value ?? string.Empty,
        };
    }

    private Typewriter.CodeModel.AttributeCollection CreateTypeReferenceAttributes(
        IEnumerable<AttributeMetadata> attributes)
    {
        return new Typewriter.CodeModel.AttributeCollection(
            items: attributes.Select(
                selector: attribute => new CodeAttribute
                {
                    AssemblyName = ResolveAssemblyName(assemblyName: attribute.AssemblyName),
                    Name = attribute.Name,
                    FullName = attribute.FullName,
                    Arguments = new Typewriter.CodeModel.AttributeArgumentCollection(
                        items: attribute.Arguments.Select(
                            selector: argument => new Typewriter.CodeModel.AttributeArgument
                            {
                                AssemblyName = ResolveAssemblyName(assemblyName: argument.AssemblyName),
                                Name = argument.Name ?? string.Empty,
                                Value = argument.Value ?? string.Empty,
                            })),
                    Value = TemplateAttributeValueFormatter.Format(attribute: attribute),
                }));
    }

    private Typewriter.CodeModel.EnumValue CreateEnumValue(
        EnumValueMetadata enumValue,
        CodeEnum? parent)
    {
        return new Typewriter.CodeModel.EnumValue
        {
            AssemblyName = ResolveAssemblyName(assemblyName: enumValue.AssemblyName),
            Name = enumValue.Name,
            FullName = string.IsNullOrWhiteSpace(value: enumValue.ParentTypeFullName)
                ? enumValue.Name
                : string.Concat(str0: enumValue.ParentTypeFullName, str1: ".", str2: enumValue.Name),
            Parent = parent ?? CreateParentEnum(fullName: enumValue.ParentTypeFullName),
            Attributes = CreateAttributes(attributes: enumValue.Attributes, parent: null),
            DocComment = CreateDocComment(docComment: enumValue.DocComment, parent: null),
            Value = enumValue.Value ?? 0,
        };
    }

    private Typewriter.CodeModel.InterfaceCollection CreateInterfaces(TypeMetadata type)
    {
        return new Typewriter.CodeModel.InterfaceCollection(
            items: type.BaseTypes
                .Where(predicate: IsInterfaceReference)
                .Select(selector: CreateInterfaceReference));
    }

    private bool IsInterfaceReference(TypeMetadataReference reference)
    {
        if (_typesByFullName.TryGetValue(key: reference.FullName, value: out var metadata))
        {
            return metadata.Kind == TypeMetadataKind.Interface;
        }

        // Heuristic: names like "ISomething" (I + uppercase) are likely interfaces.
        return reference.Name.Length > 1
            && reference.Name[index: 0] == 'I'
            && char.IsUpper(c: reference.Name[index: 1]);
    }

    private CodeInterface CreateInterfaceReference(TypeMetadataReference reference)
    {
        if (_typesByFullName.TryGetValue(key: reference.FullName, value: out var metadata)
            && metadata.Kind == TypeMetadataKind.Interface)
        {
            return CreateInterface(type: metadata, reference: reference);
        }

        var interfaceType = CreateType(type: reference);
        return new CodeInterface
        {
            AssemblyName = ResolveAssemblyName(assemblyName: reference.AssemblyName),
            Name = reference.Name,
            FullName = reference.FullName,
            Namespace = reference.Namespace,
            IsGeneric = interfaceType.IsGeneric,
            Type = interfaceType,
            TypeArguments = interfaceType.TypeArguments,
        };
    }

    private Typewriter.CodeModel.AttributeCollection CreateAttributes(
        IEnumerable<AttributeMetadata> attributes,
        Typewriter.CodeModel.Item? parent)
    {
        return new Typewriter.CodeModel.AttributeCollection(items: attributes.Select(selector: attribute => CreateAttribute(attribute: attribute, parent: parent)));
    }

    private Typewriter.CodeModel.Item? CreateParentTypeItem(string fullName)
    {
        if (string.IsNullOrWhiteSpace(value: fullName)
            || !_typesByFullName.TryGetValue(key: fullName, value: out var type))
        {
            return null;
        }

        return type.Kind switch
        {
            TypeMetadataKind.Class => CreateShallowClass(type: type),
            TypeMetadataKind.Record => CreateShallowRecord(type: type),
            TypeMetadataKind.Struct => CreateShallowStruct(type: type),
            TypeMetadataKind.Interface => CreateShallowInterface(type: type),
            TypeMetadataKind.Enum => CreateShallowEnum(type: type),
            _ => null,
        };
    }

    private CodeMethod? CreateParentMethod(string fullName)
    {
        return !string.IsNullOrWhiteSpace(value: fullName)
            && _methodsByFullName.TryGetValue(key: fullName, value: out var method)
                ? CreateMethod(method: method, parent: CreateParentTypeItem(fullName: method.ParentTypeFullName), includeParameters: false)
                : null;
    }

    private Typewriter.CodeModel.Property? CreateParentProperty(string fullName)
    {
        return !string.IsNullOrWhiteSpace(value: fullName)
            && _propertiesByFullName.TryGetValue(key: fullName, value: out var property)
                ? CreateProperty(property: property, parent: CreateParentTypeItem(fullName: property.ParentTypeFullName), includeParameters: false)
                : null;
    }

    private CodeEnum? CreateParentEnum(string fullName)
    {
        return !string.IsNullOrWhiteSpace(value: fullName)
            && _typesByFullName.TryGetValue(key: fullName, value: out var type)
            && type.Kind == TypeMetadataKind.Enum
                ? CreateShallowEnum(type: type)
                : null;
    }

    private CodeClass? CreateContainingClass(TypeMetadata type)
    {
        return !string.IsNullOrWhiteSpace(value: type.ContainingTypeFullName)
            && _typesByFullName.TryGetValue(key: type.ContainingTypeFullName, value: out var containingType)
            && containingType.Kind == TypeMetadataKind.Class
                ? CreateShallowClass(type: containingType)
                : null;
    }

    private CodeRecord? CreateContainingRecord(TypeMetadata type)
    {
        return !string.IsNullOrWhiteSpace(value: type.ContainingTypeFullName)
            && _typesByFullName.TryGetValue(key: type.ContainingTypeFullName, value: out var containingType)
            && containingType.Kind == TypeMetadataKind.Record
                ? CreateShallowRecord(type: containingType)
                : null;
    }

    private CodeStruct? CreateContainingStruct(TypeMetadata type)
    {
        return !string.IsNullOrWhiteSpace(value: type.ContainingTypeFullName)
            && _typesByFullName.TryGetValue(key: type.ContainingTypeFullName, value: out var containingType)
            && containingType.Kind == TypeMetadataKind.Struct
                ? CreateShallowStruct(type: containingType)
                : null;
    }

    private CodeClass CreateShallowClass(TypeMetadata type)
    {
        return new CodeClass
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            IsAbstract = type.IsAbstract,
            IsGeneric = type.TypeParameters.Count > 0 || type.TypeArguments.Count > 0,
            IsStatic = type.IsStatic,
            Type = CreateTypeShell(type: type),
            TypeArguments = new Typewriter.CodeModel.TypeCollection(items: type.TypeArguments.Select(selector: argument => CreateType(type: argument))),
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeRecord CreateShallowRecord(TypeMetadata type)
    {
        return new CodeRecord
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            IsAbstract = type.IsAbstract,
            IsGeneric = type.TypeParameters.Count > 0 || type.TypeArguments.Count > 0,
            Type = CreateTypeShell(type: type),
            TypeArguments = new Typewriter.CodeModel.TypeCollection(items: type.TypeArguments.Select(selector: argument => CreateType(type: argument))),
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeStruct CreateShallowStruct(TypeMetadata type)
    {
        return new CodeStruct
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            IsGeneric = type.TypeParameters.Count > 0 || type.TypeArguments.Count > 0,
            IsStatic = type.IsStatic,
            Type = CreateTypeShell(type: type),
            TypeArguments = new Typewriter.CodeModel.TypeCollection(items: type.TypeArguments.Select(selector: argument => CreateType(type: argument))),
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeInterface CreateShallowInterface(TypeMetadata type)
    {
        return new CodeInterface
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            IsGeneric = type.TypeParameters.Count > 0 || type.TypeArguments.Count > 0,
            Type = CreateTypeShell(type: type),
            TypeArguments = new Typewriter.CodeModel.TypeCollection(items: type.TypeArguments.Select(selector: argument => CreateType(type: argument))),
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeEnum CreateShallowEnum(TypeMetadata type)
    {
        return new CodeEnum
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            IsFlags = HasAttribute(attributes: type.Attributes, name: "Flags"),
            Type = CreateTypeShell(type: type),
        };
    }

    private CodeType CreateTypeShell(TypeMetadata type)
    {
        return new CodeType
        {
            AssemblyName = ResolveAssemblyName(assemblyName: type.AssemblyName),
            Name = type.Name,
            FullName = FormatGenericFullName(type: type),
            Namespace = type.Namespace,
            Attributes = CreateAttributes(attributes: type.Attributes, parent: null),
            FileLocations = type.FileLocations,
            IsDefined = true,
            IsEnum = type.Kind == TypeMetadataKind.Enum,
            IsGeneric = type.TypeParameters.Count > 0 || type.TypeArguments.Count > 0,
            IsStruct = type.Kind == TypeMetadataKind.Struct,
            DefaultValue = GetDefaultValue(type: type),
            Settings = _settings,
            TypeArguments = new Typewriter.CodeModel.TypeCollection(items: type.TypeArguments.Select(selector: argument => CreateType(type: argument))),
            TypeParameters = CreateTypeParameters(typeParameters: type.TypeParameters, parent: null),
        };
    }

    private CodeClass? CreateBaseClass(TypeMetadata type)
    {
        var baseType = type.BaseTypes.FirstOrDefault(predicate: reference => !IsInterfaceReference(reference: reference));
        if (baseType is null)
        {
            return null;
        }

        return _typesByFullName.TryGetValue(key: baseType.FullName, value: out var baseMetadata)
            && baseMetadata.Kind == TypeMetadataKind.Class
                ? CreateClass(type: SubstituteTypeArguments(definition: baseMetadata, reference: baseType), reference: baseType)
                : new CodeClass
                {
                    AssemblyName = ResolveAssemblyName(assemblyName: baseType.AssemblyName),
                    Name = baseType.Name,
                    FullName = baseType.FullName,
                    Namespace = baseType.Namespace,
                    IsGeneric = baseType.TypeArguments.Count > 0,
                    Type = CreateType(type: baseType),
                    TypeArguments = new Typewriter.CodeModel.TypeCollection(items: baseType.TypeArguments.Select(selector: argument => CreateType(type: argument))),
                };
    }

    private CodeRecord? CreateBaseRecord(TypeMetadata type)
    {
        var baseType = type.BaseTypes.FirstOrDefault(predicate: IsRecordBaseReference);
        if (baseType is null)
        {
            return null;
        }

        return _typesByFullName.TryGetValue(key: baseType.FullName, value: out var baseMetadata)
            && baseMetadata.Kind == TypeMetadataKind.Record
                ? CreateRecord(type: SubstituteTypeArguments(definition: baseMetadata, reference: baseType), reference: baseType)
                : new CodeRecord
                {
                    AssemblyName = ResolveAssemblyName(assemblyName: baseType.AssemblyName),
                    Name = baseType.Name,
                    FullName = baseType.FullName,
                    Namespace = baseType.Namespace,
                    IsGeneric = baseType.TypeArguments.Count > 0,
                    Type = CreateType(type: baseType),
                    TypeArguments = new Typewriter.CodeModel.TypeCollection(items: baseType.TypeArguments.Select(selector: argument => CreateType(type: argument))),
                };
    }

    private bool IsRecordBaseReference(TypeMetadataReference reference)
    {
        if (_typesByFullName.TryGetValue(key: reference.FullName, value: out var metadata))
        {
            return metadata.Kind == TypeMetadataKind.Record;
        }

        // Heuristic: exclude names that look like interfaces (I + uppercase) and
        // System.I* types, which are far more likely to be interfaces than records.
        if (reference.Name.Length > 1
            && reference.Name[index: 0] == 'I'
            && char.IsUpper(c: reference.Name[index: 1]))
        {
            return false;
        }

        if (reference.FullName.StartsWith(value: "System.I", comparisonType: StringComparison.Ordinal))
        {
            return false;
        }

        // Non-interface, non-class external base is conservatively treated as not a record
        // since the fallback CreateBaseRecord already handles the shell case.
        return false;
    }

    private string ResolveAssemblyName(string? assemblyName)
    {
        return string.IsNullOrWhiteSpace(value: assemblyName)
            ? _assemblyName
            : assemblyName;
    }

    private string GetDefaultValue(
        TypeMetadataReference type,
        FrontendRuntimeTypeKind runtimeType = FrontendRuntimeTypeKind.Auto)
    {
        if (type.IsNullable)
        {
            return "null";
        }

        if (type.IsDictionary)
        {
            return "{}";
        }

        if (type.IsCollection)
        {
            return "[]";
        }

        var stringLiteralCharacter = _settings.StringLiteralCharacter;
        var overriddenDefault = GetRuntimeOverrideDefault(runtimeType: runtimeType, stringLiteralCharacter: stringLiteralCharacter);
        if (overriddenDefault is not null)
        {
            return overriddenDefault;
        }

        if ((_settings.DateLibraryGeneration != Typewriter.Configuration.DateLibrary.Legacy
             || runtimeType != FrontendRuntimeTypeKind.Auto)
            && DateSemanticTypeResolver.Resolve(type: type, runtimeType: runtimeType) is { } semanticKind)
        {
            return _settings.GetDateInitializer(kind: semanticKind);
        }

        if (TryGetSpecialDefault(type: type, stringLiteralCharacter: stringLiteralCharacter, value: out var specialDefault))
        {
            return specialDefault;
        }

        if (type.IsEnum)
        {
            return ResolveEnumDefault(type: type) ?? "0";
        }

        if (type.Name.Equals(value: "Boolean", comparisonType: StringComparison.OrdinalIgnoreCase)
            || type.Name.Equals(value: "bool", comparisonType: StringComparison.OrdinalIgnoreCase)
            || type.FullName.Equals(value: "System.Boolean", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return "false";
        }

        if (type.IsPrimitive
            && !type.Name.Equals(value: "String", comparisonType: StringComparison.OrdinalIgnoreCase)
            && !type.FullName.Equals(value: "System.String", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return "0";
        }

        return type.Name.Equals(value: "String", comparisonType: StringComparison.OrdinalIgnoreCase)
            || type.FullName.Equals(value: "System.String", comparisonType: StringComparison.OrdinalIgnoreCase)
                ? $"{stringLiteralCharacter}{stringLiteralCharacter}"
                : "null";
    }

    private string? GetRuntimeOverrideDefault(
        FrontendRuntimeTypeKind runtimeType,
        char stringLiteralCharacter) =>
        runtimeType switch
        {
            FrontendRuntimeTypeKind.Decimal => ScalarInitializer.ResolveDecimal(
                decimalType: _settings.DecimalTypeGeneration,
                decimalInitializer: _settings.DecimalInitializerGeneration),
            FrontendRuntimeTypeKind.Uuid => ScalarInitializer.ResolveGuid(
                guidType: _settings.GuidTypeGeneration,
                guidInitializer: _settings.GuidInitializerGeneration,
                stringLiteralCharacter: stringLiteralCharacter),
            FrontendRuntimeTypeKind.String => $"{stringLiteralCharacter}{stringLiteralCharacter}",
            _ => null,
        };

    private bool TryGetSpecialDefault(
        TypeMetadataReference type,
        char stringLiteralCharacter,
        [NotNullWhen(returnValue: true)] out string? value)
    {
        if (type.FullName.Equals(value: "System.Guid", comparisonType: StringComparison.Ordinal))
        {
            value = ScalarInitializer.ResolveGuid(
                guidType: _settings.GuidTypeGeneration,
                guidInitializer: _settings.GuidInitializerGeneration,
                stringLiteralCharacter: stringLiteralCharacter);
            return true;
        }

        if (type.FullName.Equals(value: "System.TimeSpan", comparisonType: StringComparison.Ordinal))
        {
            value = $"{stringLiteralCharacter}00:00:00{stringLiteralCharacter}";
            return true;
        }

        if (TypeScriptTemporalTypes.IsDateOnly(fullName: type.FullName))
        {
            value = _settings.DateOnlyInitializerGeneration;
            return true;
        }

        if (TypeScriptTemporalTypes.IsTimeOnly(fullName: type.FullName))
        {
            value = TypeScriptTemporalTypes.FormatTimeOnlyInitializer(
                initializer: _settings.TimeOnlyInitializerGeneration,
                stringLiteralCharacter: stringLiteralCharacter);
            return true;
        }

        value = GetConfiguredNonStringDefault(type: type);
        return value is not null;
    }

    private string? GetConfiguredNonStringDefault(TypeMetadataReference type)
    {
        if (type.FullName.Equals(value: "System.Decimal", comparisonType: StringComparison.Ordinal))
        {
            return ScalarInitializer.ResolveDecimal(
                decimalType: _settings.DecimalTypeGeneration,
                decimalInitializer: _settings.DecimalInitializerGeneration);
        }

        return type.IsDateLike || TypeScriptTemporalTypes.IsDateTime(fullName: type.FullName)
            ? _settings.DateInitializerGeneration
            : null;
    }

    private string? ResolveEnumDefault(TypeMetadataReference type)
    {
        if (type.EnumValues.Count > 0)
        {
            return ResolveEnumDefault(enumName: type.Name, enumValues: type.EnumValues);
        }

        return _typesByFullName.TryGetValue(key: type.FullName, value: out var enumMetadata)
            ? ResolveEnumDefault(enumName: type.Name, enumValues: enumMetadata.EnumValues)
            : null;
    }

    /// <summary>
    /// A <see cref="CodeFile"/> whose type collections are adapted on first access instead of
    /// eagerly in <see cref="AdaptProject"/>. Each collection is memoized so repeated template
    /// access costs nothing extra, and templates that never touch a collection never pay for it.
    /// </summary>
    private sealed class LazyCodeFile : CodeFile
    {
        private readonly Lazy<Typewriter.CodeModel.IClassCollection> _classes;
        private readonly Lazy<Typewriter.CodeModel.IDelegateCollection> _delegates;
        private readonly Lazy<Typewriter.CodeModel.IEnumCollection> _enums;
        private readonly Lazy<Typewriter.CodeModel.IInterfaceCollection> _interfaces;
        private readonly Lazy<Typewriter.CodeModel.IRecordCollection> _records;
        private readonly Lazy<Typewriter.CodeModel.IStructCollection> _structs;
        private readonly Lazy<Typewriter.CodeModel.ITypeCollection> _types;

        public LazyCodeFile(
            TemplateCodeModelAdapterFactory factory,
            ProjectMetadata project)
        {
            var rootTypes = new Lazy<TypeMetadata[]>(valueFactory: () => project.Types
                .Where(predicate: static type => string.IsNullOrWhiteSpace(value: type.ContainingTypeFullName))
                .ToArray());

            _classes = new Lazy<Typewriter.CodeModel.IClassCollection>(valueFactory: () =>
                new Typewriter.CodeModel.ClassCollection(
                    items: rootTypes.Value
                        .Where(predicate: static type => type.Kind == TypeMetadataKind.Class)
                        .Select(selector: factory.CreateClass)));
            _records = new Lazy<Typewriter.CodeModel.IRecordCollection>(valueFactory: () =>
                new Typewriter.CodeModel.RecordCollection(
                    items: rootTypes.Value
                        .Where(predicate: static type => type.Kind == TypeMetadataKind.Record)
                        .Select(selector: factory.CreateRecord)));
            _structs = new Lazy<Typewriter.CodeModel.IStructCollection>(valueFactory: () =>
                new Typewriter.CodeModel.StructCollection(
                    items: rootTypes.Value
                        .Where(predicate: static type => type.Kind == TypeMetadataKind.Struct)
                        .Select(selector: factory.CreateStruct)));
            _interfaces = new Lazy<Typewriter.CodeModel.IInterfaceCollection>(valueFactory: () =>
                new Typewriter.CodeModel.InterfaceCollection(
                    items: rootTypes.Value
                        .Where(predicate: static type => type.Kind == TypeMetadataKind.Interface)
                        .Select(selector: factory.CreateInterface)));
            _enums = new Lazy<Typewriter.CodeModel.IEnumCollection>(valueFactory: () =>
                new Typewriter.CodeModel.EnumCollection(
                    items: rootTypes.Value
                        .Where(predicate: static type => type.Kind == TypeMetadataKind.Enum)
                        .Select(selector: factory.CreateEnum)));
            _types = new Lazy<Typewriter.CodeModel.ITypeCollection>(valueFactory: () =>
                new Typewriter.CodeModel.TypeCollection(items: rootTypes.Value.Select(selector: factory.CreateType)));
            _delegates = new Lazy<Typewriter.CodeModel.IDelegateCollection>(valueFactory: () =>
                new Typewriter.CodeModel.DelegateCollection(
                    items: project.Delegates.Select(selector: @delegate => factory.CreateDelegate(@delegate: @delegate, parent: null))));
        }

        public override Typewriter.CodeModel.IClassCollection Classes => _classes.Value;

        public override Typewriter.CodeModel.IDelegateCollection Delegates => _delegates.Value;

        public override Typewriter.CodeModel.IEnumCollection Enums => _enums.Value;

        public override Typewriter.CodeModel.IInterfaceCollection Interfaces => _interfaces.Value;

        public override Typewriter.CodeModel.IRecordCollection Records => _records.Value;

        public override Typewriter.CodeModel.IStructCollection Structs => _structs.Value;

        public override Typewriter.CodeModel.ITypeCollection Types => _types.Value;
    }

    // Compares only the reference identity of the metadata object plus the runtime-type
    // discriminator. Structural equality would be both slower and wrong here, since two
    // structurally identical references can still carry different declaration context.
    private sealed class TypeReferenceKeyComparer : IEqualityComparer<(TypeMetadataReference Reference, FrontendRuntimeTypeKind RuntimeType)>
    {
        public static readonly TypeReferenceKeyComparer Instance = new();

        public bool Equals(
            (TypeMetadataReference Reference, FrontendRuntimeTypeKind RuntimeType) x,
            (TypeMetadataReference Reference, FrontendRuntimeTypeKind RuntimeType) y)
        {
            return ReferenceEquals(objA: x.Reference, objB: y.Reference) && x.RuntimeType == y.RuntimeType;
        }

        public int GetHashCode((TypeMetadataReference Reference, FrontendRuntimeTypeKind RuntimeType) obj)
        {
            return HashCode.Combine(
                value1: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: obj.Reference),
                value2: (int)obj.RuntimeType);
        }
    }
}
