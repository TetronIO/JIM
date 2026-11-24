// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Application.Services;
using JIM.Models.Exceptions;
using JIM.Models.Localisation;
using JIM.Models.Operations;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// The text a synchronisation run and the Worker store on Activities and execution items (#3039): the English is exactly
/// what was written before messages existed, and each message renders in French and German for the portal.
/// </summary>
[TestFixture]
public class ActivityMessageWorkerFamiliesTests
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

    private static MemoryReading Reading() => new() { ProcessBytes = 41_000_000_000, TotalBytes = 62_000_000_000, AvailableBytes = 5_000_000_000 };

    [Test]
    public void ImportErrors_EachRecord_SaysWhatItAlwaysSaid_AndTranslates()
    {
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportDuplicateAttributes), "mail, cn"),
            "The imported object has one or more duplicate attributes: mail, cn. Please de-duplicate and try again.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportDuplicateExternalIdInBatch), "E1"),
            "Duplicate external ID 'E1' found in the same import batch. All objects with this external ID have been rejected. Fix the source data to ensure unique external IDs.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportDuplicateExternalIdAcrossPages), "E1"),
            "Duplicate external ID 'E1' found across import pages. An object with this external ID was already returned on an earlier page, so this copy has been rejected. This may indicate a directory server paging issue.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportCsoCreationFailed), "E1"),
            "Failed to create Connected System Object for import object with external ID 'E1'. No specific error was recorded.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportUnexpectedAttribute), "sn"),
            "Was not expecting the imported object attribute 'sn'.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportHashVerificationFailed), "abc", ActivityMessage.Plain(1200)),
            "Verification Mode: the stored import content hash for CSO abc matched the incoming hash, but the honest diff found 1200 changed attribute value(s). A content-hash skip would have missed this change.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ImportOrphanedItem), "E1", "False"),
            "Internal error: RPEI was created for import (ExtId=E1) but CSO was not persisted. CSO reference exists=False. This indicates a bug in the persistence layer.");
    }

    [Test]
    public void FailureInContext_WithAndWithoutACarriedMessage_ReadsAsTheWorkerAlwaysWroteIt()
    {
        var plain = new InvalidOperationException("boom", new TimeoutException("slow"));
        var carried = LocalisedExceptions.InvalidOperation(ActivityMessage.Create(nameof(ActivityMessages.AllItemsFailed)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActivityMessage.FailureInContext(ActivityMessage.ContextSyncRunUnhandled(), plain).ToEnglish(),
                Is.EqualTo("Unhandled exception whilst executing sync run: boom --> slow"));
            Assert.That(ActivityMessage.FailureInContext(ActivityMessage.ContextCompleteFailed(), carried).ToEnglish(),
                Is.EqualTo($"Failed to complete activity after sync run: {carried.Message}"));
            Assert.That(In(ActivityMessage.FailureInContext(ActivityMessage.ContextMemoryGuard(), carried), French), Does.StartWith("Arrêté par la protection mémoire"));
            Assert.That(In(ActivityMessage.FailureInContext(ActivityMessage.ContextMemoryGuard(), carried), German), Does.StartWith("Vom Speicherschutz"));
        }
    }

    [Test]
    public void Memory_Messages_SayWhatTheGuardAlwaysSaid_AndTranslate()
    {
        var reading = Reading();
        const string figures = "57 GB of 62 GB in use, 41 GB of it by the Worker";
        var remedy = "At 92% the Worker stops its running work rather than be stopped part-way through; give JIM more memory, or run less at once.";

        AssertTranslated(ActivityMessage.MemoryWarning(reading), $"Memory was low while this ran: {figures}. {remedy}");
        AssertTranslated(ActivityMessage.MemoryStop(reading),
            $"The Worker stopped this run because its memory was nearly exhausted: {figures}. Had it carried on, the operating system would have stopped the Worker part-way through. Give JIM more memory, or run less at once, then run it again.");
        AssertTranslated(ActivityMessage.MemoryAbort(reading),
            $"The Worker stopped this run and restarted itself because its memory was nearly exhausted ({figures}) and the run did not stop when asked. Had it carried on, the operating system would have stopped the Worker without a word. Give JIM more memory, or run less at once, then run it again.");
        AssertTranslated(ActivityMessage.LikelyOutOfMemory(reading),
            $"The Worker most likely ran out of memory: when it last reported, {figures}. Give JIM more memory, or run less at once, then run it again.");
        AssertTranslated(ActivityMessage.OutOfMemory(reading),
            "This run ran out of memory and stopped (now 57 GB of 62 GB in use, 41 GB of it by this service). In a container with a memory limit, JIM can run out at about 75% of that limit. Give JIM more memory, or run less at once, then run it again.");
    }

    [Test]
    public void Memory_WithoutATotal_OutOfMemoryOmitsTheFigures_AndMatchesTheTextHelper()
    {
        var unknown = new MemoryReading { ProcessBytes = 1 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActivityMessage.OutOfMemory(unknown).ToEnglish(),
                Is.EqualTo("This run ran out of memory and stopped. In a container with a memory limit, JIM can run out at about 75% of that limit. Give JIM more memory, or run less at once, then run it again."));
            Assert.That(MemoryPressure.OutOfMemoryText(Reading()), Is.EqualTo(ActivityMessage.OutOfMemory(Reading()).ToEnglish()));
            Assert.That(MemoryPressure.Describe(Reading(), "the Worker"), Is.EqualTo(In(ActivityMessage.Create(nameof(ActivityMessages.MemoryFiguresByWorker), "57 GB", "62 GB", "41 GB"), MessageRenderer.English)));
        }
    }

    [Test]
    public void Paragraphs_PutsABlankLineBetweenTwoMessages()
    {
        var joined = ActivityMessage.Paragraphs(ActivityMessage.Create(nameof(ActivityMessages.Verbatim), "first"), ActivityMessage.Create(nameof(ActivityMessages.Verbatim), "second"));

        Assert.That(joined.ToEnglish(), Is.EqualTo("first\n\nsecond"));
    }

    [Test]
    public void ScopingCriterionFailed_CarriesAMessage_ThatSaysExactlyItsText()
    {
        var exception = new ScopingCriterionFailedException("Rule A", "mail", "Its pattern ran past its time limit.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Is.EqualTo("The scoping criterion on 'mail' in the Synchronisation Rule 'Rule A' could not be evaluated, so this object's scope was left as it was. Its pattern ran past its time limit."));
            Assert.That(exception.DetailOf()!.ToEnglish(), Is.EqualTo(exception.Message));
            Assert.That(In(exception.DetailOf()!, French), Does.Contain("règle de synchronisation"));
            Assert.That(In(exception.DetailOf()!, German), Does.Contain("Synchronisierungsregel"));
        }
    }

    [Test]
    public void ExportScopingFailed_NamesTheObject_AndTranslates()
    {
        var exception = new ScopingCriterionFailedException("Rule A", "mail", "Why.");
        var id = Guid.NewGuid();

        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExportScopingFailed), ActivityMessage.CauseOf(exception), id.ToString()),
            $"{exception.Message} Nothing was exported for Metaverse Object {id}.");
    }

    [Test]
    public void ExpressionExceptions_CarryMessages_AndDescribeThemselvesAsBefore()
    {
        var evaluation = new SyncExpressionEvaluationException("cs[\"x\"]", "displayName", new InvalidOperationException("bad"))
        {
            SyncRuleName = "Derived"
        };
        var missing = new SyncExpressionMissingInputException("cs[\"x\"]", "mail", ["cs[\"givenName\"]"]) { SyncRuleName = "Derived" };
        var mvoId = Guid.NewGuid();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Message, Is.EqualTo("Expression evaluation failed for target attribute 'displayName': bad"));
            Assert.That(ActivityMessage.ExpressionEvaluationFailed(evaluation, mvoId).ToEnglish(),
                Is.EqualTo($"Expression evaluation failed for Metaverse Object {mvoId} flowing to attribute 'displayName' (derived by Synchronisation Rule 'Derived'): bad. Expression: cs[\"x\"]"));
            Assert.That(ActivityMessage.ExpressionEvaluationFailed(evaluation).ToEnglish(),
                Is.EqualTo("Expression evaluation failed flowing to attribute 'displayName' (derived by Synchronisation Rule 'Derived'): bad. Expression: cs[\"x\"]"));
            Assert.That(missing.DescribeForAdministrator(),
                Is.EqualTo("Expression for target attribute 'mail' was not evaluated: no value for cs[\"givenName\"]. Missing Input Behaviour is set to Fail the object. " +
                           "The Attribute Flow is derived by Synchronisation Rule 'Derived'. Supply the missing value, handle its absence in the Expression, or change the Attribute Flow's Missing Input Behaviour."));
            Assert.That(ActivityMessage.ExportMissingInput(missing, mvoId, "Directory").ToEnglish(),
                Is.EqualTo($"{missing.Message} No Pending Export was generated for Metaverse Object {mvoId} on 'Directory'. Supply the missing value, handle its absence in the Expression, or change the Attribute Flow's Missing Input Behaviour."));
            Assert.That(In(missing.Describe(), French), Does.Contain("flux d’attributs"));
            Assert.That(In(missing.Describe(), German), Does.Contain("Attributfluss"));
            Assert.That(In(ActivityMessage.ExpressionEvaluationFailed(evaluation, mvoId), French), Does.StartWith("L’évaluation de l’expression a échoué pour l’objet Metaverse"));
        }
    }

    [Test]
    public void ExportRuleMessages_OverlapHandOverConflictAndRecall_SayWhatTheyAlwaysSaid_AndTranslate()
    {
        var overlap = new ExportRuleOverlap
        {
            MetaverseObjectId = Guid.NewGuid(), ConnectedSystemId = 1, ConnectedSystemName = "Directory", ObjectTypeName = "User", SyncRuleNames = ["A", "B"]
        };
        var handOver = new ExportRuleHandOver
        {
            ConnectedSystemObjectId = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), ConnectedSystemId = 1, DepartedSyncRuleId = 1,
            DepartedSyncRuleName = "A", CoveringSyncRuleId = 2, CoveringSyncRuleName = "B"
        };
        var conflict = new ExportObjectTypeConflict
        {
            MetaverseObjectId = Guid.NewGuid(), SyncRuleName = "R", TargetObjectTypeName = "Group", ExistingConnectedSystemObjectId = Guid.NewGuid(), ExistingObjectTypeName = "User"
        };
        var recallId = Guid.NewGuid();
        var recall = new ReferenceRecallScopingFailure(recallId, "Why.");

        AssertTranslated(overlap.Describe(),
            "This Metaverse Object is in scope of 2 export Synchronisation Rules that target 'User' in 'Directory': 'A', 'B'. Only one export rule may manage an object, so nothing is exported to that Connected System for it. Give the rules scopes that never overlap, or combine them into one rule.");
        AssertTranslated(handOver.Describe(), "Left the scope of 'A'. 'B' still covers this object, so it now manages the account instead of it being deprovisioned.");
        AssertTranslated(conflict.Describe("Directory"),
            $"Metaverse Object {conflict.MetaverseObjectId}: Synchronisation Rule 'R' targets Connected System Object Type 'Group' in 'Directory', but this Metaverse Object is already represented there by a 'User' object. A Metaverse Object can have only one Connected System Object per Connected System, so nothing was staged for this Rule. Either narrow the Scoping Criteria so that no Metaverse Object satisfies two outbound Synchronisation Rules targeting this Connected System, or give the exported Object Types their own Connected System over the same target.");
        AssertTranslated(recall.Describe(), $"Why. The references Metaverse Object {recallId} held to deleted objects were not removed from its accounts.");
        Assert.That(recall.DescribeForAdministrator(), Is.EqualTo(recall.Describe().ToEnglish()));
    }

    [Test]
    public void AttributeFlowErrors_EveryKind_SaysWhatItAlwaysSaid_AndTranslates()
    {
        AttributeFlowError Error(AttributeFlowErrorKind kind) => new()
        {
            Kind = kind, TargetAttributeName = "mail", SourceAttributeName = "emails", ValueCount = 3, Expression = "mv[\"x\"]", Value = "v",
            MissingInputs = ["a", "b"], SyncRuleName = "Derived", TargetDateTimeResolution = DateTimeResolution.Second, TargetValueCount = 2
        };

        AssertTranslated(ActivityMessage.DescribeAttributeFlowError(Error(AttributeFlowErrorKind.MultiValuedToSingleValued), true, "Directory"),
            "Multi-valued Metaverse source attribute 'emails' has 3 values but target attribute 'mail' on 'Directory' is single-valued, so no Pending Export was generated for this attribute. Map to a multi-valued attribute, reduce the source to a single value, or use an Expression to select one value.");
        AssertTranslated(ActivityMessage.DescribeAttributeFlowError(Error(AttributeFlowErrorKind.MultiValuedToSingleValued), false, null),
            "Multi-valued source attribute 'emails' has 3 values but target attribute 'mail' (derived by Synchronisation Rule 'Derived') is single-valued, so no value was flowed for this attribute. Map to a multi-valued attribute, reduce the source to a single value, or use an Expression to select one value.");
        AssertTranslated(ActivityMessage.DescribeAttributeFlowError(Error(AttributeFlowErrorKind.ExpressionMissingInput), false, null),
            "The Expression for target attribute 'mail' (derived by Synchronisation Rule 'Derived') was not evaluated because this object has no value for a, b, so no value was flowed for this attribute; the object's other attributes were unaffected. Missing Input Behaviour is set to Fail this mapping on this Attribute Flow. Supply the missing value, handle its absence in the Expression, or change the Attribute Flow's Missing Input Behaviour.");
        AssertTranslated(ActivityMessage.DescribeAttributeFlowError(Error(AttributeFlowErrorKind.DatesCollapseAtTargetResolution), true, "Directory"),
            "'emails' holds 3 dates, but target attribute 'mail' on 'Directory' holds dates to the second, where some of them become the same date, so it is sent 2 distinct values. The attribute and the object's other attributes still export. If the dates must stay apart, keep them further apart than the Connected System's resolution, or check the attribute's Date Resolution on the Connected System's Schema tab.");
        AssertTranslated(ActivityMessage.DescribeAttributeFlowError(Error(AttributeFlowErrorKind.CredentialAttributeTarget), true, "Directory"),
            "Target attribute 'mail' on 'Directory' holds credentials, so JIM never exports it through an Attribute Flow and no Pending Export was generated for this attribute; the object's other attributes were unaffected. Remove the Attribute Flow targeting it. JIM delivers passwords only through Password Synchronisation, on Connected Systems whose Connector supports it.");
    }

    [Test]
    public void GeneratedValueMessages_SayWhatTheyAlwaysSaid_AndTranslate()
    {
        AssertTranslated(ActivityMessage.GeneratedValueLostRace("sAMAccountName", "jsmith"),
            "sAMAccountName's generated value \"jsmith\" was issued to another object at the same moment as this one; it will be regenerated the next time this object synchronises.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.GeneratedValueExhausted), "mail", ActivityMessage.Plain(5), "jsmith5",
                ActivityMessage.Create(nameof(ActivityMessages.GeneratedValueAnotherObject))),
            "No free value was found for mail after 5 attempts; the last candidate, \"jsmith5\", is already held by another object.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.GeneratedValueWidthExceeded), "employeeId", "1000", ActivityMessage.Plain(3)),
            "The next value for employeeId would be \"1000\", which no longer fits the configured fixed width of 3 digits.");
    }

    [TestCase(nameof(ActivityMessages.ProgressSavingChanges), "Saving changes")]
    [TestCase(nameof(ActivityMessages.ProgressCreatingCsos), "Creating Connected System Objects")]
    [TestCase(nameof(ActivityMessages.ProgressUpdatingCsos), "Updating Connected System Objects")]
    [TestCase(nameof(ActivityMessages.ProgressProcessingImported), "Processing imported objects")]
    [TestCase(nameof(ActivityMessages.ProgressImportingFromFile), "Importing objects from file")]
    [TestCase(nameof(ActivityMessages.ProgressImportingFromSystem), "Importing objects from Connected System")]
    [TestCase(nameof(ActivityMessages.ProgressRecordingResults), "Recording results")]
    [TestCase(nameof(ActivityMessages.ProgressPreparingDeltaSync), "Preparing delta sync")]
    [TestCase(nameof(ActivityMessages.ProgressProcessingModifiedCsos), "Processing modified Connected System Objects")]
    public void ProgressNarration_EachPhase_SaysWhatItAlwaysSaid_AndTranslates(string key, string english) =>
        AssertTranslated(ActivityMessage.Create(key), english);

    [Test]
    public void ProgressNarration_WithCounts_SaysWhatItAlwaysSaid_AndTranslates()
    {
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ProgressLoadingBatch), ActivityMessage.Plain(2), ActivityMessage.Plain(5), 12345),
            "Loading batch 2 of 5 (12,345 objects in all)");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ProgressExporting), 4200), "Exporting 4,200 changes");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ProgressReadingPage), 1234), "Reading page 1,234");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ProgressImportingFromSystemPage), ActivityMessage.Plain(3)),
            "Importing objects from Connected System (page 3)");
    }

    [Test]
    public void WarningFamilies_ProbeScimAndHierarchy_SayWhatTheyAlwaysSaid_AndTranslate()
    {
        AssertTranslated(ActivityMessage.Counted(nameof(ActivityMessages.ProbeUndeterminedOne), 1, "Directory", "It timed out", ActivityMessage.Plain(1)),
            "JIM couldn't probe Directory for values already in use. It timed out. JIM chose 1 value using its own records only.");
        AssertTranslated(ActivityMessage.Counted(nameof(ActivityMessages.ProbeUndeterminedOne), 4, "Directory", "It timed out", ActivityMessage.Plain(4)),
            "JIM couldn't probe Directory for values already in use. It timed out. JIM chose 4 values using its own records only.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ProbeIncomplete), "Directory", "Only 100 were checked"),
            "JIM's probe of Directory for values already in use was incomplete. Only 100 were checked.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ScimLatestRejection), "Unknown schema"), "Latest rejection: Unknown schema");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ScimRequestsFrom), "Key A"), "SCIM requests from 'Key A'");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.HierarchyNoPartitions)),
            "The hierarchy refresh retrieved no partitions from the Connected System, so the existing hierarchy was left unchanged. This usually indicates a connection, authentication, or scope problem rather than an empty directory; check the Connected System's settings and connectivity, then try again.");
    }

    [Test]
    public void SynchronisationHistoryFailure_CarriesAMessage_ThatSaysExactlyItsText()
    {
        var inner = new InvalidOperationException("db");
        var first = new SynchronisationHistoryPersistenceException(
            SynchronisationHistoryPersistenceException.Describe(2, 5, "Directory"), inner, 2, 5, "Directory");
        var next = SynchronisationHistoryPersistenceException.DescribeNextPage(3, 5, SynchronisationHistoryNextPageOutcome.WriteFailed, "disk full");
        var joined = ActivityMessage.Spaced(first.Detail!, next);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Message, Is.EqualTo(SynchronisationHistoryPersistenceException.BuildMessage(2, 5, "Directory")));
            Assert.That(first.Detail!.ToEnglish(), Is.EqualTo(first.Message));
            Assert.That(joined.ToEnglish(), Is.EqualTo(first.Message + " " + SynchronisationHistoryPersistenceException.BuildNextPageMessage(3, 5, SynchronisationHistoryNextPageOutcome.WriteFailed, "disk full")));
            Assert.That(In(joined, French), Does.Contain("Échec de l’enregistrement"));
            Assert.That(In(joined, German), Does.Contain("konnten nicht aufgezeichnet werden"));
        }
    }

    [Test]
    public void PasswordQueueOutcome_EveryShape_SaysWhatItAlwaysSaid_AndTranslates()
    {
        var enabled = new PasswordQueueTargetOutcome { ConnectedSystemId = 1, ConnectedSystemName = "A", Enabled = true };
        var paused = new PasswordQueueTargetOutcome { ConnectedSystemId = 2, ConnectedSystemName = "B", Enabled = false };

        AssertTranslated(JIM.Application.Servers.PasswordSynchronisationServer.DescribeQueueOutcomeMessage([enabled, paused], PendingPasswordChangeOrigin.Propagated),
            "Password change queued for 2 Connected Systems: A, B. Held until Password Synchronisation is enabled on B; the changes will be delivered then, or expire first.");
        AssertTranslated(JIM.Application.Servers.PasswordSynchronisationServer.DescribeQueueOutcomeMessage([enabled], PendingPasswordChangeOrigin.Explicit),
            "Password set requested for 1 account: A.");
        AssertTranslated(JIM.Application.Servers.PasswordSynchronisationServer.DescribeQueueOutcomeMessage([], PendingPasswordChangeOrigin.Propagated),
            "No Connected System is configured for Password Synchronisation, so this password was not queued for delivery anywhere.");
    }

    [Test]
    public void ExchangeRetryMessages_SayWhatTheyAlwaysSaid_AndTranslate()
    {
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExchangeRetryNothingSingle)), "Nothing was retried: the change is no longer parked or retrying.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExchangeRetryNothingMany)), "Nothing was retried: none of the changes is parked or retrying any more.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExchangeRetryNothingMatched)), "Nothing was retried: no queued Exchange change matched that was parked or retrying.");
        AssertTranslated(ActivityMessage.Counted(nameof(ActivityMessages.ExchangeRetriedOne), 3, ActivityMessage.Plain(3)), "3 queued Exchange changes will be attempted again.");
        AssertTranslated(ActivityMessage.Create(nameof(ActivityMessages.ExchangeRetryRecord)), "Retried; JIM will attempt it again shortly.");
    }
}
