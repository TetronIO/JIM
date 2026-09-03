// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Interfaces;
using JIM.Models.Transactional;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace JIM.Models.Staging;

public class ConnectedSystem : IAuditable
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Please provide a name for the Connected System")]
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;
    public ActivityInitiatorType CreatedByType { get; set; }
    public Guid? CreatedById { get; set; }
    public string? CreatedByName { get; set; }

    public DateTime? LastUpdated { get; set; }
    public ActivityInitiatorType LastUpdatedByType { get; set; }
    public Guid? LastUpdatedById { get; set; }
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The operational status of the Connected System.
    /// Used to block operations during deletion.
    /// </summary>
    public ConnectedSystemStatus Status { get; set; } = ConnectedSystemStatus.Active;

    public List<ConnectedSystemRunProfile>? RunProfiles { get; set; } = new();

    public List<ConnectedSystemObject> Objects { get; set; } = new();

    public List<ConnectedSystemObjectType>? ObjectTypes { get; set; } = new();

    public List<PendingExport> PendingExports { get; set; } = null!;

    public int ConnectorDefinitionId { get; set; }

    public ConnectorDefinition ConnectorDefinition { get; set; } = null!;

    public List<ConnectedSystemSettingValue> SettingValues { get; set; } = new();

    /// <summary>
    /// We track whether setting values have been validated by the Connector so that we can prevent the user from navigating to configuration phases that are dependent upon valid setting values.
    /// When a Connected System is created, this will be false as there are no values supplied yet.
    /// When any setting values are changed by the user, this will be toggled to false until the settings are validated.
    /// </summary>
    public bool SettingValuesValid { get; set; }

    /// <summary>
    /// If the Connector implements partitions, then at least one partition is required, and containers may reside under those, if supported by the Connector.
    /// Note: Partitions don't have to support containers, but it's common that they do, i.e. with LDAP-based Connectors.
    /// </summary>
    public List<ConnectedSystemPartition>? Partitions { get; set; }

    /// <summary>
    /// Information that connector developers want to have persisted between synchronisation runs can be stored here.
    /// This is to suppose use-cases such as needing to store the last change id for an LDAP sytem, to enable delta imports.
    /// </summary>
    public string? PersistedConnectorData { get; set; }

    /// <summary>
    /// The password policy JIM discovered on this Connected System, where the Connector is able to read one.
    /// Null when the Connector cannot discover policies, or when nothing has been discovered yet.
    /// <para>
    /// Used to pre-fill initial password generation so an administrator does not have to retype rules the target
    /// already enforces. Refreshed whenever the schema is imported.
    /// </para>
    /// </summary>
    public ConnectedSystemPasswordPolicy? PasswordPolicy { get; set; }

    /// <summary>
    /// Whether, and how, this Connected System receives synchronised passwords (#1119). Null means Password
    /// Synchronisation has never been configured here, which is where every system starts and stays until an
    /// administrator decides otherwise.
    /// <para>
    /// Distinct from <see cref="PasswordPolicy"/>, which is what JIM discovered about the target's own rules.
    /// This is what JIM has been told to do.
    /// </para>
    /// </summary>
    public ConnectedSystemPasswordSynchronisation? PasswordSynchronisation { get; set; }

    /// <summary>
    /// Determines where Object Matching Rules are configured for this Connected System.
    /// ConnectedSystem (default): Rules are defined per object type and shared across Synchronisation Rules.
    /// SyncRule: Rules are defined per Synchronisation Rule for advanced scenarios.
    /// </summary>
    public ObjectMatchingRuleMode ObjectMatchingRuleMode { get; set; } = ObjectMatchingRuleMode.ConnectedSystem;

    /// <summary>
    /// Controls how an import-time reference attribute value that cannot be resolved to a Connected System Object is
    /// treated for this Connected System. Error (default) marks the affected object's Run Profile Execution Item as
    /// errored; Warn downgrades to an Activity-level warning summarising the unresolved count; Ignore suppresses
    /// both, logging the unresolved references only. In all three modes the unresolved value remains stored on the
    /// Connected System Object.
    /// </summary>
    public UnresolvedReferenceHandling UnresolvedReferenceHandling { get; set; } = UnresolvedReferenceHandling.Error;

    /// <summary>
    /// Timestamp of when the last synchronisation (full or delta) completed successfully.
    /// Used by delta sync to determine which CSOs have been modified since the last run,
    /// and by full sync to identify unchanged CSOs that can skip attribute processing.
    /// </summary>
    public DateTime? LastSyncCompletedAt { get; set; }

    /// <summary>
    /// The start instant of the last successfully completed Full Synchronisation, i.e. the moment up to which
    /// Synchronisation Rule configuration is known to have been applied to every object of this system.
    /// Full Synchronisation compares the newest rule/mapping configuration change against this to decide whether
    /// the unchanged-object optimisation must be disabled for the run (a configuration change must reach every
    /// object, not just objects whose source data changed). Distinct from <see cref="LastSyncCompletedAt"/>,
    /// which ANY completed synchronisation advances and which tracks source data staleness only: a no-change
    /// Delta Synchronisation advances that watermark without applying configuration, so it must not advance this.
    /// The run's START time is recorded (not its completion) so a configuration change made mid-run is still
    /// detected as newer and re-applied by the next Full Synchronisation.
    /// </summary>
    public DateTime? ConfigurationLastFullyAppliedAt { get; set; }

    /// <summary>
    /// Maximum number of export batches to process concurrently.
    /// Only applicable when the connector supports parallel export (SupportsParallelExport).
    /// Null or 1 means sequential processing (default). Higher values enable parallel batch export
    /// with separate DbContext and connector instances per batch.
    /// </summary>
    public int? MaxExportParallelism { get; set; }

    /// <summary>
    /// How long an account provisioned into this Connected System stays owed an initial password before JIM
    /// records an expiry and stops trying. Null follows JIM's default of seven days.
    /// <para>
    /// Held per Connected System because the thing it has to outlast is that system being unavailable, and how
    /// long that lasts is a property of the system rather than of the deployment. A directory taken out of
    /// service for a fortnight expires every account provisioned against it under the default; raising the value
    /// here beforehand is what prevents that, and it should not raise it for every other system too.
    /// </para>
    /// <para>
    /// Password Synchronisation (#1119) reads the same value for its queued password changes rather than adding a
    /// second window beside it. The question both are asking is identical, "how long can this system be
    /// unavailable before JIM stops trying", and the answer is a property of the system either way. The name
    /// predates the second use.
    /// </para>
    /// </summary>
    public TimeSpan? InitialPasswordTimeToLive { get; set; }

    /// <summary>
    /// Whether JIM must refuse to send a password to this Connected System over a connection it cannot confirm
    /// is encrypted. Off by default.
    /// <para>
    /// Held on the Connected System rather than on any one feature's configuration, because it governs every
    /// password JIM sends here: the initial password on an account it provisions, a password an administrator
    /// sets by hand, and a synchronised password change (#1119). A switch that guarded only one of those would
    /// leave an administrator who turned it on still sending passwords in the clear down the other two, which is
    /// worse than not offering it. It also has to be settable on a system that provisions accounts but receives
    /// no synchronised passwords, and such a system has no Password Synchronisation configuration to hold it.
    /// </para>
    /// <para>
    /// Off by default is a considered position rather than laxity. The LDAP Connector warns on an unencrypted
    /// connection instead of blocking, because a signed and sealed bind is a legitimate encrypted alternative
    /// that JIM cannot detect from the Connected System's settings alone, so refusing on the settings would
    /// refuse a valid configuration. This is how an administrator who knows their deployment closes that gap.
    /// </para>
    /// <para>
    /// The Connector reports whether its channel is encrypted
    /// (<see cref="JIM.Models.Interfaces.IConnectorPasswordManagement.IsPasswordChannelSecure"/>); the refusal is
    /// JIM's, applied here, because a Connector cannot know whether a given deployment is an isolated network
    /// with a directory that cannot serve TLS.
    /// </para>
    /// </summary>
    public bool RequireSecureTransport { get; set; }

    /// <summary>
    /// The time to live actually applied to a new <see cref="PendingInitialPassword"/> for this Connected System.
    /// A value of zero or less is treated as unconfigured rather than obeyed, because it would expire every
    /// account the instant it was provisioned, which is the one outcome nobody setting this can be asking for.
    /// </summary>
    [NotMapped]
    public TimeSpan EffectiveInitialPasswordTimeToLive =>
        InitialPasswordTimeToLive is { } timeToLive && timeToLive > TimeSpan.Zero
            ? timeToLive
            : PendingInitialPassword.DefaultTimeToLive;

    /// <summary>
    /// Set when the Connector Space is cleared: clearing hard-deletes Connected System Objects without
    /// obsoletion, so Metaverse attribute values contributed by source objects that never return survive
    /// with live provenance and no joined Connected System Object, indefinitely stranded (#1549). Holds the
    /// UTC time of the clear that armed it; null means no sweep is armed. The next Full Synchronisation of
    /// this system runs the stranded-value sweep (recalling the stranded values: surviving-contributor
    /// re-election or a No Contributor clear, per the shipped #1537/#809 recall engine) only once
    /// <see cref="LastSuccessfulFullImportCompletedAt"/> is later than this timestamp (#1605): a Full
    /// Synchronisation run before a Full Import has genuinely rebuilt the Connector Space would otherwise
    /// treat every previously joined object as departed. The sweep clears this back to null on completion;
    /// an interrupted sweep leaves it set, so the arming survives a retry.
    /// </summary>
    public DateTime? StrandedValueSweepArmedAt { get; set; }

    /// <summary>
    /// The UTC time the most recent Full Import of this Connected System completed successfully (#1605):
    /// Activity status Complete, or CompleteWithWarning where the warning came only from a connector-level
    /// warning message with no object-level errors recorded. An import that completed with object-level
    /// errors, completed with an unhandled error, failed, or was cancelled never stamps this, because an
    /// object that failed to import was never staged, and the stranded-value sweep must not treat it as
    /// departed. Compared against <see cref="StrandedValueSweepArmedAt"/> to gate the sweep; null means no
    /// Full Import of this system has ever completed successfully.
    /// </summary>
    public DateTime? LastSuccessfulFullImportCompletedAt { get; set; }

    /// <summary>
    /// EF back-link.
    /// </summary>
    public List<Activity>? Activities { get; set; }

    public override string ToString()
    {
        return $"{nameof(ConnectedSystem)}: {Name} ({Id})";
    }
}