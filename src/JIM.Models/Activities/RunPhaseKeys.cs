// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// The keys of the phases JIM itself moves through during a Run Profile execution. The worker
/// enters phases by these constants and <see cref="RunProfilePhaseCatalogue"/> declares them;
/// RunProfilePhaseCatalogueTests fails if the two drift apart.
/// </summary>
/// <remarks>
/// Keys are persisted on <see cref="ActivityPhase"/> rows and so are part of the data model:
/// renaming one leaves historic Activities referring to a key that no longer exists. Change the
/// <see cref="RunProfilePhase.Name"/> when the wording needs to improve; leave the key alone.
/// </remarks>
public static class RunPhaseKeys
{
    // ─── Import (Full and Delta) ───

    /// <summary>Opening the connection to the Connected System. Call-based Connectors only.</summary>
    public const string ImportConnect = "import.connect";

    /// <summary>Fetching objects from the Connected System, and processing each page as it arrives. Hosts the Connector's own phases.</summary>
    public const string ImportFetch = "import.fetch";

    /// <summary>Working out which Connected System Objects are absent from the source and marking them deleted. Full Imports only.</summary>
    public const string ImportDeletions = "import.deletions";

    /// <summary>Turning unresolved reference values into links between Connected System Objects.</summary>
    public const string ImportResolveReferences = "import.references";

    /// <summary>Persisting the imported Connected System Objects and their change records.</summary>
    public const string ImportSave = "import.save";

    /// <summary>Confirming Pending Exports against the values that came back from the Connected System.</summary>
    public const string ImportReconcile = "import.reconcile";

    /// <summary>Recording the per-object Run Profile Execution Items that the Activity's results are read from.</summary>
    public const string ImportRecordResults = "import.record";

    // ─── Synchronisation (Full and Delta) ───

    /// <summary>Loading Synchronisation Rules and counting the work before synchronisation starts.</summary>
    public const string SyncPrepare = "sync.prepare";

    /// <summary>Evaluating each Connected System Object: matching, joining, projecting and flowing attributes.</summary>
    public const string SyncProcessObjects = "sync.process";

    /// <summary>Resolving references whose target object was only created on a later page of the run.</summary>
    public const string SyncResolveCrossPageReferences = "sync.crosspagereferences";

    // ─── Export ───

    /// <summary>Loading Pending Exports and counting the work before the Connector is called.</summary>
    public const string ExportPrepare = "export.prepare";

    /// <summary>Writing changes to the Connected System. Hosts the Connector's own phases.</summary>
    public const string ExportExecute = "export.execute";

    /// <summary>Resolving references recorded in export change history once every exported object exists.</summary>
    public const string ExportResolveReferences = "export.references";
}
