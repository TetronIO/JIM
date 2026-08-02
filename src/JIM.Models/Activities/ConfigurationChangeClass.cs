// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// How consequential a configuration change is, and therefore what JIM must show the administrator
/// before it is applied. See engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md for the model, the
/// boundary between Destructive and SyncAffecting, and the classification of every property.
///
/// The numeric order is the severity order: a change is classified by the highest class among the
/// properties that actually changed, so these values must never be reordered or renumbered.
/// </summary>
public enum ConfigurationChangeClass
{
    /// <summary>
    /// No classification applies. Used for creates, which have no prior snapshot to diff and so put
    /// no existing object at risk.
    /// </summary>
    NotClassified = 0,

    /// <summary>
    /// Class C: no effect on synchronisation outcomes (names, descriptions, display hints, schedule
    /// timing, page sizes). Never prompts; the save proceeds untouched.
    /// </summary>
    Cosmetic = 1,

    /// <summary>
    /// Class B: changes synchronisation outcomes without directly destroying data (scoping, Object
    /// Matching Rules, Attribute Flow, schema selection). A preview is offered; the save is never
    /// blocked.
    /// </summary>
    SyncAffecting = 2,

    /// <summary>
    /// Class A: can cascade deletions or mass deprovisioning the moment it is applied (deprovision
    /// actions, deletion rules, Object Type and partition deselection). A preview and a count-stating
    /// confirmation are mandatory.
    /// </summary>
    Destructive = 3
}
