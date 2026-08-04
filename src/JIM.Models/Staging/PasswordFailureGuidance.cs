// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What an administrator should do about a password JIM could not set (issue #1172).
/// <para>
/// Keyed off the classification JIM already computes rather than written as one generic help panel, because a
/// refused password and an unreachable directory need opposite advice: one says try another password, the other
/// says the password was never the problem. Sending an administrator to change a password that was fine is the
/// specific failure this exists to prevent.
/// </para>
/// <para>
/// Held here, in one place, rather than as copy inside a dialog. The same classification is recorded against
/// parked provisioning passwords (<see cref="PendingInitialPassword.FailureReason"/>), so the panel that shows
/// those meets exactly the same words rather than a second set that drifts.
/// </para>
/// </summary>
public class PasswordFailureGuidance
{
    /// <summary>
    /// What happened, in a few words. Sits above the detail as the thing to read first.
    /// </summary>
    public required string Headline { get; init; }

    /// <summary>
    /// What the failure means for the account, and whether the password itself is implicated.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// What to look at, most likely cause first.
    /// </summary>
    public required IReadOnlyList<string> Checks { get; init; }

    /// <summary>
    /// Whether trying again is worth anything, which is the single most useful thing to say: three of the five
    /// reasons are worth a retry and two are not, and guessing wrong wastes a round trip against a directory or
    /// leaves an account without a password nobody is going back to.
    /// </summary>
    public required PasswordRetryVerdict Verdict { get; init; }

    /// <summary>
    /// Where in the Connected System's own configuration the repair happens, as a tab on its page. Null where
    /// nothing in JIM will help.
    /// </summary>
    public string? ConnectedSystemTab { get; init; }

    /// <summary>
    /// What to call the link to <see cref="ConnectedSystemTab"/>.
    /// </summary>
    public string? ConnectedSystemTabLabel { get; init; }

    /// <summary>
    /// The guidance for one classified failure. Every reason has an answer; there is no fallback case, because
    /// a reason with nothing useful to say would be a reason JIM should not be reporting.
    /// </summary>
    public static PasswordFailureGuidance For(PasswordSetFailureReason reason) => reason switch
    {
        PasswordSetFailureReason.PolicyRejection => new PasswordFailureGuidance
        {
            Headline = "The password was refused",
            Summary = "The Connected System read the password and rejected it. The connection, the rights and the account are all fine, so another password may well be accepted.",
            Checks =
            [
                "The rules JIM discovered are a floor, not a guarantee. A policy that applies to only some accounts, or a custom password filter the system runs internally, is exposed over no protocol and cannot be read.",
                "Password history is the other common cause: this account may have held this value before. Generating another is the quickest test.",
                "If every generated password is refused, compare what JIM produces against the discovered policy on the Connected System's Schema tab."
            ],
            Verdict = PasswordRetryVerdict.RetryWithADifferentPassword,
            ConnectedSystemTab = "schema",
            ConnectedSystemTabLabel = "Schema (Password Channel)"
        },

        PasswordSetFailureReason.Transient => new PasswordFailureGuidance
        {
            Headline = "The Connected System could not be reached",
            Summary = "Nothing was established about the password itself. The same request is worth repeating once the system is reachable.",
            Checks =
            [
                "Active Directory refuses passwords over an unencrypted connection, so a system whose exports run happily over plain LDAP still needs LDAPS to set one. This is the most common cause and the least obvious, because exporting keeps working.",
                "Run Check password channel on the Connected System. It writes nothing, so it is safe against production at any time.",
                "A directory that has just been restarted, or a certificate that has just expired, produces this too."
            ],
            Verdict = PasswordRetryVerdict.RetryUnchanged,
            ConnectedSystemTab = "schema",
            ConnectedSystemTabLabel = "Schema (Check password channel)"
        },

        PasswordSetFailureReason.ConfigurationFault => new PasswordFailureGuidance
        {
            Headline = "JIM is not permitted to set a password here",
            Summary = "The account JIM connects as may not reset passwords on this object, or the Connected System is not configured for it. Retrying changes nothing until somebody grants the right.",
            Checks =
            [
                "Rights are granted per part of a directory, so the same service account can hold the right in one container and not in another. Check the container this account sits in specifically.",
                "Check password channel reports rights per managed container, which is where to look first.",
                "Accounts held in a directory's privileged groups have their permissions periodically overwritten from a template, so a delegation made on the container does not apply to them."
            ],
            Verdict = PasswordRetryVerdict.NeedsSomebodyElse,
            ConnectedSystemTab = "schema",
            ConnectedSystemTabLabel = "Schema (Check password channel)"
        },

        PasswordSetFailureReason.TargetObjectNotFound => new PasswordFailureGuidance
        {
            Headline = "The Connected System does not have this account",
            Summary = "Straight after provisioning this is usually replication rather than absence, and waiting fixes it.",
            Checks =
            [
                "If the account was created moments ago, give the directory time to replicate it and try again.",
                "Otherwise confirm the account still exists in the Connected System, and that it sits in a container this Connected System manages.",
                "An account moved out of a managed container is present in the directory but out of JIM's reach."
            ],
            Verdict = PasswordRetryVerdict.RetryUnchanged,
            ConnectedSystemTab = "partitions-containers",
            ConnectedSystemTabLabel = "Partitions and Containers"
        },

        PasswordSetFailureReason.UnsupportedOperation => new PasswordFailureGuidance
        {
            Headline = "This Connected System cannot set a password on this object",
            Summary = "It will answer identically every time, so there is nothing to retry. The credential needs a different route entirely.",
            Checks =
            [
                "The Connector may not support passwords at all, or not for this kind of object.",
                "Some systems accept a password only at creation, through provisioning, and refuse it afterwards.",
                "Where the system has its own self-service password mechanism, that is the route to use."
            ],
            Verdict = PasswordRetryVerdict.NeverHelps
        },

        // None is not a failure, so asking for its guidance is a caller's mistake rather than a state to render.
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "There is no guidance for a password set that did not fail.")
    };
}
