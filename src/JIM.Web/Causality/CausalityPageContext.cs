// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The page-level context the Run Profile Execution Item detail page supplies to the causality
/// visualisation: the executing Connected System and Run Profile, the record (CSO) being processed,
/// and the joined Metaverse Object Type names for link building. All values are optional; legacy or
/// partially-loaded data must degrade gracefully rather than error.
/// </summary>
/// <param name="ConnectedSystemId">Id of the Connected System the run executed against.</param>
/// <param name="ConnectedSystemName">Name of the Connected System (e.g. "HR CSV Source").</param>
/// <param name="RunProfileName">Name of the executed Run Profile (e.g. "Full Synchronisation").</param>
/// <param name="CsoId">Id of the processed Connected System Object; null when it has been deleted.</param>
/// <param name="CsoDisplayName">Display name of the record (e.g. "Liam Allen").</param>
/// <param name="CsoExternalId">External id of the record (e.g. "S8-287551").</param>
/// <param name="CsoObjectTypeName">The record's object type name (e.g. "person").</param>
/// <param name="MvoTypeName">Singular Metaverse Object Type name (e.g. "Person").</param>
/// <param name="MvoTypePluralName">Plural Metaverse Object Type name (e.g. "People") for link building.</param>
public sealed record CausalityPageContext(
    int? ConnectedSystemId,
    string? ConnectedSystemName,
    string? RunProfileName,
    Guid? CsoId,
    string? CsoDisplayName,
    string? CsoExternalId,
    string? CsoObjectTypeName,
    string? MvoTypeName,
    string? MvoTypePluralName);
