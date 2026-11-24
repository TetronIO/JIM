// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Localisation;
using JIM.Models.Resources;

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
    /// <summary>
    /// Connected Systems removed.
    /// </summary>
    public int ConnectedSystemsRemoved { get; set; }

    /// <summary>
    /// Connected System Objects removed.
    /// </summary>
    public int ConnectedSystemObjectsRemoved { get; set; }

    /// <summary>
    /// Metaverse Objects removed, counting the administrators when the wipe included them.
    /// </summary>
    public int MetaverseObjectsRemoved { get; set; }

    /// <summary>
    /// Metaverse Object change records removed.
    /// </summary>
    public int MetaverseObjectChangesRemoved { get; set; }

    /// <summary>
    /// Connected System Object change records removed.
    /// </summary>
    public int ConnectedSystemObjectChangesRemoved { get; set; }

    /// <summary>
    /// Synchronisation Rules removed.
    /// </summary>
    public int SyncRulesRemoved { get; set; }

    /// <summary>
    /// Object Matching Rules removed.
    /// </summary>
    public int ObjectMatchingRulesRemoved { get; set; }

    /// <summary>
    /// Schedules removed.
    /// </summary>
    public int SchedulesRemoved { get; set; }

    /// <summary>
    /// Records of Schedule runs removed.
    /// </summary>
    public int ScheduleExecutionsRemoved { get; set; }

    /// <summary>
    /// Activities removed.
    /// </summary>
    public int ActivitiesRemoved { get; set; }

    /// <summary>
    /// Pending Exports removed.
    /// </summary>
    public int PendingExportsRemoved { get; set; }

    /// <summary>
    /// Metaverse Object Types removed; the built-in ones are kept.
    /// </summary>
    public int CustomMetaverseObjectTypesRemoved { get; set; }

    /// <summary>
    /// Metaverse Attributes removed; the built-in ones are kept.
    /// </summary>
    public int CustomMetaverseAttributesRemoved { get; set; }

    /// <summary>
    /// Roles removed; the built-in ones are kept.
    /// </summary>
    public int CustomRolesRemoved { get; set; }

    /// <summary>
    /// Connector Definitions removed; the built-in ones are kept.
    /// </summary>
    public int CustomConnectorDefinitionsRemoved { get; set; }

    /// <summary>
    /// Predefined Searches removed; the built-in ones are kept.
    /// </summary>
    public int CustomPredefinedSearchesRemoved { get; set; }

    /// <summary>
    /// Example Data Sets removed; the built-in ones are kept.
    /// </summary>
    public int CustomExampleDataSetsRemoved { get; set; }

    /// <summary>
    /// Example Data Templates removed; the built-in ones are kept.
    /// </summary>
    public int CustomExampleDataTemplatesRemoved { get; set; }

    /// <summary>
    /// API Keys removed; infrastructure keys are kept.
    /// </summary>
    public int CustomApiKeysRemoved { get; set; }

    /// <summary>
    /// Trusted Certificates removed.
    /// </summary>
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
    /// removed the breakdown is replaced with an "already empty" note. This is its British English.
    /// </summary>
    /// <param name="includeAdministrators">Whether the reset removed administrator users (true) or preserved them (false).</param>
    public string BuildResetMessage(bool includeAdministrators) =>
        BuildResetMessageDetail(includeAdministrators).ToEnglish();

    /// <summary>
    /// <see cref="BuildResetMessage"/> as a message, so the portal shows it in the reader's language: each count
    /// is pluralised by that language's own rule, and the list is lines of whole phrases rather than English
    /// fragments joined by code (#3094).
    /// </summary>
    /// <param name="includeAdministrators">Whether the reset removed administrator users (true) or preserved them (false).</param>
    public JimMessage BuildResetMessageDetail(bool includeAdministrators)
    {
        var header = includeAdministrators
            ? JimMessage.Create(MessageCatalogues.SystemReset, nameof(SystemResetMessages.HeaderAdministratorsRemoved))
            : JimMessage.Create(MessageCatalogues.SystemReset, nameof(SystemResetMessages.HeaderAdministratorsRetained),
                AdministratorsRetained.ToString(CultureInfo.InvariantCulture));

        // Administrators are conveyed by the header rather than a line: in the administrator-inclusive wipe they are
        // already counted within MetaverseObjectsRemoved, so a separate line would double-count them. Categories are in
        // a sensible reading order.
        var lines = new[]
        {
            Line(ConnectedSystemsRemoved, nameof(SystemResetMessages.RemovedConnectedSystemsOne)),
            Line(ConnectedSystemObjectsRemoved, nameof(SystemResetMessages.RemovedConnectedSystemObjectsOne)),
            Line(MetaverseObjectsRemoved, nameof(SystemResetMessages.RemovedMetaverseObjectsOne)),
            Line(MetaverseObjectChangesRemoved, nameof(SystemResetMessages.RemovedMetaverseObjectChangesOne)),
            Line(ConnectedSystemObjectChangesRemoved, nameof(SystemResetMessages.RemovedConnectedSystemObjectChangesOne)),
            Line(SyncRulesRemoved, nameof(SystemResetMessages.RemovedSynchronisationRulesOne)),
            Line(ObjectMatchingRulesRemoved, nameof(SystemResetMessages.RemovedObjectMatchingRulesOne)),
            Line(SchedulesRemoved, nameof(SystemResetMessages.RemovedSchedulesOne)),
            Line(ScheduleExecutionsRemoved, nameof(SystemResetMessages.RemovedScheduleExecutionsOne)),
            Line(ActivitiesRemoved, nameof(SystemResetMessages.RemovedActivitiesOne)),
            Line(PendingExportsRemoved, nameof(SystemResetMessages.RemovedPendingExportsOne)),
            Line(CustomMetaverseObjectTypesRemoved, nameof(SystemResetMessages.RemovedCustomMetaverseObjectTypesOne)),
            Line(CustomMetaverseAttributesRemoved, nameof(SystemResetMessages.RemovedCustomMetaverseAttributesOne)),
            Line(CustomRolesRemoved, nameof(SystemResetMessages.RemovedCustomRolesOne)),
            Line(CustomConnectorDefinitionsRemoved, nameof(SystemResetMessages.RemovedCustomConnectorDefinitionsOne)),
            Line(CustomPredefinedSearchesRemoved, nameof(SystemResetMessages.RemovedCustomPredefinedSearchesOne)),
            Line(CustomExampleDataSetsRemoved, nameof(SystemResetMessages.RemovedCustomExampleDataSetsOne)),
            Line(CustomExampleDataTemplatesRemoved, nameof(SystemResetMessages.RemovedCustomExampleDataTemplatesOne)),
            Line(CustomApiKeysRemoved, nameof(SystemResetMessages.RemovedCustomApiKeysOne)),
            Line(TrustedCertificatesRemoved, nameof(SystemResetMessages.RemovedTrustedCertificatesOne))
        }.OfType<JimMessage>().ToList();

        if (lines.Count == 0)
            return JimMessage.Create(MessageCatalogues.SystemReset, nameof(SystemResetMessages.NothingRemoved), header);

        var list = lines.Aggregate((joined, next) =>
            JimMessage.Create(MessageCatalogues.SystemReset, nameof(SystemResetMessages.Joined), joined, next));
        return JimMessage.Create(MessageCatalogues.SystemReset, nameof(SystemResetMessages.RemovedList), header, list);
    }

    private static JimMessage? Line(int count, string oneKey) =>
        count > 0 ? JimMessage.Counted(MessageCatalogues.SystemReset, oneKey, count, count) : null;
}
