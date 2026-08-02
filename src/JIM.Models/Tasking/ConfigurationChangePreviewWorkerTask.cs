// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Tasking;

/// <summary>
/// Hands a configuration change preview to JIM.Worker, for proposals whose estimated population is large enough
/// that evaluating it in JIM.Web's process would tie up a web host for minutes (#827).
///
/// Unlike every other worker task, this one does **not** create its Activity: the preview's Activity already exists,
/// because stage 1 validation ran in the request path and recorded its result there. The task attaches to that
/// Activity so both halves of the preview appear as one thing.
/// </summary>
public class ConfigurationChangePreviewWorkerTask : WorkerTask
{
    /// <summary>The surface being previewed; selects the adapter that will evaluate it.</summary>
    public ConfigurationChangePreviewSurface Surface { get; set; } = ConfigurationChangePreviewSurface.NotSet;

    /// <summary>The integer identifier of the configuration object being changed, for surfaces keyed that way.</summary>
    public int? TargetId { get; set; }

    /// <summary>The Guid identifier, for surfaces keyed that way.</summary>
    public Guid? TargetGuidId { get; set; }

    /// <summary>The object's name, carried so the worker need not re-read an object to log about it.</summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// The proposed configuration, serialised as the adapter's declared proposal type. This is the only reason
    /// that type exists: a proposal is an unsaved object living in the caller's memory, and crossing a process
    /// boundary is the one thing it cannot do on its own.
    ///
    /// The payload carries no type name. The worker resolves the type from the adapter registered for
    /// <see cref="Surface"/>, so a tampered row can only ever deserialise into the type that surface expects.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string ProposedConfigurationPayload { get; set; } = null!;
}
