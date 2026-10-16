// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Supplies streamed Metaverse Objects' values with their attribute entities, for previews that evaluate an export
/// rule's expression criteria against them. The stream carries each value's attribute id, which an attribute
/// criterion reads; an expression reads the object by attribute name, and without the entity a held value reads as
/// no value, which decides the object's scope on a value it does not have.
/// </summary>
internal static class PreviewMetaverseObjectAttributes
{
    /// <summary>
    /// The attributes of the rules' Metaverse Object Types by id, when any of <paramref name="rules"/> has an
    /// expression criterion; otherwise null, and nothing needs attaching.
    /// </summary>
    public static IReadOnlyDictionary<int, MetaverseAttribute>? WhenReadByName(params SyncRule[] rules) =>
        rules.Any(ScopingEvaluationServer.HasExpressionCriteria)
            ? rules
                .SelectMany(rule => rule.MetaverseObjectType?.Attributes ?? [])
                .DistinctBy(attribute => attribute.Id)
                .ToDictionary(attribute => attribute.Id)
            : null;

    /// <summary>
    /// Gives each of <paramref name="mvo"/>'s values its attribute entity from <paramref name="attributesById"/>, where
    /// the stream left it unset. The stream's objects are untracked copies, so nothing is written back.
    /// </summary>
    public static void Attach(MetaverseObject mvo, IReadOnlyDictionary<int, MetaverseAttribute>? attributesById)
    {
        if (attributesById == null)
            return;

        var unattached = mvo.AttributeValues
            .Where(value => value.Attribute == null)
            .Select(value => (Value: value, Attribute: attributesById.GetValueOrDefault(value.AttributeId)))
            .Where(pair => pair.Attribute != null);
        foreach (var (value, attribute) in unattached)
            value.Attribute = attribute!;
    }
}
