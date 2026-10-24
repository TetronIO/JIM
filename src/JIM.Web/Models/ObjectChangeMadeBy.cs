// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// Who or what made a Metaverse Object change, in the words an Object Changes row and its detail panel show.
/// </summary>
public static class ObjectChangeMadeBy
{
    /// <summary>
    /// The most specific thing the change record says made it: the Synchronisation Rule, then the principal by name,
    /// then the kind of principal, then the mechanism (System, for example, for a change recorded without an
    /// initiator). Null when the record says nothing at all.
    /// </summary>
    public static string? Describe(MvoChangeHistoryDto change)
    {
        if (!string.IsNullOrWhiteSpace(change.SyncRuleName))
            return change.SyncRuleName;

        if (!string.IsNullOrWhiteSpace(change.InitiatedByName))
            return change.InitiatedByName;

        if (change.InitiatedByType != ActivityInitiatorType.NotSet)
            return change.InitiatedByType.ToString().SplitOnCapitalLetters();

        return change.ChangeInitiatorType != MetaverseObjectChangeInitiatorType.NotSet
            ? change.ChangeInitiatorType.ToString().SplitOnCapitalLetters()
            : null;
    }
}
