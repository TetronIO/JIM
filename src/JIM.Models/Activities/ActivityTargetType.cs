// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

public enum ActivityTargetType
{
    NotSet = 0,
    ExampleDataTemplate = 1,
    ConnectedSystem = 2,
    ConnectedSystemRunProfile = 3,
    SyncRule = 4,
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
    ExampleDataSet = 19
}
