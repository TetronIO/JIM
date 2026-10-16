// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One piece of configuration that reads an attribute: a Scoping Criterion, or an Expression in an Attribute Flow,
/// Object Matching Rule or Example Data Template. JIM refuses to delete, unassign or rename an attribute while
/// something reads it, and names each reader so the administrator can change it first.
/// </summary>
public class AttributeReader
{
    /// <summary>
    /// What kind of configuration reads the attribute.
    /// </summary>
    public AttributeReaderKind Kind { get; set; }

    /// <summary>
    /// The id of the reading item: the Scoping Criterion, the Attribute Flow source, the Object Matching Rule, or the
    /// Example Data Template attribute.
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// True when the attribute is read inside an Expression, by name; false when a Scoping Criterion compares the
    /// attribute directly.
    /// </summary>
    public bool ViaExpression { get; set; }

    /// <summary>
    /// The Expression that reads the attribute, when <see cref="ViaExpression"/> is true; otherwise null.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// The id of the Synchronisation Rule the reader belongs to; null for an Example Data Template, or for an Object
    /// Matching Rule defined on a Connected System Object Type.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The name of the Synchronisation Rule the reader belongs to, when it belongs to one.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The id of the Example Data Template the reader belongs to, when it belongs to one.
    /// </summary>
    public int? ExampleDataTemplateId { get; set; }

    /// <summary>
    /// The name of the Example Data Template the reader belongs to, when it belongs to one.
    /// </summary>
    public string? ExampleDataTemplateName { get; set; }

    /// <summary>
    /// A sentence naming the reader and where it lives, for messages and the portal.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
