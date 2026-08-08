using Typewriter.Abstractions;

namespace Typewriter.Engine;

internal sealed class ProjectMetadataIndex
{
    private readonly Lazy<SourceFileDependencyIndex> _dependencyIndex;

    private ProjectMetadataIndex(
        ProjectMetadata metadata,
        IReadOnlyDictionary<string, TypeMetadata> typesByFullName,
        IReadOnlyDictionary<string, MethodMetadata> methodsByFullName,
        IReadOnlyDictionary<string, PropertyMetadata> propertiesByFullName)
    {
        TypesByFullName = typesByFullName;
        MethodsByFullName = methodsByFullName;
        PropertiesByFullName = propertiesByFullName;
        _dependencyIndex = new Lazy<SourceFileDependencyIndex>(
            valueFactory: () => SourceFileDependencyIndex.Build(metadata: metadata),
            mode: LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyDictionary<string, TypeMetadata> TypesByFullName { get; }

    public IReadOnlyDictionary<string, MethodMetadata> MethodsByFullName { get; }

    public IReadOnlyDictionary<string, PropertyMetadata> PropertiesByFullName { get; }

    public static ProjectMetadataIndex Create(ProjectMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(argument: metadata);

        return new ProjectMetadataIndex(
            metadata: metadata,
            typesByFullName: metadata.Types
                .GroupBy(keySelector: type => type.FullName, comparer: StringComparer.Ordinal)
                .ToDictionary(keySelector: group => group.Key, elementSelector: group => group.First(), comparer: StringComparer.Ordinal),
            methodsByFullName: metadata.Types
                .SelectMany(selector: type => type.Methods)
                .GroupBy(keySelector: method => method.FullName, comparer: StringComparer.Ordinal)
                .ToDictionary(keySelector: group => group.Key, elementSelector: group => group.First(), comparer: StringComparer.Ordinal),
            propertiesByFullName: metadata.Types
                .SelectMany(selector: type => type.Properties)
                .GroupBy(keySelector: property => property.FullName, comparer: StringComparer.Ordinal)
                .ToDictionary(keySelector: group => group.Key, elementSelector: group => group.First(), comparer: StringComparer.Ordinal));
    }

    /// <summary>
    /// Computes the transitive closure of source files affected by the changed source
    /// files: every changed file plus every file whose declared types reference a type
    /// declared in an affected file. Paths not tracked by this project are ignored.
    /// </summary>
    /// <param name="changedSourcePaths">The full paths of the changed source files.</param>
    /// <returns>The full paths of the affected source files (case-insensitive set).</returns>
    public IReadOnlyCollection<string> GetAffectedSourceFiles(IReadOnlyCollection<string> changedSourcePaths)
    {
        ArgumentNullException.ThrowIfNull(argument: changedSourcePaths);

        return _dependencyIndex.Value.GetAffectedSourceFiles(changedSourcePaths: changedSourcePaths);
    }

    private sealed class SourceFileDependencyIndex
    {
        private readonly IReadOnlyDictionary<string, HashSet<string>> _declaredTypesBySourceFile;
        private readonly IReadOnlyDictionary<string, HashSet<string>> _referencedTypesBySourceFile;

        // Maps each file that declares part of a partial type to the other files declaring that
        // same type. Partial siblings never reference one another, so the type-reference graph
        // above cannot connect them; without this map an edit to one declaring file leaves the
        // others (and, under Combined, the single file that actually owns the output) stale.
        private readonly IReadOnlyDictionary<string, HashSet<string>> _partialSiblingsBySourceFile;

        private SourceFileDependencyIndex(
            IReadOnlyDictionary<string, HashSet<string>> declaredTypesBySourceFile,
            IReadOnlyDictionary<string, HashSet<string>> referencedTypesBySourceFile,
            IReadOnlyDictionary<string, HashSet<string>> partialSiblingsBySourceFile)
        {
            _declaredTypesBySourceFile = declaredTypesBySourceFile;
            _referencedTypesBySourceFile = referencedTypesBySourceFile;
            _partialSiblingsBySourceFile = partialSiblingsBySourceFile;
        }

        public static SourceFileDependencyIndex Build(ProjectMetadata metadata)
        {
            var declaredTypesBySourceFile = new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
            var referencedTypesBySourceFile = new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
            foreach (var sourceFile in metadata.SourceFiles)
            {
                var declaredTypes = new HashSet<string>(comparer: StringComparer.Ordinal);
                var referencedTypes = new HashSet<string>(comparer: StringComparer.Ordinal);
                var visitedReferences = new HashSet<object>(comparer: ReferenceEqualityComparer.Instance);
                foreach (var type in sourceFile.Types)
                {
                    CollectType(type: type, declaredTypes: declaredTypes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                }

                foreach (var delegateMetadata in sourceFile.Delegates)
                {
                    _ = declaredTypes.Add(item: delegateMetadata.FullName);
                    CollectDelegate(delegateMetadata: delegateMetadata, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                }

                var sourcePath = sourceFile.Path;
                declaredTypesBySourceFile[key: sourcePath] = declaredTypes;
                referencedTypesBySourceFile[key: sourcePath] = referencedTypes;
            }

            return new SourceFileDependencyIndex(
                declaredTypesBySourceFile: declaredTypesBySourceFile,
                referencedTypesBySourceFile: referencedTypesBySourceFile,
                partialSiblingsBySourceFile: BuildPartialSiblings(metadata: metadata));
        }

        public IReadOnlyCollection<string> GetAffectedSourceFiles(IReadOnlyCollection<string> changedSourcePaths)
        {
            var affectedFiles = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
            var dirtyTypes = new HashSet<string>(comparer: StringComparer.Ordinal);
            foreach (var changedPath in changedSourcePaths)
            {
                if (_declaredTypesBySourceFile.TryGetValue(key: changedPath, value: out var declaredTypes)
                    && affectedFiles.Add(item: changedPath))
                {
                    dirtyTypes.UnionWith(other: declaredTypes);
                }
            }

            // Editing one declaration of a partial type invalidates the type as a whole, so pull in
            // its other declaring files before the reference walk below. This restores the v3.0.1
            // behaviour where rendering a non-primary declaration triggered a render of the file
            // that owns the output. It matters most under PartialRenderingMode.Combined, where the
            // type is emitted only on its primary file: without this, editing any other declaring
            // file yields no render context at all and the output silently goes stale.
            ExpandPartialSiblings(
                affectedFiles: affectedFiles,
                dirtyTypes: dirtyTypes,
                partialSiblingsBySourceFile: _partialSiblingsBySourceFile,
                declaredTypesBySourceFile: _declaredTypesBySourceFile);

            var expanded = affectedFiles.Count > 0;
            while (expanded)
            {
                expanded = false;
                foreach (var pair in _referencedTypesBySourceFile)
                {
                    if (affectedFiles.Contains(item: pair.Key) || !pair.Value.Overlaps(other: dirtyTypes))
                    {
                        continue;
                    }

                    _ = affectedFiles.Add(item: pair.Key);
                    if (_declaredTypesBySourceFile.TryGetValue(key: pair.Key, value: out var declaredTypes))
                    {
                        dirtyTypes.UnionWith(other: declaredTypes);
                    }

                    // A file reached through the reference graph may itself declare part of a
                    // partial type, so its siblings must be pulled in as well.
                    ExpandPartialSiblings(
                        affectedFiles: affectedFiles,
                        dirtyTypes: dirtyTypes,
                        partialSiblingsBySourceFile: _partialSiblingsBySourceFile,
                        declaredTypesBySourceFile: _declaredTypesBySourceFile);

                    expanded = true;
                }
            }

            return affectedFiles;
        }

        /// <summary>
        /// Expands <paramref name="affectedFiles"/> to a fixpoint over partial-type siblings.
        /// </summary>
        /// <param name="affectedFiles">The affected file set, extended in place.</param>
        /// <param name="dirtyTypes">The dirty type set, extended with any newly affected file's declarations.</param>
        /// <param name="partialSiblingsBySourceFile">Map from a declaring file to its partial siblings.</param>
        /// <param name="declaredTypesBySourceFile">Map from a source file to the types it declares.</param>
        /// <remarks>
        /// Loops until no new file is added because a sibling may itself be a declaration of a
        /// different partial type, chaining one group of declaring files into another.
        /// </remarks>
        private static void ExpandPartialSiblings(
            HashSet<string> affectedFiles,
            HashSet<string> dirtyTypes,
            IReadOnlyDictionary<string, HashSet<string>> partialSiblingsBySourceFile,
            IReadOnlyDictionary<string, HashSet<string>> declaredTypesBySourceFile)
        {
            if (partialSiblingsBySourceFile.Count == 0)
            {
                return;
            }

            var pending = new Queue<string>(collection: affectedFiles);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!partialSiblingsBySourceFile.TryGetValue(key: current, value: out var siblings))
                {
                    continue;
                }

                foreach (var sibling in siblings)
                {
                    if (!affectedFiles.Add(item: sibling))
                    {
                        continue;
                    }

                    if (declaredTypesBySourceFile.TryGetValue(key: sibling, value: out var siblingDeclaredTypes))
                    {
                        dirtyTypes.UnionWith(other: siblingDeclaredTypes);
                    }

                    pending.Enqueue(item: sibling);
                }
            }
        }

        private static void CollectType(
            TypeMetadata type,
            ISet<string> declaredTypes,
            ISet<string> referencedTypes,
            ISet<object> visitedReferences)
        {
            _ = declaredTypes.Add(item: type.FullName);
            CollectReferences(references: type.BaseTypes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            CollectReferences(references: type.TypeArguments, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            CollectAttributes(attributes: type.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            CollectMembers(type: type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            foreach (var delegateMetadata in type.Delegates)
            {
                _ = declaredTypes.Add(item: delegateMetadata.FullName);
                CollectDelegate(delegateMetadata: delegateMetadata, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }

            foreach (var nestedType in EnumerateNestedTypes(type: type))
            {
                CollectType(type: nestedType, declaredTypes: declaredTypes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }
        }

        /// <summary>
        /// Builds the file-to-sibling-files map for every partial type in the project.
        /// </summary>
        /// <param name="metadata">The project metadata to scan for multi-file type declarations.</param>
        /// <returns>
        /// A case-insensitive map from a declaring file to the other files declaring the same
        /// partial type(s). Files that declare no partial type are absent from the map.
        /// </returns>
        /// <remarks>
        /// A type declared across several files reports every declaring file in
        /// <see cref="TypeMetadata.FileLocations"/>, so grouping by that list is sufficient; types
        /// with a single location are skipped because they have no siblings. Nested types are
        /// walked as well, since a nested type can be partial independently of its container.
        /// </remarks>
        private static Dictionary<string, HashSet<string>> BuildPartialSiblings(ProjectMetadata metadata)
        {
            var partialSiblingsBySourceFile = new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);

            void Link(TypeMetadata type)
            {
                if (type.FileLocations.Count > 1)
                {
                    foreach (var declaringPath in type.FileLocations)
                    {
                        if (!partialSiblingsBySourceFile.TryGetValue(key: declaringPath, value: out var siblings))
                        {
                            siblings = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
                            partialSiblingsBySourceFile[key: declaringPath] = siblings;
                        }

                        // Every declaring file (including this one) is added; the self-entry is
                        // harmless because the caller only unions into an already-affected set.
                        siblings.UnionWith(other: type.FileLocations);
                    }
                }

                foreach (var nestedType in EnumerateNestedTypes(type: type))
                {
                    Link(type: nestedType);
                }
            }

            foreach (var type in metadata.Types)
            {
                Link(type: type);
            }

            return partialSiblingsBySourceFile;
        }

        private static IEnumerable<TypeMetadata> EnumerateNestedTypes(TypeMetadata type) =>
            type.NestedClasses
                .Concat(second: type.NestedRecords)
                .Concat(second: type.NestedStructs)
                .Concat(second: type.NestedEnums)
                .Concat(second: type.NestedInterfaces);

        private static void CollectMembers(
            TypeMetadata type,
            ISet<string> referencedTypes,
            ISet<object> visitedReferences)
        {
            foreach (var property in type.Properties)
            {
                CollectReference(reference: property.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectAttributes(attributes: property.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectParameters(parameters: property.Parameters, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }

            foreach (var method in type.Methods)
            {
                CollectReference(reference: method.ReturnType, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectAttributes(attributes: method.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectParameters(parameters: method.Parameters, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }

            foreach (var constant in type.Constants)
            {
                CollectReference(reference: constant.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectAttributes(attributes: constant.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }

            foreach (var field in type.Fields)
            {
                CollectReference(reference: field.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectAttributes(attributes: field.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }

            foreach (var staticReadOnlyField in type.StaticReadOnlyFields)
            {
                CollectReference(reference: staticReadOnlyField.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectAttributes(attributes: staticReadOnlyField.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }

            foreach (var @event in type.Events)
            {
                CollectReference(reference: @event.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectAttributes(attributes: @event.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }
        }

        private static void CollectDelegate(
            DelegateMetadata delegateMetadata,
            ISet<string> referencedTypes,
            ISet<object> visitedReferences)
        {
            CollectReference(reference: delegateMetadata.ReturnType, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            CollectAttributes(attributes: delegateMetadata.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            CollectParameters(parameters: delegateMetadata.Parameters, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
        }

        private static void CollectParameters(
            IReadOnlyList<ParameterMetadata> parameters,
            ISet<string> referencedTypes,
            ISet<object> visitedReferences)
        {
            foreach (var parameter in parameters)
            {
                CollectReference(reference: parameter.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                CollectAttributes(attributes: parameter.Attributes, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }
        }

        private static void CollectAttributes(
            IReadOnlyList<AttributeMetadata> attributes,
            ISet<string> referencedTypes,
            ISet<object> visitedReferences)
        {
            foreach (var attribute in attributes)
            {
                _ = referencedTypes.Add(item: attribute.FullName);
                CollectReference(reference: attribute.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                foreach (var argument in attribute.Arguments)
                {
                    CollectReference(reference: argument.Type, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                    CollectReference(reference: argument.TypeValue, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
                }
            }
        }

        private static void CollectReferences(
            IReadOnlyList<TypeMetadataReference> references,
            ISet<string> referencedTypes,
            ISet<object> visitedReferences)
        {
            foreach (var reference in references)
            {
                CollectReference(reference: reference, referencedTypes: referencedTypes, visitedReferences: visitedReferences);
            }
        }

        private static void CollectReference(
            TypeMetadataReference? reference,
            ISet<string> referencedTypes,
            ISet<object> visitedReferences)
        {
            if (reference is null)
            {
                return;
            }

            var pending = new Stack<TypeMetadataReference>();
            pending.Push(item: reference);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visitedReferences.Add(item: current))
                {
                    continue;
                }

                _ = referencedTypes.Add(item: current.FullName);
                if (current.ElementType is not null)
                {
                    pending.Push(item: current.ElementType);
                }

                foreach (var typeArgument in current.TypeArguments)
                {
                    pending.Push(item: typeArgument);
                }

                foreach (var tupleElement in current.TupleElements)
                {
                    pending.Push(item: tupleElement.Type);
                }
            }
        }
    }
}
