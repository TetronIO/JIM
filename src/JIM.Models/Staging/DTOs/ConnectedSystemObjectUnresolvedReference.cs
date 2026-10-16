// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One reference value on a Connected System Object that could not be resolved to another Connected System Object,
/// with its likely cause and what to do about it (#959). The same shape serves the portal, the REST API and
/// PowerShell, so all three say the same thing.
/// </summary>
public class ConnectedSystemObjectUnresolvedReference
{
    /// <summary>The attribute the value is on.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The reference value as imported, for example a Distinguished Name.</summary>
    public string Value { get; set; } = null!;

    /// <summary>Why the value most likely could not be resolved, judged against the current Container Scope.</summary>
    public UnresolvedReferenceCause Cause { get; set; }

    /// <summary>Whether the fix lies in JIM's configuration or in the Connected System.</summary>
    public UnresolvedReferenceFixLocation FixLocation { get; set; }

    /// <summary>
    /// The Container the cause concerns: the one to select, the one excluding the value, or the one the value sits in
    /// while in scope. Null where the cause concerns no Container.
    /// </summary>
    public string? ContainerExternalId { get; set; }

    /// <summary>
    /// The partition the cause concerns: the one to select, for a value in a partition that is not selected, or the one
    /// holding a value that no Container JIM knows of holds. Null otherwise.
    /// </summary>
    public string? PartitionExternalId { get; set; }

    /// <summary>The Object Types an ambiguous value matches; empty for every other cause.</summary>
    public List<string> CandidateObjectTypeNames { get; set; } = [];

    /// <summary>
    /// What is wrong, in words: why the value most likely did not resolve. Names the Container or partition concerned
    /// exactly as <see cref="ContainerExternalId"/> or <see cref="PartitionExternalId"/> holds it, where there is one.
    /// </summary>
    public string Finding { get; set; } = null!;

    /// <summary>What to do about it, in words.</summary>
    public string Remedy { get; set; } = null!;
}
