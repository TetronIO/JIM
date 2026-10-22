// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.ExampleData;

namespace JIM.Web.Models;

/// <summary>
/// The edits the portal's Data Generation Template editor makes (#1466), each applied to a freshly loaded template that
/// is then saved whole. Rows are found by id rather than by position: a save replaces every row (new ids), so an id
/// the page holds that the fresh template lacks means the template was saved elsewhere meanwhile, and the edit fails
/// with <see cref="ExampleDataTemplateChangedException"/> rather than landing on the wrong row.
/// </summary>
public static class ExampleDataTemplateGraphEdits
{
    /// <summary>Replaces the Template Attribute with <paramref name="templateAttributeId"/> in place, or appends when it is null.</summary>
    public static void SaveAttribute(ExampleDataTemplate template, int objectTypeId, int? templateAttributeId, ExampleDataTemplateAttribute attribute)
    {
        var attributes = FindObjectType(template, objectTypeId).TemplateAttributes;
        if (templateAttributeId == null)
        {
            attributes.Add(attribute);
            return;
        }

        var index = attributes.FindIndex(a => a.Id == templateAttributeId.Value);
        if (index < 0)
            throw new ExampleDataTemplateChangedException();

        attributes[index] = attribute;
    }

    public static void RemoveAttribute(ExampleDataTemplate template, int objectTypeId, int templateAttributeId)
    {
        if (FindObjectType(template, objectTypeId).TemplateAttributes.RemoveAll(a => a.Id == templateAttributeId) == 0)
            throw new ExampleDataTemplateChangedException();
    }

    /// <summary>
    /// The names of the Object Type's attributes generated only when a condition on <paramref name="metaverseAttribute"/>
    /// holds. Without that attribute's values such a condition is never met, so removing it stops them being generated.
    /// </summary>
    public static IReadOnlyList<string> AttributesConditionalOn(ExampleDataObjectType objectType, MetaverseAttribute metaverseAttribute) =>
        objectType.TemplateAttributes
            .Where(a => a.AttributeDependency?.MetaverseAttribute.Id == metaverseAttribute.Id && a.MetaverseAttribute != null)
            .Select(a => a.MetaverseAttribute!.Name)
            .ToList();

    public static void AddObjectType(ExampleDataTemplate template, MetaverseObjectType metaverseObjectType, int objectsToCreate) =>
        template.ObjectTypes.Add(new ExampleDataObjectType { MetaverseObjectType = metaverseObjectType, ObjectsToCreate = objectsToCreate });

    public static void SetObjectsToCreate(ExampleDataTemplate template, int objectTypeId, int objectsToCreate) =>
        FindObjectType(template, objectTypeId).ObjectsToCreate = objectsToCreate;

    /// <exception cref="InvalidOperationException">It is the template's only Object Type; a template needs at least one.</exception>
    public static void RemoveObjectType(ExampleDataTemplate template, int objectTypeId)
    {
        var objectType = FindObjectType(template, objectTypeId);
        if (template.ObjectTypes.Count == 1)
            throw new InvalidOperationException("A Data Generation Template needs at least one Metaverse Object Type, so its last one cannot be removed.");

        template.ObjectTypes.Remove(objectType);
    }

    private static ExampleDataObjectType FindObjectType(ExampleDataTemplate template, int objectTypeId) =>
        template.ObjectTypes.SingleOrDefault(ot => ot.Id == objectTypeId) ?? throw new ExampleDataTemplateChangedException();
}
