// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// A Synchronisation Rule's scoping criterion could not be evaluated for an object (#2053), so whether the object is in
/// scope cannot be decided. Synchronisation stops that object with a <c>ScopingCriterionFailed</c> error and takes no
/// action for it, rather than bringing it into scope or taking it out on a guess; every other object carries on.
/// </summary>
public class ScopingCriterionFailedException : OperationalException
{
    /// <param name="syncRuleName">The Synchronisation Rule whose criterion failed.</param>
    /// <param name="attributeName">The attribute the criterion compares.</param>
    /// <param name="reason">Why it could not be evaluated, as a sentence.</param>
    /// <param name="innerException">The failure underneath, if any.</param>
    public ScopingCriterionFailedException(string syncRuleName, string attributeName, string reason, Exception? innerException = null)
        : base($"The scoping criterion on '{attributeName}' in the Synchronisation Rule '{syncRuleName}' could not be evaluated, " +
               $"so this object's scope was left as it was. {reason}", innerException)
    {
        AttributeName = attributeName;
    }

    /// <summary>The attribute the criterion compares.</summary>
    public string AttributeName { get; }
}
