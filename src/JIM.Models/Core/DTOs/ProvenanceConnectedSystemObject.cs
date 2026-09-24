// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// A Connected System Object named in provenance: enough to render an object chip and link to it.
/// </summary>
public class ProvenanceConnectedSystemObject
{
    public Guid Id { get; set; }

    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = null!;

    public string TypeName { get; set; } = null!;

    public string? DisplayName { get; set; }

    public string? ExternalId { get; set; }
}
