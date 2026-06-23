// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Utility;

/// <summary>
/// Summary of what a factory reset removed from the database.
/// </summary>
/// <remarks>
/// Counts are captured before each table is wiped so callers can report on the scale of the
/// operation. Preserved rows (built-in metaverse attributes, built-in roles, the seeded
/// service settings record, infrastructure API keys, the EF migration history) are not
/// represented here.
/// <para>
/// The counts cover the top-level entities and history an operator cares about. Deliberately
/// out of scope (and therefore not counted): transient/internal worker state (worker tasks,
/// deferred references) and cascade child rows (attribute values, sync-rule mappings, run
/// profiles, partitions, containers) that are removed as a side effect of deleting their parent.
/// </para>
/// </remarks>
public class SystemResetResult
{
    public int ConnectedSystemsRemoved { get; set; }
    public int ConnectedSystemObjectsRemoved { get; set; }
    public int MetaverseObjectsRemoved { get; set; }
    public int MetaverseObjectChangesRemoved { get; set; }
    public int ConnectedSystemObjectChangesRemoved { get; set; }
    public int SyncRulesRemoved { get; set; }
    public int ObjectMatchingRulesRemoved { get; set; }
    public int SchedulesRemoved { get; set; }
    public int ScheduleExecutionsRemoved { get; set; }
    public int ActivitiesRemoved { get; set; }
    public int PendingExportsRemoved { get; set; }
    public int CustomMetaverseObjectTypesRemoved { get; set; }
    public int CustomMetaverseAttributesRemoved { get; set; }
    public int CustomRolesRemoved { get; set; }
    public int CustomConnectorDefinitionsRemoved { get; set; }
    public int CustomPredefinedSearchesRemoved { get; set; }
    public int CustomExampleDataSetsRemoved { get; set; }
    public int CustomExampleDataTemplatesRemoved { get; set; }
    public int CustomApiKeysRemoved { get; set; }
    public int TrustedCertificatesRemoved { get; set; }

    /// <summary>
    /// Number of Metaverse Objects holding the built-in Administrator role that were preserved by the reset.
    /// Zero when the reset was performed with administrators included in the wipe.
    /// </summary>
    public int AdministratorsRetained { get; set; }

    /// <summary>
    /// Number of Metaverse Objects holding the built-in Administrator role that were removed by the reset.
    /// Non-zero only when the reset was performed with administrators included in the wipe.
    /// </summary>
    public int AdministratorsRemoved { get; set; }

    /// <summary>
    /// Builds the human-readable summary recorded on the reset Activity: a header stating the
    /// administrator outcome, followed by a bulleted breakdown of every object type that had a
    /// non-zero removal count. Categories with a zero count are omitted; when nothing at all was
    /// removed the breakdown is replaced with an "already empty" note.
    /// </summary>
    /// <param name="includeAdministrators">Whether the reset removed administrator users (true) or preserved them (false).</param>
    public string BuildResetMessage(bool includeAdministrators)
    {
        var header = includeAdministrators
            ? "Factory reset completed (administrators removed)."
            : $"Factory reset completed (administrators retained: {AdministratorsRetained}).";

        // (count, singular, plural) in a sensible reading order. Administrators are conveyed by the
        // header rather than a bullet: in the administrator-inclusive wipe they are already counted
        // within MetaverseObjectsRemoved, so a separate bullet would double-count them.
        var categories = new (int Count, string Singular, string Plural)[]
        {
            (ConnectedSystemsRemoved, "Connected System", "Connected Systems"),
            (ConnectedSystemObjectsRemoved, "Connected System Object", "Connected System Objects"),
            (MetaverseObjectsRemoved, "Metaverse Object", "Metaverse Objects"),
            (MetaverseObjectChangesRemoved, "Metaverse Object change record", "Metaverse Object change records"),
            (ConnectedSystemObjectChangesRemoved, "Connected System Object change record", "Connected System Object change records"),
            (SyncRulesRemoved, "Synchronisation Rule", "Synchronisation Rules"),
            (ObjectMatchingRulesRemoved, "Object Matching Rule", "Object Matching Rules"),
            (SchedulesRemoved, "schedule", "schedules"),
            (ScheduleExecutionsRemoved, "schedule execution", "schedule executions"),
            (ActivitiesRemoved, "activity", "activities"),
            (PendingExportsRemoved, "Pending Export", "Pending Exports"),
            (CustomMetaverseObjectTypesRemoved, "custom Metaverse Object Type", "custom Metaverse Object Types"),
            (CustomMetaverseAttributesRemoved, "custom metaverse attribute", "custom metaverse attributes"),
            (CustomRolesRemoved, "custom role", "custom roles"),
            (CustomConnectorDefinitionsRemoved, "custom connector definition", "custom connector definitions"),
            (CustomPredefinedSearchesRemoved, "custom predefined search", "custom predefined searches"),
            (CustomExampleDataSetsRemoved, "custom example data set", "custom example data sets"),
            (CustomExampleDataTemplatesRemoved, "custom example data template", "custom example data templates"),
            (CustomApiKeysRemoved, "custom API key", "custom API keys"),
            (TrustedCertificatesRemoved, "trusted certificate", "trusted certificates")
        };

        var bullets = categories
            .Where(c => c.Count > 0)
            .Select(c => $"• {c.Count.ToString("N0", CultureInfo.InvariantCulture)} {(c.Count == 1 ? c.Singular : c.Plural)}")
            .ToList();

        if (bullets.Count == 0)
            return $"{header}{Environment.NewLine}{Environment.NewLine}Nothing was removed; the system was already empty.";

        return $"{header}{Environment.NewLine}{Environment.NewLine}Removed:{Environment.NewLine}{string.Join(Environment.NewLine, bullets)}";
    }
}
