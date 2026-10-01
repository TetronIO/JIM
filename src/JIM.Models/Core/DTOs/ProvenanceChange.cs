// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The recorded change behind a value: when, which Activity (and execution item), and who or what initiated it.
/// </summary>
public class ProvenanceChange
{
    public DateTime ChangeTime { get; set; }

    public Guid? ActivityId { get; set; }

    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>Human-readable description of the Activity, e.g. the Run Profile name ("Delta Synchronisation").</summary>
    public string? ActivityDescription { get; set; }

    public string? InitiatedByName { get; set; }

    public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }
}
