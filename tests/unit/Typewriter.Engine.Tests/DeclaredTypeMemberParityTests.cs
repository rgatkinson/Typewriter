using System.Reflection;
using Typewriter.Engine;
using Xunit;

namespace Typewriter.Engine.Tests;

/// <summary>
/// Structural guards ensuring the declaration path and the reference path cannot drift apart again.
/// </summary>
/// <remarks>
/// <para>
/// The declaration-vs-reference bug was not a single mistake; it recurred because the two paths
/// were parallel hand-maintained lists of member assignments, and a member added to one was easy to
/// omit from the other. The behavioural tests in
/// <see cref="DeclarationVersusReferenceTests"/> catch the members we thought to check; these tests
/// catch the ones nobody thought to check, by asserting on the shape of the code itself.
/// </para>
/// <para>
/// If one of these fails, the fix is not to update the expectation -- it is to wire the new member
/// through both paths.
/// </para>
/// </remarks>
public sealed class DeclaredTypeMemberParityTests
{
    // Every factory declared on the shared set must actually reach the reference-side code model. A
    // member added to DeclaredTypeMembers but never consumed by MappedCodeType would reintroduce
    // exactly the original bug: populated on declarations, silently empty on references.
    [Fact]
    public void MappedCodeTypeProjectsEveryDeclaredMember()
    {
        // Attributes is the one member set eagerly through the init-property rather than lazily
        // overridden. It is safe to materialise during construction because attribute models do not
        // recurse back into type construction, and the reference path deliberately builds the
        // reference-safe variant. Both paths still source it from the shared set, so it cannot drift.
        var eagerlyAssigned = new HashSet<string>(comparer: StringComparer.Ordinal) { "Attributes" };

        var declaredMembers = typeof(DeclaredTypeMembers)
            .GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
            .Select(selector: property => property.Name)
            .Where(predicate: name => !eagerlyAssigned.Contains(name))
            .ToList();

        var overriddenMembers = typeof(MappedCodeType)
            .GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
            .Where(predicate: property => property.GetMethod?.GetBaseDefinition().DeclaringType != property.DeclaringType)
            .Select(selector: property => property.Name)
            .ToHashSet(comparer: StringComparer.Ordinal);

        declaredMembers.Should().NotBeEmpty();

        foreach (var member in declaredMembers)
        {
            overriddenMembers.Should().Contain(
                expected: member,
                because: "MappedCodeType must project '{0}' or references will silently report it as empty",
                becauseArgs: member);
        }
    }

    // The reference-side type must not re-expose the old one-property-per-member factory surface.
    // Those properties are what allowed the two paths to be wired independently; funnelling
    // everything through the single DeclaredMembers object is what removes the drift.
    [Fact]
    public void MappedCodeTypeDoesNotReintroducePerMemberFactories()
    {
        var factoryProperties = typeof(MappedCodeType)
            .GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
            .Where(predicate: property => property.Name.EndsWith(value: "Factory", comparisonType: StringComparison.Ordinal))
            .Select(selector: property => property.Name)
            .ToList();

        factoryProperties.Should().BeEmpty();
    }

    // Declared members are exposed as delegates rather than materialised values because eager
    // construction on the reference path does not terminate. A member typed as a plain collection
    // would be evaluated during construction and could reintroduce the original stack overflow.
    [Fact]
    public void DeclaredTypeMembersAreAllDeferred()
    {
        var nonDeferred = typeof(DeclaredTypeMembers)
            .GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
            .Where(predicate: property => !property.PropertyType.IsGenericType
                || property.PropertyType.GetGenericTypeDefinition() != typeof(Func<>))
            .Select(selector: property => property.Name)
            .ToList();

        nonDeferred.Should().BeEmpty();
    }

    // The previous two tests verify the shared set is fully consumed; this one verifies the set is
    // complete in the first place. Every declared-member surface on the code model's Type must be
    // present in DeclaredTypeMembers, otherwise a member could be populated on declarations and
    // never wired into the shared set at all -- which is precisely how BaseClass, Interfaces,
    // DocComment, the Nested* family and TypeParameters were missed on the reference path.
    //
    // Reference-level facts are excluded because they legitimately come from the reference itself
    // rather than from any declaration.
    [Fact]
    public void DeclaredTypeMembersCoversTypeDeclaredSurface()
    {
        // Facts about a use of a type, not about its declaration.
        var referenceLevelMembers = new HashSet<string>(comparer: StringComparer.Ordinal)
        {
            "AssemblyName", "DefaultValue", "ElementType", "FileLocations", "FullName", "IsDate",
            "IsDefined", "IsDictionary", "IsDynamic", "IsEnum", "IsEnumerable", "IsGeneric",
            "IsGuid", "IsNullable", "IsPrimitive", "IsStruct", "IsTask", "IsTimeSpan",
            "IsValueTuple", "Name", "name", "Namespace", "OriginalName", "Parent", "Settings",
            "TupleElements", "TypeArguments",
        };

        var declaredSurface = typeof(Typewriter.CodeModel.Type)
            .GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
            .Select(selector: property => property.Name)
            .Where(predicate: name => !referenceLevelMembers.Contains(name))
            .ToList();

        var sharedSet = typeof(DeclaredTypeMembers)
            .GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
            .Select(selector: property => property.Name)
            .ToHashSet(comparer: StringComparer.Ordinal);

        declaredSurface.Should().NotBeEmpty();

        foreach (var member in declaredSurface)
        {
            sharedSet.Should().Contain(
                expected: member,
                because: "'{0}' is a declared member, so it must be wired through DeclaredTypeMembers to reach type references",
                becauseArgs: member);
        }
    }
}
