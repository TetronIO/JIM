// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A value Sync Preview generated that the real synchronisation would also probe for (Unique Value Generation, #242,
/// release 3). Sync Preview is a dry run and never contacts a Connected System, so it checks JIM's own records only;
/// when the synchronisation runs, JIM also asks each of <see cref="ConnectedSystemNames"/> whether the value is
/// already in use, and generates a different value if one of them already has an account using it.
/// </summary>
public class SyncPreviewGeneratedValueProbe
{
    /// <summary>The Metaverse attribute the value was generated for.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The value the preview generated.</summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// The Connected Systems the real synchronisation would probe for the value, by name, ordered by name: those the
    /// value is exported to unchanged whose Connector probes the attribute, less any the generated mapping excludes.
    /// </summary>
    public List<string> ConnectedSystemNames { get; set; } = [];
}
