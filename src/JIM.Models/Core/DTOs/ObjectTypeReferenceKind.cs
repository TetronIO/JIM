// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The kind of configuration object that references a Metaverse Object Type, used to categorise the entries in an
/// <see cref="ObjectTypeDeletionImpact"/> so the UI and PowerShell can group and describe what a deletion affects.
/// </summary>
public enum ObjectTypeReferenceKind
{
    /// <summary>
    /// A Synchronisation Rule that targets the Object Type. A hard block: the rule must be removed before the type can
    /// be deleted, because deleting the type would otherwise cascade-delete the entire rule.
    /// </summary>
    SynchronisationRule,

    /// <summary>
    /// A Predefined Search scoped to the Object Type. Cascade-removed with the type on confirmation.
    /// </summary>
    PredefinedSearch,

    /// <summary>
    /// An Example Data Template that populates the Object Type. Cascade-removed with the type on confirmation.
    /// </summary>
    ExampleDataTemplate,

    /// <summary>
    /// A custom Metaverse Attribute binding (the join between the type and an attribute). The attribute itself is not
    /// deleted; only the binding row is cascade-removed with the type on confirmation.
    /// </summary>
    AttributeBinding
}
