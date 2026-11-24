// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core.DTOs;
using JIM.Application.Servers;
using JIM.Models.Localisation;
using JIM.Models.Resources;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// The Activity and execution item texts that were English-only after #3039's first pass and are stored as messages now
/// (#3084): the English is exactly what each writer always wrote, and each message renders in French and German.
/// </summary>
[TestFixture]
public class ActivityMessageRemainingWritersTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");

    private static string In(JimMessage message, CultureInfo language) => MessageRenderer.Render(message, language, language);

    private static void AssertTranslated(JimMessage message, string english)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(english));
            Assert.That(In(message, French), Is.Not.EqualTo(english));
            Assert.That(In(message, German), Is.Not.EqualTo(english));
        }
    }

    private static AttributeReference Described(JimMessage description) => new() { Kind = AttributeReferenceKind.ImportAttributeFlow, DescriptionDetail = description };

    [Test]
    public void MetaverseRemovals_SayWhatTheServerAlwaysWrote_AndTranslate()
    {
        var reference = Described(JimMessage.Create(MessageCatalogues.MetaverseReferences, nameof(MetaverseReferenceMessages.ReferenceImportFlowInRule), "HR to Metaverse"));

        AssertTranslated(ActivityMessage.MetaverseAttributeReferenceRemoved(reference, "Mail"),
            "Removed Import Attribute Flow in Synchronisation Rule 'HR to Metaverse' referencing Metaverse Attribute 'Mail'");
        AssertTranslated(ActivityMessage.MetaverseAttributeRemoved("Mail"), "Removed Metaverse Attribute 'Mail'");
        AssertTranslated(ActivityMessage.MetaverseAttributeUnassigned("Mail", "User"),
            "Unassigned Metaverse Attribute 'Mail' from Metaverse Object Type 'User'");
        AssertTranslated(ActivityMessage.MetaverseReferenceRemovedScoped(ActivityMessage.DescribedReference(reference), "User"),
            "Removed Import Attribute Flow in Synchronisation Rule 'HR to Metaverse' scoped to Metaverse Object Type 'User'");
        AssertTranslated(ActivityMessage.MetaverseObjectTypeRemoved("User"), "Removed Metaverse Object Type 'User'");
    }

    [Test]
    public void MetaverseReference_DescribedByEnglishOnly_StaysAsWritten()
    {
        var reference = new AttributeReference { Description = "Object Type binding: User" };

        Assert.That(ActivityMessage.MetaverseAttributeReferenceRemoved(reference, "Mail").ToEnglish(),
            Is.EqualTo("Removed Object Type binding: User referencing Metaverse Attribute 'Mail'"));
    }

    [Test]
    public void AttributeReference_SettingTheDescriptionMessage_SetsTheEnglishDescription()
    {
        var reference = Described(JimMessage.Create(MessageCatalogues.MetaverseReferences, nameof(MetaverseReferenceMessages.ReferenceSsoMapping)));

        Assert.That(reference.Description, Is.EqualTo("Service Settings SSO unique-identifier mapping (cleared)"));
    }

    [Test]
    public void MetaverseReferenceDescriptions_EachSaysWhatTheRepositoryAlwaysWrote_AndTranslates()
    {
        JimMessage Make(string key, params MessageArgument[] arguments) => JimMessage.Create(MessageCatalogues.MetaverseReferences, key, arguments);

        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceBinding), "User"), "Object Type binding: User");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceImportFlowInRule), "R"), "Import Attribute Flow in Synchronisation Rule 'R'");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceImportFlowMapping), "4"), "Import Attribute Flow (mapping 4)");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportSourceInRule), "R"), "Export Attribute Flow source in Synchronisation Rule 'R'");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportSourceMapping), "4"), "Export Attribute Flow source (mapping source 4)");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportMappingInRuleRemoved), "R"),
            "Export Attribute Flow in Synchronisation Rule 'R' (removed: it would be left with no sources)");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExportMappingRemoved), "4"),
            "Export Attribute Flow (mapping 4) removed: it would be left with no sources");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceScopingCriterion), "4"), "Scoping criterion 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceObjectMatchingRule), "4"), "Object Matching Rule 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferencePredefinedSearchColumn), "S"), "Predefined Search column in 'S'");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferencePredefinedSearchCriterion), "4"), "Predefined Search criterion 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExampleDataAttribute), "4"), "Example Data template attribute 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceExampleDataDependency), "4"), "Example Data template attribute dependency 4");
        AssertTranslated(Make(nameof(MetaverseReferenceMessages.ReferenceSsoMapping)), "Service Settings SSO unique-identifier mapping (cleared)");
    }

    private static JimMessage Server(string key, params MessageArgument[] arguments) => JimMessage.Create(ApplicationMessageCatalogues.Servers, key, arguments);

    [Test]
    public void CertificateTrustResults_SayWhatTheServerAlwaysSaid_AndTranslate()
    {
        AssertTranslated(Server(nameof(ServerMessages.TrustThumbprintRequired)),
            "A thumbprint is required. JIM will not trust whatever a server happens to be presenting.");
        AssertTranslated(Server(nameof(ServerMessages.TrustServerUnreachable), "ldap.example.com", "636"),
            "ldap.example.com:636 could not be reached, so nothing was trusted.");
        AssertTranslated(Server(nameof(ServerMessages.TrustServerOfferedNothing), "ldap.example.com", "636"),
            "ldap.example.com:636 offered no certificate, so there is nothing to trust.");
        AssertTranslated(Server(nameof(ServerMessages.TrustThumbprintMismatch), "ldap.example.com"),
            "ldap.example.com is presenting a different certificate from the one you confirmed, so nothing has been trusted. This is expected if the certificate was renewed; investigate if it was not.");
        AssertTranslated(Server(nameof(ServerMessages.TrustRootRequiredWithRoot), "directory server", "Leaf", "Root CA"),
            "This connection to the directory server accepts only the root of the certificate chain, so trusting 'Leaf' would not make it work, and nothing was trusted. Trust 'Root CA' instead.");
        AssertTranslated(Server(nameof(ServerMessages.TrustRootRequiredNoRoot), "directory server"),
            "This connection to the directory server accepts only the root of the certificate chain, and JIM could not find it, so nothing was trusted. Get the root certificate from whoever runs your PKI and add it under Admin > Certificates.");
        AssertTranslated(Server(nameof(ServerMessages.TrustAlreadyInStore), "Leaf"), "'Leaf' is already in the JIM certificate store.");
    }

    [Test]
    public void ExportItemErrors_SayWhatTheServerAlwaysWrote_AndTranslate()
    {
        var other = new InvalidOperationException("connection reset");
        var carried = LocalisedExceptions.InvalidOperation(ActivityMessage.Create(nameof(ActivityMessages.AllItemsFailed)));

        AssertTranslated(ActivityMessage.ExportBatchFailed(ActivityMessage.CauseOf(other)), "Batch export failed: connection reset");
        AssertTranslated(ActivityMessage.ExportBatchCloseFailed(ActivityMessage.CauseOf(other)),
            "A batch's results were recorded, but closing its connection failed: connection reset");
        AssertTranslated(ActivityMessage.ExportBatchFinishFailed(ActivityMessage.CauseOf(other)),
            "The batch's results were recorded, but JIM failed while finishing them: connection reset");
        AssertTranslated(ActivityMessage.ExportRunFailed(ActivityMessage.CauseOf(other)), "Export failed: connection reset");
        AssertTranslated(ActivityMessage.ExportFailedNoReason(), "Export failed");
        Assert.That(ActivityMessage.ExportBatchFailed(ActivityMessage.CauseOf(carried)).ToEnglish(), Is.EqualTo($"Batch export failed: {carried.Message}"));
    }

    [Test]
    public void ProcessedExportItem_SettingTheErrorMessage_SetsTheEnglish()
    {
        var item = new JIM.Models.Transactional.ProcessedExportItem { ErrorMessageDetail = ActivityMessage.ExportFailedNoReason() };

        Assert.That(item.ErrorMessage, Is.EqualTo("Export failed"));
    }

    [Test]
    public void CollisionRemediation_Messages_SayWhatTheRunAlwaysWrote_AndTranslate()
    {
        AssertTranslated(ActivityMessage.CollisionRemediated("Corporate AD", "sAMAccountName", "jsmith", "jsmith2"),
            "Corporate AD already holds the sAMAccountName \"jsmith\" for another object, so JIM corrected it to \"jsmith2\".");

        var limit = ActivityMessage.CollisionWhyLimitReached(3);
        AssertTranslated(ActivityMessage.CollisionNeedsDecision("Corporate AD", "sAMAccountName", "jsmith", limit),
            "Corporate AD rejected the sAMAccountName \"jsmith\" because another object there already holds it, and JIM has not changed it: " +
            "it has already been corrected 3 times, so JIM has stopped correcting it. Allow the rename, retry once the conflict is resolved in Corporate AD, or leave it.");

        AssertTranslated(ActivityMessage.CollisionWhyAnchored("HR", ActivityMessage.CollisionHeld(null)),
            "HR has already accepted it for this object, so changing it would rename an account already in use");
        AssertTranslated(ActivityMessage.CollisionWhyAnchored("HR", ActivityMessage.CollisionHeld("mail")),
            "HR has already accepted the mail built from it for this object, so changing it would rename an account already in use");
        AssertTranslated(ActivityMessage.CollisionWhyCannotTell("HR", ActivityMessage.CollisionHeld("mail")),
            "JIM cannot tell whether HR already holds the mail built from it, because HR has not completed a Full Import since its connector space was cleared");
        AssertTranslated(ActivityMessage.CollisionWhyNoValue("the pool is exhausted"), "no other value could be found for it: the pool is exhausted");
    }

    [Test]
    public void ExportNarration_SaysWhatTheServerAlwaysSaid_AndTranslates()
    {
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExportResolvingDeferred), ActivityMessage.Plain(12000)), "Resolving 12000 deferred exports");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExportLoadingDeferred), ActivityMessage.Plain(1), ActivityMessage.Plain(500), ActivityMessage.Plain(1200)),
            "Loading deferred exports 1 to 500 of 1200");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExportWithdrewDeferredReferences), ActivityMessage.Plain(3)),
            "Withdrew references to deleted objects from 3 deferred exports");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExportSavingResolvedReferences), ActivityMessage.Plain(3)),
            "Saving resolved references for 3 deferred exports");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExportRecordingWaiting)), "Recording exports that are still waiting on their references");
    }

    private static ProvisionedPasswordStagingOutcome Staged(
        PendingPasswordChangeStatus? status = null, PasswordSetFailureReason? failure = null, string? target = null,
        bool held = false, bool gone = false, bool otherType = false, PendingPasswordChangeOrigin? origin = null) => new()
    {
        RequestedId = Guid.NewGuid(),
        Disposition = ProvisionedPasswordStagingDisposition.Superseded,
        ExistingStatus = status,
        ExistingFailureReason = failure,
        ExistingTargetMessage = target,
        ExistingHeld = held,
        ExistingAccountGone = gone,
        ExistingForAnotherObjectType = otherType,
        ExistingOrigin = origin
    };

    private const string CarriedBase =
        "Initial password not queued for Corporate AD: a password change already waiting for this person there will be this Connected System Object's first password instead, " +
        "keeping its own password and expiry. Its delivery applies this Synchronisation Rule's Initial Password settings for enabling the Connected System Object and for " +
        "whether its password never expires, except where an export has written those itself.";

    [Test]
    public void InitialPasswordStaging_Carried_SaysWhatTheServerAlwaysSaid_AndTranslates()
    {
        AssertTranslated(InitialPasswordStagingMessages.Carried("Corporate AD", Staged()), CarriedBase);
        AssertTranslated(InitialPasswordStagingMessages.Carried("Corporate AD", Staged(PendingPasswordChangeStatus.Delivering)),
            CarriedBase + " It was being delivered as the Connected System Object was created, and is delivered again with these settings.");
        AssertTranslated(InitialPasswordStagingMessages.Carried("Corporate AD", Staged(failure: PasswordSetFailureReason.TargetObjectNotFound)),
            CarriedBase + " It was waiting for the Connected System Object to exist, and is due now.");
        AssertTranslated(InitialPasswordStagingMessages.Carried("Corporate AD", Staged(PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.PolicyRejection, " too short ")),
            CarriedBase + " It had been parked (the password did not meet the system's password policy: too short), and is due again now that the Connected System Object exists.");
        AssertTranslated(InitialPasswordStagingMessages.Carried("Corporate AD", Staged(PendingPasswordChangeStatus.Parked)),
            CarriedBase + " It had been parked (for a reason JIM did not record), and is due again now that the Connected System Object exists.");
    }

    [Test]
    public void InitialPasswordStaging_ReplacedWaitingChange_SaysWhatTheServerAlwaysSaid_AndTranslates()
    {
        AssertTranslated(InitialPasswordStagingMessages.ReplacedWaitingChange("Corporate AD", Staged(held: true))!,
            "it is held while Password Synchronisation is switched off for Corporate AD, so it had not reached it, and a Connected System Object's first password is delivered whatever the pause.");
        AssertTranslated(InitialPasswordStagingMessages.ReplacedWaitingChange("Corporate AD", Staged(gone: true))!,
            "it was set on a Connected System Object that no longer exists there, or is no longer this person's.");
        AssertTranslated(InitialPasswordStagingMessages.ReplacedWaitingChange("Corporate AD", Staged(otherType: true))!,
            "it is for the Object Type that Password Synchronisation sets passwords on in Corporate AD, which this Connected System Object is not, so it could not reach it, nor any other of this person's there while this one is theirs.");
        AssertTranslated(InitialPasswordStagingMessages.ReplacedWaitingChange("Corporate AD",
                Staged(PendingPasswordChangeStatus.Parked, PasswordSetFailureReason.Transient, origin: PendingPasswordChangeOrigin.Propagated))!,
            "JIM had stopped trying to deliver it (the system could not be reached).");
        Assert.That(InitialPasswordStagingMessages.ReplacedWaitingChange("Corporate AD", Staged()), Is.Null);
    }

    [Test]
    public void InitialPasswordStaging_QueuedAndNotSet_SayWhatTheServerAlwaysSaid_AndTranslate()
    {
        var replaced = InitialPasswordStagingMessages.ReplacedWaitingChange("Corporate AD", Staged(gone: true))!;

        AssertTranslated(InitialPasswordStagingMessages.Queued("Corporate AD", null), "Initial password queued for delivery to Corporate AD.");
        AssertTranslated(InitialPasswordStagingMessages.Queued("Corporate AD", replaced),
            "Initial password queued for delivery to Corporate AD, in place of a password change for this person that was waiting there: " +
            "it was set on a Connected System Object that no longer exists there, or is no longer this person's.");
        AssertTranslated(InitialPasswordStagingMessages.NotSet("Corporate AD", replaced),
            "Password not set on Corporate AD: replaced by the Initial Password of a Connected System Object JIM has just provisioned there, because this change " +
            "it was set on a Connected System Object that no longer exists there, or is no longer this person's.");
    }

    [Test]
    public void ConnectedSystemServer_Texts_SayWhatTheServerAlwaysWrote_AndTranslate()
    {
        var undecidable = new JIM.Models.Exceptions.ScopingCriterionFailedException("Rule A", "mail", "the value is unreadable");
        var cause = ActivityMessage.CauseOf(undecidable);
        var id = Guid.NewGuid().ToString();

        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.AttributePriorityRenumbered), "Rule A"),
            "Attribute priority numbers were renumbered by a change to Synchronisation Rule 'Rule A'. " +
            "The order in which Synchronisation Rules contribute to the Metaverse Attribute is unchanged.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.DeprovisioningScopeHeldBack), cause, id),
            $"{undecidable.Message} It and Metaverse Object {id} were left as they were, and the Connected System is not deleted until they can be processed.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.RecallScopeHeldBack), cause, id),
            $"{undecidable.Message} Metaverse Object {id} was left as it was, with the values the Synchronisation Rule contributed.");
    }

    [Test]
    public void ImportObjectTypeErrors_SayWhatTheProcessorAlwaysWrote_AndTranslate()
    {
        AssertTranslated(ActivityMessage.ImportObjectTypeChangeWithdrawn("User", "Contact"),
            "JIM holds this object as a 'User', and this Full Import allows Object Type changes, but it could not retire the 'User' object: " +
            "it is outside what the Full Import's deletion detection covers (a partition this Run Profile does not import, or a deselected Object Type that an enabled Synchronisation Rule still manages). " +
            "The new 'Contact' object has not been created, and the 'User' object has been left as it is, so nothing is deprovisioned. " +
            "Bring the 'User' object into the Full Import's scope, or disable the Synchronisation Rules bound to its Object Type, then run the Full Import again.");

        const string tail = " If the object's type really has changed, run a Full Import with Allow Object Type Changes enabled on its Run Profile: " +
            "JIM will retire the 'User' object and import a new 'Contact' one. If it has not, review the Object Types selected on the Connected System.";
        AssertTranslated(ActivityMessage.ImportObjectTypeChangeRefused("User", "Contact", "E1", heldIsObsolete: false),
            "JIM holds this object as a 'User', but the Connector now returns it as a 'Contact' (External ID 'E1'). " +
            "It has not been imported, and the existing 'User' object has been left as it is, so nothing is deprovisioned." + tail);
        AssertTranslated(ActivityMessage.ImportObjectTypeChangeRefused("User", "Contact", "E1", heldIsObsolete: true),
            "JIM holds this object as a 'User', but the Connector now returns it as a 'Contact' (External ID 'E1'). " +
            "It has not been imported, and the existing 'User' object, which an earlier import marked obsolete, has been left as it is." + tail);

        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExportedCreateNotConfirmed)),
            "The Create export succeeded, but this import read every object and did not report this one back. The Create will be retried on the next export.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportDeletionWithoutObjectType)),
            "The Connector reported a deletion without its Object Type, and its External ID does not tell JIM which object was deleted: " +
            "this Connector's External IDs can name different objects in different Object Types. Nothing was deleted; run a Full Import, which detects deletions by absence.");

        var notFound = ActivityMessage.ImportObjectTypeNotFound(7, "Widget");
        Assert.That(notFound.ToEnglish(), Is.EqualTo("PerformImportAsync: Couldn't find valid Connected System (7) object type for imported object type: Widget"));
        Assert.That(In(notFound, French), Is.Not.EqualTo(notFound.ToEnglish()));
    }

    [Test]
    public void UnresolvedReferences_SaysWhatTheServerAlwaysWrote_WithAndWithoutMore_AndTranslates()
    {
        const string lead = " reference value(s) could not be written because the referenced Metaverse Object has no Connected System Object in this Connected System: ";
        const string tail = ". The referenced object is out of scope for every Synchronisation Rule into this system, or has not been provisioned yet. " +
            "Everything else on this export was written; the reference is retried on the deferred cadence and written when the object appears.";

        AssertTranslated(ActivityMessage.UnresolvedReferences(["Manager -> 'Ada' (1)", "Manager -> 'Bo' (2)"], 3), "2" + lead + "Manager -> 'Ada' (1); Manager -> 'Bo' (2)" + tail);
        AssertTranslated(ActivityMessage.UnresolvedReferences(["a -> 1", "b -> 2", "c -> 3", "d -> 4", "e -> 5"], 3), "5" + lead + "a -> 1; b -> 2; c -> 3; and 2 more" + tail);
    }

    [Test]
    public void ProvisioningMatchErrors_SayWhatTheServerAlwaysWrote_AndTranslate()
    {
        const string rest = ". A Metaverse Object can join only one Connected System Object in each Connected System: correct the data, or the Object Matching Rule, so that its values identify one.";

        AssertTranslated(ActivityMessage.ProvisioningMatchFailed("Corporate AD", "the rule could not be worked out"), "Before provisioning to 'Corporate AD': the rule could not be worked out");
        AssertTranslated(ActivityMessage.ProvisioningAmbiguity(2, "Corporate AD", null, [], 0, previewing: false),
            "This Metaverse Object matches 2 Connected System Objects in 'Corporate AD', so it was neither joined to one nor provisioned" + rest);
        AssertTranslated(ActivityMessage.ProvisioningAmbiguity(2, "Corporate AD", "Employee ID equals employeeNumber", ["\"E1\""], 0, previewing: true),
            "This Metaverse Object matches 2 Connected System Objects in 'Corporate AD' on Employee ID equals employeeNumber (\"E1\"), so a synchronisation would neither join it to one nor provision it" + rest);
        AssertTranslated(ActivityMessage.ProvisioningAmbiguity(2, "Corporate AD", "x equals y", ["\"E1\"", "\"E2\""], 3, previewing: false),
            "This Metaverse Object matches 2 Connected System Objects in 'Corporate AD' on x equals y (\"E1\", \"E2\", and 3 more), so it was neither joined to one nor provisioned" + rest);
    }
}
