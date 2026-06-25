// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exceptions;
using System.Linq;
using System.Text.RegularExpressions;
namespace JIM.Models.ExampleData;

public partial class ExampleDataObjectType
{
    public int Id { get; set; }
    public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
    public List<ExampleDataTemplateAttribute> TemplateAttributes { get; } = new();
    public int ObjectsToCreate { get; set; }

    // matches mv["Attribute Name"] / mv['Attribute Name'] references inside an expression, capturing the attribute name.
    [GeneratedRegex(@"mv\s*\[\s*[""']([^""']+)[""']\s*\]", RegexOptions.Compiled)]
    private static partial Regex MetaverseAttributeReferenceRegex();

    /// <summary>
    /// Returns this object type's template attributes ordered so that any attribute a given attribute depends on is
    /// generated first. Dependencies are derived from (a) the attributes an <see cref="ExampleDataTemplateAttribute.Expression"/>
    /// references via the mv["Attribute Name"] accessor, and (b) an attribute's conditional <see cref="ExampleDataTemplateAttribute.AttributeDependency"/>.
    /// A deterministic topological sort is used (original declaration order breaks ties). Throws if a circular dependency is detected.
    /// </summary>
    /// <exception cref="ExampleDataTemplateException">A circular dependency exists between attribute generation expressions/dependencies.</exception>
    public List<ExampleDataTemplateAttribute> GetTemplateAttributesInDependencyOrder()
    {
        // map referenceable attributes (those targeting a Metaverse Attribute) by name, case-insensitively.
        var attributesByName = new Dictionary<string, ExampleDataTemplateAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in TemplateAttributes.Where(a => a.MetaverseAttribute != null))
            attributesByName[attribute.MetaverseAttribute!.Name] = attribute;

        var ordered = new List<ExampleDataTemplateAttribute>(TemplateAttributes.Count);
        var visited = new HashSet<ExampleDataTemplateAttribute>();
        var visiting = new HashSet<ExampleDataTemplateAttribute>();

        foreach (var attribute in TemplateAttributes)
            Visit(attribute, attributesByName, ordered, visited, visiting);

        return ordered;
    }

    private static void Visit(
        ExampleDataTemplateAttribute attribute,
        Dictionary<string, ExampleDataTemplateAttribute> attributesByName,
        List<ExampleDataTemplateAttribute> ordered,
        HashSet<ExampleDataTemplateAttribute> visited,
        HashSet<ExampleDataTemplateAttribute> visiting)
    {
        if (visited.Contains(attribute))
            return;

        if (!visiting.Add(attribute))
        {
            var name = attribute.MetaverseAttribute?.Name ?? attribute.ConnectedSystemObjectTypeAttribute?.Name ?? "(unknown)";
            throw new ExampleDataTemplateException($"Circular dependency detected in example data attribute generation involving '{name}'. Check the expressions and attribute dependencies for a reference cycle.");
        }

        foreach (var dependency in GetDependencies(attribute, attributesByName))
            Visit(dependency, attributesByName, ordered, visited, visiting);

        visiting.Remove(attribute);
        visited.Add(attribute);
        ordered.Add(attribute);
    }

    private static IEnumerable<ExampleDataTemplateAttribute> GetDependencies(
        ExampleDataTemplateAttribute attribute,
        Dictionary<string, ExampleDataTemplateAttribute> attributesByName)
    {
        var dependencies = new List<ExampleDataTemplateAttribute>();

        // expression mv["..."] references
        if (!string.IsNullOrEmpty(attribute.Expression))
        {
            foreach (var referencedName in MetaverseAttributeReferenceRegex()
                         .Matches(attribute.Expression)
                         .Select(match => match.Groups[1].Value))
            {
                if (attributesByName.TryGetValue(referencedName, out var referenced) && !ReferenceEquals(referenced, attribute))
                    dependencies.Add(referenced);
            }
        }

        // conditional attribute dependency (generate only when another attribute holds a given value)
        if (attribute.AttributeDependency?.MetaverseAttribute != null &&
            attributesByName.TryGetValue(attribute.AttributeDependency.MetaverseAttribute.Name, out var conditional) &&
            !ReferenceEquals(conditional, attribute))
        {
            dependencies.Add(conditional);
        }

        return dependencies;
    }
}
