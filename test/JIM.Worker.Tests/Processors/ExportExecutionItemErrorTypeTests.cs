// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// How an export's classified failure is recorded on its Run Profile Execution Item. A classification a Connector
/// made must survive into the recorded error: flattened to Unhandled Error it reads as a JIM defect and fails the
/// Activity, when the Connected System simply refused the data.
/// </summary>
[TestFixture]
public class ExportExecutionItemErrorTypeTests
{
    [Test]
    public void ToExecutionItemErrorType_ValueAlreadyInUse_IsRecordedAsValueAlreadyInUse()
    {
        Assert.That(SyncExportTaskProcessor.ToExecutionItemErrorType(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse),
            Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UniqueValueAlreadyInUse));
    }

    [Test]
    public void ToExecutionItemErrorType_PasswordRequiredToEnableAccount_IsRecordedAsPasswordRequiredToEnableAccount()
    {
        // Active Directory refusing to enable an account with no password (#2039) is configuration to act on, not a
        // JIM defect, so it must not fall into the Unhandled Error bucket that fails the Activity.
        Assert.That(SyncExportTaskProcessor.ToExecutionItemErrorType(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount),
            Is.EqualTo(ActivityRunProfileExecutionItemErrorType.PasswordRequiredToEnableAccount));
    }

    // #2118: each of these is a Connected System saying no, or JIM refusing a write it would not be able to read back,
    // and each names something an administrator can act on.
    [TestCase(ConnectedSystemExportErrorType.OutsideManagedScope, ActivityRunProfileExecutionItemErrorType.OutsideManagedScope)]
    [TestCase(ConnectedSystemExportErrorType.AccessDenied, ActivityRunProfileExecutionItemErrorType.AccessDenied)]
    [TestCase(ConnectedSystemExportErrorType.RejectedByConnectedSystem, ActivityRunProfileExecutionItemErrorType.RejectedByConnectedSystem)]
    [TestCase(ConnectedSystemExportErrorType.PlaceholderMemberConstraintViolation, ActivityRunProfileExecutionItemErrorType.PlaceholderMemberConstraintViolation)]
    [TestCase(ConnectedSystemExportErrorType.MissingDependency, ActivityRunProfileExecutionItemErrorType.MissingDependency)]
    [TestCase(ConnectedSystemExportErrorType.ConcurrencyConflict, ActivityRunProfileExecutionItemErrorType.ConcurrencyConflict)]
    public void ToExecutionItemErrorType_ConnectedSystemRefusal_IsRecordedAsItsOwnErrorType(ConnectedSystemExportErrorType exportErrorType, ActivityRunProfileExecutionItemErrorType expected)
    {
        Assert.That(SyncExportTaskProcessor.ToExecutionItemErrorType(exportErrorType), Is.EqualTo(expected));
    }

    [TestCase(ConnectedSystemExportErrorType.InvalidGeneratedExternalId, ActivityRunProfileExecutionItemErrorType.InvalidGeneratedExternalId)]
    [TestCase(ConnectedSystemExportErrorType.ClassMembershipRequirementsNotMet, ActivityRunProfileExecutionItemErrorType.ClassMembershipRequirementsNotMet)]
    [TestCase(ConnectedSystemExportErrorType.General, ActivityRunProfileExecutionItemErrorType.UnhandledError)]
    [TestCase(null, ActivityRunProfileExecutionItemErrorType.UnhandledError)]
    public void ToExecutionItemErrorType_OtherClassifications_KeepTheirExistingMapping(ConnectedSystemExportErrorType? exportErrorType, ActivityRunProfileExecutionItemErrorType expected)
    {
        Assert.That(SyncExportTaskProcessor.ToExecutionItemErrorType(exportErrorType), Is.EqualTo(expected));
    }

    /// <summary>
    /// A Connector classifies a failure precisely so it is not reported as a JIM defect. Every classification but
    /// General must therefore reach its own execution item error type; one left to the default arm is the very fault
    /// #2118 found four of.
    /// </summary>
    [Test]
    public void ToExecutionItemErrorType_EveryClassificationButGeneral_IsNotRecordedAsUnhandledError()
    {
        var flattened = Enum.GetValues<ConnectedSystemExportErrorType>()
            .Where(type => type != ConnectedSystemExportErrorType.General)
            .Where(type => SyncExportTaskProcessor.ToExecutionItemErrorType(type) == ActivityRunProfileExecutionItemErrorType.UnhandledError)
            .ToList();

        Assert.That(flattened, Is.Empty, "these classifications are recorded as Unhandled Error, which reads as a JIM defect");
    }
}
