// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Models.Localisation;

namespace JIM.Application.Localisation;

/// <summary>
/// What the export server says on an export's execution items (#3084): batch failures and Collision Remediation outcomes.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>A batch of exports failed; <paramref name="cause"/> is another system's words or JIM's own message.</summary>
    public static JimMessage ExportBatchFailed(MessageArgument cause) => Create(nameof(ActivityMessages.ExportBatchFailed), cause);

    /// <summary>A batch's results were recorded but closing its connection failed.</summary>
    public static JimMessage ExportBatchCloseFailed(MessageArgument cause) => Create(nameof(ActivityMessages.ExportBatchCloseFailed), cause);

    /// <summary>A batch's results were recorded but JIM failed while finishing them.</summary>
    public static JimMessage ExportBatchFinishFailed(MessageArgument cause) => Create(nameof(ActivityMessages.ExportBatchFinishFailed), cause);

    /// <summary>What an export item says when the Connector refused it without a reason.</summary>
    public static JimMessage ExportFailedNoReason() => Create(nameof(ActivityMessages.ExportFailedNoReason));

    /// <summary>An export run failed; <paramref name="cause"/> is another system's words or JIM's own message.</summary>
    public static JimMessage ExportRunFailed(MessageArgument cause) => Create(nameof(ActivityMessages.ExportFailed), cause);

    /// <summary>A rejected Generated Value JIM corrected.</summary>
    public static JimMessage CollisionRemediated(string connectedSystemName, string attributeName, string rejectedValue, string newValue) =>
        Create(nameof(ActivityMessages.CollisionRemediated), connectedSystemName, attributeName, rejectedValue, newValue);

    /// <summary>A rejected Generated Value JIM did not change, and why.</summary>
    public static JimMessage CollisionNeedsDecision(string connectedSystemName, string attributeName, string rejectedValue, JimMessage why) =>
        Create(nameof(ActivityMessages.CollisionNeedsDecision), connectedSystemName, attributeName, rejectedValue, why);

    /// <summary>Why a value was not changed: it had already been corrected as often as JIM will.</summary>
    public static JimMessage CollisionWhyLimitReached(int corrections) =>
        Create(nameof(ActivityMessages.CollisionWhyLimitReached), Plain(corrections));

    /// <summary>What a Connected System has accepted or may hold: the generated value itself, or one built from it.</summary>
    public static JimMessage CollisionHeld(string? builtValueName) => builtValueName == null
        ? Create(nameof(ActivityMessages.CollisionItself))
        : Create(nameof(ActivityMessages.CollisionBuiltFromIt), builtValueName);

    /// <summary>Why a value was not changed: a Connected System has already accepted it.</summary>
    public static JimMessage CollisionWhyAnchored(string connectedSystemName, JimMessage held) =>
        Create(nameof(ActivityMessages.CollisionWhyAnchored), connectedSystemName, held);

    /// <summary>Why a value was not changed: JIM cannot tell whether a Connected System holds it.</summary>
    public static JimMessage CollisionWhyCannotTell(string connectedSystemName, JimMessage held) =>
        Create(nameof(ActivityMessages.CollisionWhyCannotTell), connectedSystemName, held);

    /// <summary>Why a value was not changed: no other value could be found for it.</summary>
    public static JimMessage CollisionWhyNoValue(MessageArgument failure) =>
        Create(nameof(ActivityMessages.CollisionWhyNoValue), failure);
}
