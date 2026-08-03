// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Models.Sync;

/// <summary>
/// The decision-time snapshot of the deletion policy facts that produced a Metaverse Object deletion
/// decision (#119). Captured when the decision is made and serialised onto the decision record
/// (<c>ActivityRunProfileExecutionItem.DeletionPolicySnapshotJson</c>, and
/// <c>MetaverseObject.DeletionPolicySnapshotJson</c> for grace period deletions so housekeeping can carry
/// it onto the final deletion record). Keeping the facts on the record follows the established event-time
/// denormalisation pattern (DeletionInitiatedByName, CreatedByName): the decision stays explainable after
/// an administrator edits the object type's deletion configuration.
/// </summary>
public class MvoDeletionPolicySnapshot
{
    // Follows the established stored-JSON conventions (ConfigurationSnapshotService, ActivityErrorDetail):
    // camelCase properties, string enum values so the stored document stays self-describing, nulls omitted.
    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    /// <summary>
    /// The deletion rule in force at decision time.
    /// </summary>
    public MetaverseObjectDeletionRule DeletionRule { get; set; }

    /// <summary>
    /// The trigger mode in force at decision time (only meaningful for
    /// <see cref="MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected"/>).
    /// </summary>
    public AuthoritativeSourceTriggerMode TriggerMode { get; set; }

    /// <summary>
    /// The ids of the authoritative source Connected Systems selected as deletion triggers at decision time.
    /// </summary>
    public List<int> SelectedSourceSystemIds { get; set; } = new();

    /// <summary>
    /// The display names of the selected trigger sources at decision time, in the same order as
    /// <see cref="SelectedSourceSystemIds"/>. Name snapshots survive deletion of the systems themselves.
    /// </summary>
    public List<string> SelectedSourceSystemNames { get; set; } = new();

    /// <summary>
    /// The grace period configured at decision time. Null or zero means deletion occurs immediately when
    /// the trigger condition is met.
    /// </summary>
    public TimeSpan? GracePeriod { get; set; }

    /// <summary>
    /// When the scheduled deletion becomes due (decision time plus the grace period), in UTC. Null for
    /// decisions that did not schedule a deletion. Recorded rather than derived so the due date stays
    /// accurate after the grace period is reconfigured (#119).
    /// </summary>
    public DateTime? DeletionEligibleDate { get; set; }

    /// <summary>
    /// The Connected System whose disconnection triggered the evaluation.
    /// </summary>
    public int? TriggeringSystemId { get; set; }

    /// <summary>
    /// The display name of the triggering Connected System at decision time.
    /// </summary>
    public string? TriggeringSystemName { get; set; }

    /// <summary>
    /// The ids of the selected source systems that still held a joined Connected System Object at decision
    /// time (after the triggering disconnection). Drives mode-vocabulary decision reasons such as
    /// "1 of 2 sources remains connected".
    /// </summary>
    public List<int> RemainingConnectedSourceSystemIds { get; set; } = new();

    /// <summary>
    /// The display names of the still-connected source systems at decision time, in the same order as
    /// <see cref="RemainingConnectedSourceSystemIds"/>.
    /// </summary>
    public List<string> RemainingConnectedSourceSystemNames { get; set; } = new();

    /// <summary>
    /// Serialises this snapshot to the JSON document stored on the decision record.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerialiserOptions);

    /// <summary>
    /// Reads back a snapshot written by <see cref="ToJson"/>. Returns null when the value is absent or no
    /// longer parses, so a rendering caller can fall back to current configuration rather than failing.
    /// </summary>
    public static MvoDeletionPolicySnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MvoDeletionPolicySnapshot>(json, SerialiserOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
