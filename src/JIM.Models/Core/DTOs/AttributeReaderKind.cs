// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// What reads an attribute, where removing or renaming the attribute would leave it reading nothing.
/// </summary>
public enum AttributeReaderKind
{
    /// <summary>
    /// A Synchronisation Rule's Scoping Criterion, comparing the attribute directly or reading it in its Expression.
    /// </summary>
    ScopingCriterion = 0,

    /// <summary>
    /// An Attribute Flow whose source Expression reads the attribute.
    /// </summary>
    AttributeFlowExpression = 1,

    /// <summary>
    /// An Object Matching Rule whose source Expression reads the attribute.
    /// </summary>
    ObjectMatchingRuleExpression = 2,

    /// <summary>
    /// An Example Data Template attribute whose Expression reads the attribute.
    /// </summary>
    ExampleDataTemplateExpression = 3
}
