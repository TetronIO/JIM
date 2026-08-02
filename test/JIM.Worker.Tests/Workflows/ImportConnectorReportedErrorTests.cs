// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Connector can report that an object it imported has a problem, by setting ErrorType and ErrorMessage
/// on the <see cref="ConnectedSystemImportObject"/>. JIM must surface that on the Activity rather than
/// dropping it, and must honour the severity the Connector chose: an object-level problem means the object
/// cannot be imported at all, whereas an attribute-level problem means one value did not parse and the rest
/// of the object is sound.
/// </summary>
[TestFixture]
public class ImportConnectorReportedErrorTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_ConnectorCouldNotDetermineObjectType_ReportsItAndSkipsTheObjectAsync()
    {
        var context = await ArrangeAsync();
        var importObjects = new List<ConnectedSystemImportObject>
        {
            BuildImportObject(context.CsoType, "EXT-000001"),
            Flag(BuildImportObject(context.CsoType, "EXT-000002"),
                ConnectedSystemImportObjectError.CouldNotDetermineObjectType,
                "Couldn't match object type 'Widget' to the one(s) selected in the schema.")
        };

        await RunImportAsync(context, importObjects);

        var errored = SingleErroredRpei(context.Activity);
        Assert.That(errored.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.CouldNotMatchObjectType));
        Assert.That(errored.ErrorMessage, Does.Contain("Couldn't match object type 'Widget'"),
            "The Connector's own message is what tells the administrator which object type was unrecognised");

        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(1), "The healthy object should import; the flagged one should not");
    }

    [Test]
    public async Task FullImport_ConnectorReportedMissingExternalIdAttributes_ReportsItAndSkipsTheObjectAsync()
    {
        var context = await ArrangeAsync();
        var importObjects = new List<ConnectedSystemImportObject>
        {
            BuildImportObject(context.CsoType, "EXT-000001"),
            Flag(BuildImportObject(context.CsoType, "EXT-000002"),
                ConnectedSystemImportObjectError.ExternalIdAttributes,
                "The external ID attribute was not present on the object returned by the directory.")
        };

        await RunImportAsync(context, importObjects);

        var errored = SingleErroredRpei(context.Activity);
        Assert.That(errored.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.MissingExternalIdAttributeValue));

        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(1));
    }

    [Test]
    public async Task FullImport_ConnectorReportedConfigurationError_ReportsItAndSkipsTheObjectAsync()
    {
        var context = await ArrangeAsync();
        var importObjects = new List<ConnectedSystemImportObject>
        {
            BuildImportObject(context.CsoType, "EXT-000001"),
            Flag(BuildImportObject(context.CsoType, "EXT-000002"),
                ConnectedSystemImportObjectError.ConfigurationError,
                "No attributes are selected for this object type.")
        };

        await RunImportAsync(context, importObjects);

        var errored = SingleErroredRpei(context.Activity);
        Assert.That(errored.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ConnectorConfigurationError));

        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(1));
    }

    [Test]
    public async Task FullImport_ConnectorReportedAttributeValueError_ReportsItAndStillImportsTheObjectAsync()
    {
        // An attribute-level problem is not an object-level one: the object's identity and every other
        // attribute parsed, and the Connector deliberately chose to return it rather than stop. Skipping it
        // would freeze the whole identity over one malformed value, and for a new joiner would mean never
        // provisioning them at all.
        var context = await ArrangeAsync();
        var importObjects = new List<ConnectedSystemImportObject>
        {
            BuildImportObject(context.CsoType, "EXT-000001"),
            Flag(BuildImportObject(context.CsoType, "EXT-000002"),
                ConnectedSystemImportObjectError.AttributeValueError,
                "Failed to parse 'startDate' as DateTime: the string was not recognised as a valid DateTime.")
        };

        await RunImportAsync(context, importObjects);

        var errored = SingleErroredRpei(context.Activity);
        Assert.That(errored.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError));
        Assert.That(errored.ErrorMessage, Does.Contain("startDate"),
            "The administrator needs to know which attribute did not flow");

        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(2), "The object imports with the values that did parse");

        // The Run Profile Execution Item must carry both the error and the object's outcome; recording the
        // error must not cost the administrator the record of what happened to the object.
        Assert.That(errored.ObjectChangeType, Is.EqualTo(ObjectChangeType.Added));
        Assert.That(errored.ConnectedSystemObject, Is.Not.Null);
    }

    [Test]
    public async Task FullImport_WithoutConnectorReportedErrors_RecordsNoErrorsAsync()
    {
        var context = await ArrangeAsync();
        var importObjects = new List<ConnectedSystemImportObject>
        {
            BuildImportObject(context.CsoType, "EXT-000001"),
            BuildImportObject(context.CsoType, "EXT-000002")
        };

        await RunImportAsync(context, importObjects);

        Assert.That(ErroredRpeis(context.Activity), Is.Empty);
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(2));
    }

    [Test]
    public async Task FullImport_ObjectTypeNotInTheSchema_ReportsItRatherThanThrowingAsync()
    {
        // Regression: collecting external IDs for deletion detection resolved the object type with
        // Single(), so an object naming a type absent from the schema took the whole import down with an
        // unhandled exception before any object could be reported.
        var context = await ArrangeAsync();
        var unknownTypeObject = BuildImportObject(context.CsoType, "EXT-000002");
        unknownTypeObject.ObjectType = "Widget";

        var importObjects = new List<ConnectedSystemImportObject>
        {
            BuildImportObject(context.CsoType, "EXT-000001"),
            unknownTypeObject
        };

        Assert.DoesNotThrowAsync(async () => await RunImportAsync(context, importObjects));

        var errored = SingleErroredRpei(context.Activity);
        Assert.That(errored.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.CouldNotMatchObjectType));

        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(context.ConnectedSystem.Id);
        Assert.That(csoCount, Is.EqualTo(1), "The healthy object should still import");
    }

    #region Helpers

    private sealed record ImportContext(
        ConnectedSystem ConnectedSystem,
        ConnectedSystemObjectType CsoType,
        ConnectedSystemRunProfile RunProfile,
        Activity Activity);

    private async Task<ImportContext> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        return new ImportContext(connectedSystem, csoType, runProfile, activity);
    }

    private async Task RunImportAsync(ImportContext context, List<ConnectedSystemImportObject> importObjects)
    {
        var connector = new MockErrorReportingConnector(importObjects);
        var workerTask = new SynchronisationWorkerTask(context.ConnectedSystem.Id, context.RunProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = context.Activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, context.ConnectedSystem, context.RunProfile, workerTask,
            new CancellationTokenSource());

        await processor.PerformImportAsync();
    }

    private static List<ActivityRunProfileExecutionItem> ErroredRpeis(Activity activity) =>
        activity.RunProfileExecutionItems
            .Where(r => r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();

    private static ActivityRunProfileExecutionItem SingleErroredRpei(Activity activity)
    {
        var errored = ErroredRpeis(activity);
        Assert.That(errored, Has.Count.EqualTo(1),
            "Exactly one object was flagged, so exactly one Run Profile Execution Item should carry an error");
        return errored[0];
    }

    private static ConnectedSystemImportObject BuildImportObject(ConnectedSystemObjectType csoType, string externalId)
    {
        var externalIdAttribute = csoType.Attributes.First(a => a.IsExternalId);
        return new ConnectedSystemImportObject
        {
            ObjectType = csoType.Name,
            ChangeType = ObjectChangeType.Created,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = externalIdAttribute.Name,
                    Type = externalIdAttribute.Type,
                    GuidValues = externalIdAttribute.Type == AttributeDataType.Guid ? [Guid.NewGuid()] : [],
                    StringValues = externalIdAttribute.Type == AttributeDataType.Text ? [externalId] : []
                }
            ]
        };
    }

    private static ConnectedSystemImportObject Flag(
        ConnectedSystemImportObject importObject,
        ConnectedSystemImportObjectError errorType,
        string errorMessage)
    {
        importObject.ErrorType = errorType;
        importObject.ErrorMessage = errorMessage;
        return importObject;
    }

    /// <summary>
    /// Returns a single page of caller-supplied import objects, some of which may carry Connector-reported
    /// errors, exactly as the File and LDAP Connectors do.
    /// </summary>
    private class MockErrorReportingConnector(List<ConnectedSystemImportObject> importObjects)
        : IConnector, IConnectorImportUsingCalls
    {
        public string Name => "MockConnector";
        public string? Description => null;
        public string? Url => null;

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger) { }

        public void CloseImportConnection() { }

        public Task<ConnectedSystemImportResult> ImportAsync(
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData,
            ILogger logger,
            CancellationToken cancellationToken,
            IConnectorProgress progress)
        {
            return Task.FromResult(new ConnectedSystemImportResult { ImportObjects = importObjects });
        }
    }

    #endregion
}
