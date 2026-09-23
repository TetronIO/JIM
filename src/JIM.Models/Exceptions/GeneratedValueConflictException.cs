// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// A <see cref="Transactional.GeneratedValueAssignment"/> could not be created because another assignment already
/// holds the same normalised value for the same attribute (Unique Value Generation, #242, plan decision 13).
/// <para>
/// The cross-assignment unique index on (attribute, normalised value) is the arbiter under concurrency: when two
/// synchronisation runs (or two pages of the same run) generate the same candidate for the same attribute at once,
/// exactly one INSERT wins and the other's <c>23505</c> violation surfaces here. This is an expected, recoverable
/// outcome, not a bug: the losing side is expected to draw its next candidate from the gates and try again, the
/// "losing-run rule" the plan's uniqueness decision describes. It is never swallowed silently; a caller that does
/// not catch it lets the failure propagate as an ordinary generation failure for that object.
/// </para>
/// </summary>
public class GeneratedValueConflictException : OperationalException
{
    public GeneratedValueConflictException(string message) : base(message) { }

    public GeneratedValueConflictException(string message, Exception? innerException) : base(message, innerException) { }
}
