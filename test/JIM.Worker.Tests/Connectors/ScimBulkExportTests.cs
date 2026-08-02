// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Connectors.SCIM;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Connectors.MockScim;
using static JIM.Worker.Tests.Connectors.ScimExportTestObjects;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Export over <c>/Bulk</c> (RFC 7644 section 3.7): the same Pending Exports, carried to the provider a
/// batch at a time instead of one request each.
/// <para>
/// Bulk buys throughput and nothing else, so every test here is really about what it must not cost.
/// JIM pairs an outcome with a change by position in the returned list, and a bulk response is neither
/// ordered nor guaranteed complete; an outcome attributed to the wrong object, or an unreported
/// operation read as a success, would record a change as applied that never happened. That is the
/// corrupted state the Synchronisation Integrity rules exist to prevent, so it is asserted directly
/// rather than inferred from a happy path.
/// </para>
/// </summary>
[TestFixture]
public class ScimBulkExportTests
{
    #region harness
    /// <summary>
    /// A provider that advertises bulk, which is the precondition for the connector using it at all.
    /// </summary>
    private static MockScimProvider BulkProvider()
    {
        var provider = new MockScimProvider();
        provider.Options.SupportsBulk = true;
        return provider;
    }

    private static List<ConnectedSystemSettingValue> BulkEnabled()
    {
        return
        [
            new ConnectedSystemSettingValue
            {
                Setting = new ConnectorDefinitionSetting { Name = ScimConnectorConstants.SettingUseBulkOperations },
                CheckboxValue = true
            }
        ];
    }

