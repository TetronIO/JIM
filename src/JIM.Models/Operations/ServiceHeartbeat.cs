// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Operations;

/// <summary>
/// One running instance of a <see cref="JimService"/>, as it last described itself. Each service writes its own row
/// every few seconds from the same place it touches its container health-check file, so the row answers the
/// question the health-check file answers for the orchestrator, but for the administrator in the portal: is this
/// service alive, what is it doing, and since when.
/// </summary>
/// <remarks>
/// Rows are keyed on (<see cref="Service"/>, <see cref="InstanceId"/>). A restarted process gets a new instance id
/// and therefore a new row; the old row stops moving and is pruned by the next start of the same service once it is
/// a day old. Every timestamp is UTC, as everywhere in JIM.
/// </remarks>
public class ServiceHeartbeat
{
    public int Id { get; set; }

    /// <summary>
    /// Which service this instance is running.
    /// </summary>
    public JimService Service { get; set; }

    /// <summary>
    /// Identifies one process: the host name plus a short per-process id, so two Workers on one host, or one Worker
    /// restarted on the same host, are told apart.
    /// </summary>
    [MaxLength(200)]
    public string InstanceId { get; set; } = null!;

    /// <summary>
    /// The machine (or container) the instance is running on, as it reports itself.
    /// </summary>
    [MaxLength(200)]
    public string HostName { get; set; } = null!;

    /// <summary>
    /// The JIM version the instance is running. Lets an administrator spot a Worker that was not upgraded with the
    /// web tier, which is the first thing to check when behaviour disagrees with the release notes.
    /// </summary>
    [MaxLength(100)]
    public string Version { get; set; } = null!;

    /// <summary>
    /// When the process started (UTC).
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the instance last wrote this row (UTC). Health is derived from how long ago this was.
    /// </summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// A short description of what the instance is doing right now (for example "Full Import: Corporate Directory"),
    /// or null when it is idle.
    /// </summary>
    [MaxLength(500)]
    public string? CurrentWork { get; set; }

    /// <summary>
    /// When the current work began (UTC); null when idle.
    /// </summary>
    public DateTime? CurrentWorkStartedAt { get; set; }

    /// <summary>
    /// When the current work last demonstrably moved forward (UTC). Null when idle, and null while the service has
    /// no way to tell progress from mere liveness; a health reader must only judge "no progress" when this is set.
    /// </summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>
    /// Free text the service wants an administrator to see beside its state, such as queue counts or the reason it
    /// is waiting. Null when there is nothing worth saying.
    /// </summary>
    [MaxLength(1000)]
    public string? Detail { get; set; }
}
