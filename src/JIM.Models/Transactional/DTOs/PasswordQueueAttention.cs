// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// How much queued password work on a Connected System is waiting on a person (#1119).
/// <para>
/// The two counts are reported separately and never summed, for the same reason
/// <see cref="InitialPasswordAttention"/> keeps them apart: a parked change can be resolved from where it is
/// reported, by fixing the cause and retrying, whereas an expired one cannot be resolved at all. Adding them
/// would produce a number with no single action behind it.
/// </para>
/// </summary>
public class PasswordQueueAttention
{
    /// <summary>
    /// Changes the target refused, or that ran out of delivery attempts, and which JIM has stopped retrying.
    /// </summary>
    public int ParkedCount { get; set; }

    /// <summary>
    /// Changes that sat unsent past the Connected System's time to live. The identity's password on that system
    /// is out of step with the rest, and JIM will not close the gap on its own.
    /// </summary>
    public int ExpiredCount { get; set; }
}
