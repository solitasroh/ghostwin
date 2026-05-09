using System.Reflection;
using Xunit;

namespace GhostWin.E2E.Tests;

public sealed class SuiteClassificationTests
{
    [Fact]
    public void Nightly_slow_and_audit_tests_are_interactive_category()
    {
        var offenders = typeof(SuiteClassificationTests).Assembly
            .GetTypes()
            .Where(IsConcreteTestClass)
            .Where(type => HasTrait(type, "Nightly", "true") ||
                           HasTrait(type, "Slow", "true") ||
                           HasTrait(type, "Tier", "Audit") ||
                           HasInteractiveSkipReason(type))
            .Where(type => !HasTrait(type, "Category", "Interactive"))
            .Select(type => type.FullName)
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Nightly/Slow/Audit tests must use Category=Interactive: " +
            string.Join(", ", offenders));
    }

    private static bool IsConcreteTestClass(Type type)
        => type is { IsClass: true, IsAbstract: false } &&
           type.GetMethods().Any(method =>
               method.GetCustomAttributes().Any(attribute =>
                   attribute is FactAttribute or TheoryAttribute));

    private static bool HasTrait(MemberInfo member, string name, string value)
        => member.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.Name == "TraitAttribute")
            .Any(attribute => attribute.ConstructorArguments.Count == 2 &&
                              string.Equals(attribute.ConstructorArguments[0].Value as string, name, StringComparison.Ordinal) &&
                              string.Equals(attribute.ConstructorArguments[1].Value as string, value, StringComparison.Ordinal));

    private static bool HasInteractiveSkipReason(Type type)
        => type.GetMethods()
            .SelectMany(method => method.GetCustomAttributesData())
            .Where(attribute => typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType) ||
                                typeof(TheoryAttribute).IsAssignableFrom(attribute.AttributeType))
            .SelectMany(attribute => attribute.NamedArguments)
            .Any(argument => argument.MemberName == "Skip" &&
                             argument.TypedValue.Value is string skip &&
                             skip.Contains("interactive", StringComparison.OrdinalIgnoreCase));
}
