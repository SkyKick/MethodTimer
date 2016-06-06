using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

public static class AttributeChecker
{
    public static bool ContainsTimeAttribute(this ICustomAttributeProvider definition)
    {
        var customAttributes = definition.CustomAttributes;

        return customAttributes.Any(x => x.AttributeType.Name == "TimeAttribute");
    }

    public static bool MatchesPointcuts(this TypeDefinition definition, Regex pointcutRegex)
    {
        return pointcutRegex.IsMatch(definition.FullName);
    }

    public static bool IsCompilerGenerated(this ICustomAttributeProvider definition)
    {
        var customAttributes = definition.CustomAttributes;

        return customAttributes.Any(x => x.AttributeType.Name == "CompilerGeneratedAttribute");
    }

    public static void RemoveTimeAttribute(this ICustomAttributeProvider definition)
    {
        var customAttributes = definition.CustomAttributes;

        var timeAttribute = customAttributes.FirstOrDefault(x => x.AttributeType.Name == "TimeAttribute");

        if (timeAttribute != null)
        {
            customAttributes.Remove(timeAttribute);
        }

    }
}