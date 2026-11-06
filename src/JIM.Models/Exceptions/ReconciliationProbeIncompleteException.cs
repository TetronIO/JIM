// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// Pending Export reconciliation was asked to judge a change from probed values that cannot answer it (#2669): the
/// change was not among those the probe searched for, or its verdict depends on a value count the probe was not asked
/// for. Always a defect in planning the probe, never a state of the data, so the import fails rather than guess a
/// verdict.
/// </summary>
public sealed class ReconciliationProbeIncompleteException : InvalidOperationException
{
    public ReconciliationProbeIncompleteException(string message) : base(message) { }
}
