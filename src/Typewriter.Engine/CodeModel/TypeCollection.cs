using Typewriter.Engine;

namespace Typewriter.CodeModel;

public sealed class TypeCollection : ItemCollection<Type>, ITypeCollection
{
    public TypeCollection()
    {
    }

    public TypeCollection(IEnumerable<Type> items)
        : base(items: items)
    {
    }

    public override string ToString() => Count == 0
        ? string.Empty
        : string.Concat(str0: "<", str1: string.Join(separator: ", ", values: this.Select(selector: item => item.Name)), str2: ">");

    protected override IEnumerable<string> GetAttributeFilter(Type item) => GetAttributeNames(attributes: item.Attributes);

    protected override IEnumerable<string> GetInheritanceFilter(Type item)
    {
        if (item.BaseClass is not null)
        {
            yield return item.BaseClass.Name;
            yield return item.BaseClass.FullName;
        }

        foreach (var implementedInterface in item.Interfaces)
        {
            yield return implementedInterface.Name;
            yield return implementedInterface.FullName;
        }
    }

    // Filters match on the C# name or the full name. OriginalName now carries the v3.0.1 nullable
    // '?' suffix, so the un-suffixed name is offered as well; otherwise a filter such as
    // $Types(int) would stop matching nullable occurrences that it used to match.
    protected override IEnumerable<string> GetItemFilter(Type item)
    {
        yield return item.OriginalName;

        var withoutNullableSuffix = CSharpTypeNameFormatter.TrimNullableSuffix(name: item.OriginalName);
        if (!string.Equals(a: withoutNullableSuffix, b: item.OriginalName, comparisonType: StringComparison.Ordinal))
        {
            yield return withoutNullableSuffix;
        }

        yield return item.FullName;
    }
}
