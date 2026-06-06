// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Utility;

/// <summary>
/// Summary of what a factory reset removed from the database.
/// </summary>
/// <remarks>
/// Counts are captured before each table is wiped so callers can report on the scale of the
/// operation. Preserved rows (built-in metaverse attributes, built-in roles, the seeded
/// service settings record, infrastructure API keys, the EF migration history) are not
/// represented here.
/// </remarks>
public class SystemResetResult
{
    public int ConnectedSystemsRemoved { get; set; }
    public int ConnectedSystemObjectsRemoved { get; set; }
    public int MetaverseObjectsRemoved { get; set; }
    public int SyncRulesRemoved { get; set; }
    public int SchedulesRemoved { get; set; }
    public int ActivitiesRemoved { get; set; }
    public int PendingExportsRemoved { get; set; }
    public int CustomMetaverseObjectTypesRemoved { get; set; }
    public int CustomMetaverseAttributesRemoved { get; set; }
    public int CustomRolesRemoved { get; set; }
    public int CustomConnectorDefinitionsRemoved { get; set; }
    public int CustomPredefinedSearchesRemoved { get; set; }
    public int CustomExampleDataSetsRemoved { get; set; }
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
}
