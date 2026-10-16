// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One next step and how many unresolved reference values it addresses, on one Connected System Object or across a
/// Connected System (#959): a change to JIM's configuration, or a cause to resolve in the Connected System.
/// </summary>
public class UnresolvedReferenceFix
{
    /// <summary>The cause the change addresses.</summary>
    public UnresolvedReferenceCause Cause { get; set; }

    /// <summary>Whether the step lies in JIM's configuration or in the Connected System.</summary>
    public UnresolvedReferenceFixLocation FixLocation { get; set; }

    /// <summary>The Container concerned, where the cause concerns one.</summary>
    public string? ContainerExternalId { get; set; }

    /// <summary>The partition concerned, where the cause concerns one.</summary>
    public string? PartitionExternalId { get; set; }

    /// <summary>How many values the step addresses.</summary>
    public int Count { get; set; }

    /// <summary>
    /// How many Connected System Objects hold the values the step addresses, counting each object once. Set in a
    /// Connected System's summary; null in one object's, where every step is about that object.
    /// </summary>
    public int? ObjectCount { get; set; }

    /// <summary>
    /// What is wrong, in words. Names the Container or partition concerned exactly as <see cref="ContainerExternalId"/> or
    /// <see cref="PartitionExternalId"/> holds it, where there is one.
    /// </summary>
    public string Finding { get; set; } = null!;

    /// <summary>What to do about it, in words.</summary>
    public string Remedy { get; set; } = null!;
}
