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

    [TestCase(ConnectedSystemExportErrorType.InvalidGeneratedExternalId, ActivityRunProfileExecutionItemErrorType.InvalidGeneratedExternalId)]
    [TestCase(ConnectedSystemExportErrorType.ClassMembershipRequirementsNotMet, ActivityRunProfileExecutionItemErrorType.ClassMembershipRequirementsNotMet)]
    [TestCase(ConnectedSystemExportErrorType.General, ActivityRunProfileExecutionItemErrorType.UnhandledError)]
    [TestCase(null, ActivityRunProfileExecutionItemErrorType.UnhandledError)]
    public void ToExecutionItemErrorType_OtherClassifications_KeepTheirExistingMapping(ConnectedSystemExportErrorType? exportErrorType, ActivityRunProfileExecutionItemErrorType expected)
    {
        Assert.That(SyncExportTaskProcessor.ToExecutionItemErrorType(exportErrorType), Is.EqualTo(expected));
    }
}