    private static async Task<List<ConnectedSystemExportResult>> ExportAsync(
        StubHttpMessageHandler handler,
        List<ConnectedSystemSettingValue> settings,
        params PendingExport[] pendingExports)
    {
        var connector = new StubbedTransportScimConnector(handler);
        connector.OpenExportConnection(settings);

        try
        {
            return await connector.ExportAsync(pendingExports, CancellationToken.None, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }
    }

    private static List<StubHttpMessageHandler.RecordedRequest> BulkRequests(StubHttpMessageHandler handler)
    {
        return handler.Requests
            .Where(r => r.RequestUri!.AbsolutePath.EndsWith("/Bulk", StringComparison.Ordinal))
            .ToList();
    }

    private static int ResourceWriteCount(StubHttpMessageHandler handler)
    {
        return handler.Requests.Count(r =>
            r.Method != HttpMethod.Get
            && !r.RequestUri!.AbsolutePath.EndsWith("/Bulk", StringComparison.Ordinal));
    }

    private static PendingExport NewUser(string userName)
    {
        var user = ObjectType("User");
        return Create(user, Change("userName", user, userName, PendingExportAttributeChangeType.Add));
    }
    #endregion

    #region when bulk is used at all
    [Test]
    public async Task ExportAsync_BulkEnabledAndAdvertised_SendsOneRequestForTheWholeBatchAsync()
    {
        var provider = BulkProvider();
        var handler = provider.CreateHandler();

        var results = await ExportAsync(handler, BulkEnabled(), NewUser("ada"), NewUser("grace"), NewUser("katherine"));

        Assert.Multiple(() =>
        {
            Assert.That(BulkRequests(handler), Has.Count.EqualTo(1));
            Assert.That(ResourceWriteCount(handler), Is.Zero, "the resources should have travelled inside the bulk request, not beside it");
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }

    [Test]
    public async Task ExportAsync_BulkEnabledButTheProviderDoesNotAdvertiseIt_SendsPerObjectRequestsAsync()
    {
        // A capability the provider has not promised is not one to use: guessing turns a discovery gap
        // into failed exports, and per-object requests are already correct.
        var provider = new MockScimProvider();
        var handler = provider.CreateHandler();

        var results = await ExportAsync(handler, BulkEnabled(), NewUser("ada"), NewUser("grace"));

        Assert.Multiple(() =>
        {
            Assert.That(BulkRequests(handler), Is.Empty);
            Assert.That(ResourceWriteCount(handler), Is.EqualTo(2));
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }

    [Test]
    public async Task ExportAsync_BulkAdvertisedButNotEnabled_SendsPerObjectRequestsAsync()
    {
        // Bulk is opt-in. Per-object export is complete and correct, whereas a provider that implements
        // bulk badly misreports outcomes JIM then records as authoritative, and there is no safe retreat
        // once a batch has partly applied. That trade is the administrator's to make, not JIM's.
        var provider = BulkProvider();
        var handler = provider.CreateHandler();

        var results = await ExportAsync(handler, [], NewUser("ada"), NewUser("grace"));

        Assert.Multiple(() =>
        {
            Assert.That(BulkRequests(handler), Is.Empty);
            Assert.That(ResourceWriteCount(handler), Is.EqualTo(2));
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }
    #endregion

    #region correlating outcomes to changes
    [Test]
    public async Task ExportAsync_Bulk_ReturnsTheIdTheProviderAssignedToEachCreateAsync()
    {
        // Without the id JIM cannot update or delete the object later, and the confirming import would
        // create a second Connected System Object for the same resource.
        var provider = BulkProvider();
        var handler = provider.CreateHandler();

        var results = await ExportAsync(handler, BulkEnabled(), NewUser("ada"), NewUser("grace"));

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.ExternalId), Is.EqualTo(new[] { "generated-1", "generated-2" }));
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_AttributesEachOutcomeToTheChangeThatProducedItAsync()
    {
        // Nothing in RFC 7644 promises the response lists operations in request order, so a connector
        // pairing them by position would report the middle object's failure against the first object.
        var provider = BulkProvider();
        provider.Options.ReturnsBulkOperationsOutOfOrder = true;
        provider.AddUser("ada-id", "ada");

        var user = ObjectType("User");
        var handler = provider.CreateHandler();

        // The rejected object is deliberately first and the batch deliberately even, so pairing outcomes
        // by position would move the failure somewhere else rather than land it back where it started.
        var results = await ExportAsync(
            handler,
            BulkEnabled(),
            Against("no-such-id", user, PendingExportChangeType.Update, Change("displayName", user, "Nobody")),
            Against("ada-id", user, PendingExportChangeType.Update, Change("displayName", user, "Ada Lovelace")),
            NewUser("grace"),
            NewUser("katherine"));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False, "the failure belongs to the object the provider rejected");
            Assert.That(results[1].Success, Is.True);
            Assert.That(results.Skip(1).Select(r => r.ExternalId), Is.EqualTo(new[] { "ada-id", "generated-1", "generated-2" }));
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_ReturnsOneResultPerPendingExportInTheOrderTheyArrivedAsync()
    {
        // JIM pairs a result with a Pending Export by position and treats a short list as success for
        // whatever is missing off the end, so the count and the order are a contract, not a convention.
        var provider = BulkProvider();
        provider.AddUser("ada-id", "ada");
        provider.AddUser("grace-id", "grace");

        var user = ObjectType("User");
        var handler = provider.CreateHandler();

        var results = await ExportAsync(
            handler,
            BulkEnabled(),
            NewUser("katherine"),
            Against("ada-id", user, PendingExportChangeType.Update, Change("displayName", user, "Ada Lovelace")),
            Against("grace-id", user, PendingExportChangeType.Delete));

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0].ExternalId, Is.EqualTo("generated-1"));
            Assert.That(results[1].ExternalId, Is.EqualTo("ada-id"));
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_WhenTheProviderEchoesNoBulkId_CorrelatesOnWhatTheOperationTargetedAsync()
    {
        // RFC 7644 section 3.7 only requires bulkId on a POST, so a provider omitting it elsewhere is
        // conformant. Losing track of every update and delete over that would make bulk unusable against
        // it, and each operation still names the one resource it addressed.
        var provider = BulkProvider();
        provider.Options.OmitsBulkIdInResponses = true;
        provider.AddUser("ada-id", "ada");
        provider.AddUser("grace-id", "grace");

        var user = ObjectType("User");
        var handler = provider.CreateHandler();

        var results = await ExportAsync(
            handler,
            BulkEnabled(),
            Against("ada-id", user, PendingExportChangeType.Update, Change("displayName", user, "Ada Lovelace")),
            Against("grace-id", user, PendingExportChangeType.Update, Change("displayName", user, "Grace Hopper")));

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That(results.Select(r => r.ExternalId), Is.EqualTo(new[] { "ada-id", "grace-id" }));
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_AnOperationTheProviderDidNotReportOnIsFailedRatherThanAssumedAppliedAsync()
    {
        // A provider that stops early says nothing about what it never reached. Silence is not consent:
        // recording those changes as exported would leave JIM believing it had applied them, and the
        // Pending Exports would be deleted with the changes still absent from the provider.
        var provider = BulkProvider();
        provider.Options.BulkOperationsOmittedFromResponse = 1;
        var handler = provider.CreateHandler();

        var results = await ExportAsync(handler, BulkEnabled(), NewUser("ada"), NewUser("grace"), NewUser("katherine"));

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[1].Success, Is.True);
            Assert.That(results[2].Success, Is.False);
            Assert.That(results[2].ErrorMessage, Does.Contain("did not report"));
        });
    }
    #endregion

    #region staying inside the provider's limits
    [Test]
    public async Task ExportAsync_Bulk_KeepsWithinTheProvidersMaximumOperationsPerRequestAsync()
    {
        // RFC 7644 section 3.7 makes the advertised limits binding, and a provider rejects an oversized
        // batch outright, so exceeding it fails every change in it rather than merely being impolite.
        var provider = BulkProvider();
        provider.Options.BulkMaxOperations = 2;
        var handler = provider.CreateHandler();

        var results = await ExportAsync(
            handler, BulkEnabled(),
            NewUser("ada"), NewUser("grace"), NewUser("katherine"), NewUser("dorothy"), NewUser("margaret"));

        Assert.Multiple(() =>
        {
            Assert.That(BulkRequests(handler), Has.Count.EqualTo(3));
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_KeepsWithinTheProvidersMaximumPayloadSizeAsync()
    {
        var provider = BulkProvider();
        provider.Options.BulkMaxPayloadSize = 400;
        var handler = provider.CreateHandler();

        var results = await ExportAsync(
            handler, BulkEnabled(),
            NewUser("ada"), NewUser("grace"), NewUser("katherine"), NewUser("dorothy"), NewUser("margaret"));

        Assert.Multiple(() =>
        {
            Assert.That(BulkRequests(handler), Has.Count.GreaterThan(1), "one request would have exceeded the provider's payload limit");
            Assert.That(BulkRequests(handler).Select(r => r.Body!.Length), Is.All.LessThanOrEqualTo(400));
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_WhenTheProviderRefusesABatchAsTooLarge_SplitsItRatherThanFailingTheChangesAsync()
    {
        // A provider that enforces a limit it never advertised refuses the request outright, having
        // applied none of it. Treating that like a failure of unknown outcome would strand the changes:
        // the next run would size the batch from the same discovery document and be refused again, so
        // the export could never succeed against that provider at all.
        var provider = BulkProvider();
        provider.Options.BulkMaxOperationsEnforcedButNotAdvertised = 2;
        var handler = provider.CreateHandler();

        var results = await ExportAsync(
            handler, BulkEnabled(),
            NewUser("ada"), NewUser("grace"), NewUser("katherine"), NewUser("dorothy"));

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That(results.Select(r => r.ExternalId), Is.Unique, "each create should have its own provider-assigned id");
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_WhenTheProviderStatesNoLimits_StillBoundsTheBatchAsync()
    {
        // Bulk support without a stated maximum is common, and an unbounded batch is a payload whose
        // size is decided by whatever the export pipeline happened to hand over.
        var provider = BulkProvider();
        var handler = provider.CreateHandler();

        var pendingExports = Enumerable.Range(0, ScimConnectorConstants.DefaultBulkMaxOperations + 1)
            .Select(i => NewUser($"user{i}"))
            .ToArray();

        var results = await ExportAsync(handler, BulkEnabled(), pendingExports);

        Assert.Multiple(() =>
        {
            Assert.That(BulkRequests(handler), Has.Count.EqualTo(2));
            Assert.That(results.Select(r => r.Success), Is.All.True);
        });
    }
    #endregion

    #region when the bulk request itself fails
    [Test]
    public async Task ExportAsync_Bulk_WhenTheProviderNeverImplementedTheEndpoint_FallsBackToPerObjectRequestsAsync()
    {
        // Advertising bulk and serving no endpoint is a provider bug worth surviving rather than
        // failing a run over, and it is the one whole-request failure where nothing can have applied,
        // which is what makes resending the changes individually safe.
        var provider = BulkProvider();
        provider.Options.BulkEndpointStatus = HttpStatusCode.NotImplemented;
        var handler = provider.CreateHandler();

        var results = await ExportAsync(handler, BulkEnabled(), NewUser("ada"), NewUser("grace"));

        Assert.Multiple(() =>
        {
            Assert.That(ResourceWriteCount(handler), Is.EqualTo(2));
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That(results.Select(r => r.ExternalId), Is.EqualTo(new[] { "generated-1", "generated-2" }));
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_WhenTheProviderFailsPartWayThrough_TheBatchIsReportedFailedRatherThanResentAsync()
    {
        // A 500 from /Bulk says nothing about how far the provider got. Resending the operations
        // individually would create every resource the failed batch had already created, so the changes
        // are reported failed and left for the next run, which the confirming import reconciles first.
        var provider = BulkProvider();
        provider.Options.BulkEndpointStatus = HttpStatusCode.InternalServerError;
        var handler = provider.CreateHandler();

        var results = await ExportAsync(handler, BulkEnabled(), NewUser("ada"), NewUser("grace"));

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => r.Success), Is.All.False);
            Assert.That(ResourceWriteCount(handler), Is.Zero, "resending would risk applying the changes twice");
        });
    }
    #endregion

    #region classifying what an operation reported
    [Test]
    public async Task ExportAsync_Bulk_AStaleEntityTagIsReportedAsAConcurrencyConflictAsync()
    {
        // The provider refused because the resource moved on since JIM read it. Retrying blindly just
        // races again, so the conflict is named for what it is and the next import reconciles it.
        var provider = BulkProvider();
        provider.AddUser("ada-id", "ada");

        var user = ObjectType("User");
        var pendingExport = Against("ada-id", user, PendingExportChangeType.Update, Change("displayName", user, "Ada Lovelace"));
        WithImportedEntityTag(pendingExport, user, "W/\"stale\"");

        var results = await ExportAsync(provider.CreateHandler(), BulkEnabled(), pendingExport);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.ConcurrencyConflict));
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_SendsTheEntityTagAsTheOperationsVersionAsync()
    {
        var provider = BulkProvider();
        var ada = provider.AddUser("ada-id", "ada");

        var user = ObjectType("User");
        var pendingExport = Against("ada-id", user, PendingExportChangeType.Update, Change("displayName", user, "Ada Lovelace"));
        WithImportedEntityTag(pendingExport, user, ada.Version);

        var handler = provider.CreateHandler();
        var results = await ExportAsync(handler, BulkEnabled(), pendingExport);

        Assert.Multiple(() =>
        {
            Assert.That(BulkRequests(handler)[0].Body, Does.Contain("version"));
            Assert.That(results[0].Success, Is.True);
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_AMissingDependencyIsClassifiedFromTheOperationsErrorAsync()
    {
        // RFC 7644 makes the client responsible for creating dependencies first, so this says the
        // referenced object has not been exported yet rather than that the data is wrong.
        var provider = BulkProvider();
        provider.Options.RejectsCreateWithMissingDependency = true;

        var results = await ExportAsync(provider.CreateHandler(), BulkEnabled(), NewUser("ada"));

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.MissingDependency));
        });
    }

    [Test]
    public async Task ExportAsync_Bulk_ADeleteOfAResourceAlreadyGoneSucceedsAsync()
    {
        // The intended end state is that the resource is absent, and it is. Failing would leave a
        // Pending Export retrying for ever against a provider that has already done what was asked.
        var provider = BulkProvider();
        var user = ObjectType("User");

        var results = await ExportAsync(
            provider.CreateHandler(), BulkEnabled(),
            Against("already-gone", user, PendingExportChangeType.Delete));

        Assert.That(results[0].Success, Is.True);
    }
    #endregion

    [Test]
    public void GetSettings_DeclaresUseBulkOperationsAsAnOptInExportSetting()
    {
        var setting = new ScimConnector().GetSettings()
            .SingleOrDefault(s => s.Name == ScimConnectorConstants.SettingUseBulkOperations);

        Assert.That(setting, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(setting!.Type, Is.EqualTo(ConnectedSystemSettingType.CheckBox));
            Assert.That(setting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Export));
            Assert.That(setting.DefaultCheckboxValue, Is.Not.True, "bulk is opt-in, so an administrator has to choose it");
        });
    }
}
