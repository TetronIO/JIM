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
    System = 12
}
