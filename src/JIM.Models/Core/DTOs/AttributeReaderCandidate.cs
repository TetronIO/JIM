// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One configuration item that may read an attribute, flattened from its owner so the reader index can be built in
/// one pass: every Scoping Criterion, and every Attribute Flow source, Object Matching Rule source and Example Data
/// Template attribute that carries an Expression.
/// </summary>
public class AttributeReaderCandidate
{
    /// <summary>
    /// What kind of configuration this is.
    /// </summary>
    public AttributeReaderKind Kind { get; set; }

    /// <summary>
    /// The id of the item (see <see cref="AttributeReader.ItemId"/>).
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// The owning Synchronisation Rule's id, when there is one.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The owning Synchronisation Rule's name, when there is one.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The owning Example Data Template's id, when there is one.
    /// </summary>
    public int? ExampleDataTemplateId { get; set; }

    /// <summary>
    /// The owning Example Data Template's name, when there is one.
    /// </summary>
    public string? ExampleDataTemplateName { get; set; }

    /// <summary>
    /// The Connected System Object Type whose attributes the item can read, when it has one.
    /// </summary>
    public int? ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The name of that Connected System Object Type, when it has one.
    /// </summary>
    public string? ConnectedSystemObjectTypeName { get; set; }

    /// <summary>
    /// The Metaverse Object Type whose attributes the item can read, when it has one.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The Metaverse Attribute a Scoping Criterion compares directly, when it compares one.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The Connected System attribute a Scoping Criterion compares directly, when it compares one.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The name of the attribute a Scoping Criterion compares directly, when it compares one.
    /// </summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// The Expression the item evaluates, when it has one.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// The name of the attribute the item writes: an Attribute Flow's target, or an Example Data Template
    /// attribute's Metaverse Attribute. Null where the item writes nothing.
    /// </summary>
    public string? TargetName { get; set; }
}
