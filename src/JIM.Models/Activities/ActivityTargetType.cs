// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

public enum ActivityTargetType
{
    NotSet = 0,
    /// <summary>
    /// An Example Data (generation) Template: its configuration, object types, template attributes and referenced
    /// Example Data Sets. Covers create, update and delete of the template definition; its configuration-change
    /// history is associated via <see cref="Activity.ExampleDataTemplateId"/>. A template's data-generation *runs*
    /// are a separate operational concern, recorded under <see cref="DataGeneration"/>, not here.
    /// </summary>
    ExampleDataTemplate = 1,
    ConnectedSystem = 2,
    ConnectedSystemRunProfile = 3,
    SynchronisationRule = 4,
    MetaverseObject = 5,
    TrustedCertificate = 6,
    ObjectMatchingRule = 7,
    MetaverseAttribute = 8,
    ServiceSetting = 9,
    HistoryRetentionCleanup = 10,
    MetaverseObjectType = 11,
    /// <summary>
    /// A system-wide operation that is not scoped to a single entity, such as a factory reset.
    /// </summary>
    System = 12,
    /// <summary>
    /// A Schedule: the plan that defines what to run and when, covering create, update (including enable, disable
    /// and re-time) and delete. Guid-keyed, so its configuration-change history is associated via
    /// <see cref="Activity.ScheduleId"/> rather than an integer foreign key.
    /// </summary>
    Schedule = 13,

    /// <summary>
    /// The Temporal Scope Reconciler sweep (issue #892), which re-evaluates relative-date scoping across all
    /// enabled Synchronisation Rules that carry a relative-date criterion.
    /// </summary>
    TemporalScopeReconciliation = 14,

    /// <summary>
    /// An API Key. Guid-keyed, so its configuration-change history is associated via
    /// <see cref="Activity.ApiKeyId"/>.
    /// </summary>
    ApiKey = 15,

    /// <summary>
    /// A Role: covers both changes to a Role's definition and changes to its membership (an object being added to
    /// or removed from the Role). Integer-keyed via <see cref="Activity.RoleId"/>.
    /// </summary>
    Role = 16,

    /// <summary>
    /// A Predefined Search, including its criteria groups and criteria, which roll up into the owning search's
    /// configuration history. Integer-keyed via <see cref="Activity.PredefinedSearchId"/>.
    /// </summary>
    PredefinedSearch = 17,

    /// <summary>
    /// A Connector Definition, including its file set (recorded as metadata, never binary content).
    /// Integer-keyed via <see cref="Activity.ConnectorDefinitionId"/>.
    /// </summary>
    ConnectorDefinition = 18,

    /// <summary>
    /// An Example Data Set. Integer-keyed via <see cref="Activity.ExampleDataSetId"/>.
    /// </summary>
    ExampleDataSet = 19,

    /// <summary>
    /// The parent Activity grouping every built-in configuration object JIM seeds itself during a single
    /// application startup (Roles, Schedules, and similar), with each seeded object's own Create Activity as a
    /// child via <see cref="Activity.ParentActivityId"/>. Created lazily, only when a seed step is about to
    /// create something, so a startup where seeding no-ops records no Activity of this type at all.
    /// </summary>
    SystemInitialisation = 20,

    /// <summary>
    /// A data-generation run: executing an Example Data (generation) Template to create Metaverse Objects. This is an
    /// operational activity, not a configuration change, so it is categorised under System, kept distinct from the
    /// template's own configuration-change history (<see cref="ExampleDataTemplate"/>). The run still links to its
    /// template via <see cref="Activity.ExampleDataTemplateId"/> for context and deep-linking.
    /// </summary>
    DataGeneration = 21,

    /// <summary>
    /// A security audit event: interactive sign-in success/failure or API key authentication failure. Sign-in
    /// successes are recorded one per session establishment; failures are aggregated (see
    /// <see cref="Activity.AggregationWindowStart"/>, <see cref="Activity.AttemptCount"/>) into one Activity per
    /// (API key prefix, client IP, failure reason) per 15-minute UTC window, so a failed-authentication spray of
    /// any volume produces a bounded number of rows. Governed by its own retention class
    /// (see <see cref="JIM.Models.Core.Constants.SettingKeys.SecurityEventRetentionPeriod"/>), separate from general
    /// history and configuration-change retention.
    /// </summary>
    Authentication = 22,

    /// <summary>
    /// A Metaverse Object Housekeeping batch (issue #1020): the worker's idle-time deletion of Metaverse Objects
    /// whose deletion grace period has expired, including the reference-recall Pending Exports staged for objects
    /// (for example groups) that referenced them. Created only when a batch actually has work to do; a quiet idle
    /// tick records no Activity at all.
    /// </summary>
    MetaverseObjectHousekeeping = 23
}
