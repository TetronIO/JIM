// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// PRD Scenario 13 (#2053) through a real Full Synchronisation: a multi-valued attribute read by an import Attribute
/// Flow gives every value, a transform applies to each, <c>Where</c> picks one, the values export to a multi-valued
/// attribute as values rather than <c>System.String[]</c>, and several values flowing to a single-valued attribute are
/// an error naming the attribute, the object's other attributes unaffected.
/// </summary>
[TestFixture]
public class MultiValuedExpressionSynchronisationTests : WorkflowTestBase
{
    private const string Primary = "SMTP:priya.shah@example.org";
    private const string Secondary = "smtp:p.shah@example.org";

    [Test]
    public async Task FullSync_Scenario13_ImportsEveryAddressAndThePrimaryAndExportsEveryAddressAsync()
    {
        var ctx = await SetUpAsync(
            ("Proxy Addresses", "Lower(cs[\"proxyAddresses\"])"),
            ("Email", "Replace(Where(cs[\"proxyAddresses\"], \"starts with\", \"SMTP:\"), \"SMTP:\", \"\")"));

        var activity = await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var exports = SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObject?.ConnectedSystemId == ctx.Target.Id).ToList();
        var exportedAddresses = exports.SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.AttributeId == ctx.TargetProxyAddresses.Id)
            .Select(c => c.StringValue)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(StringValues(mvo, "Proxy Addresses"), Is.EquivalentTo(new[] { Primary.ToLowerInvariant(), Secondary }));
            Assert.That(StringValues(mvo, "Email"), Is.EqualTo(new[] { "priya.shah@example.org" }));
            Assert.That(exportedAddresses, Is.EquivalentTo(new[] { Primary.ToLowerInvariant(), Secondary }),
                "every value exports, never System.String[]");
        }
    }

    [Test]
    public async Task FullSync_SeveralValuesIntoASingleValuedAttribute_IsAnErrorNamingTheSourceAttributeAsync()
    {
        var ctx = await SetUpAsync(("Email", "cs[\"proxyAddresses\"]"));

        var activity = await RunFullSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var error = Errors(activity).SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(StringValues(mvo, "Email"), Is.Empty, "the single-valued attribute is given neither value");
            Assert.That(StringValues(mvo, "EmployeeId"), Is.EqualTo(new[] { "EMP001" }), "the object's other attributes still flow");
            Assert.That(error?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.MultiValuedToSingleValued));
            Assert.That(error?.ErrorMessage, Does.Contain("'proxyAddresses'").And.Contain("2 values").And.Contain("Where"));
        }
    }

    private async Task<Context> SetUpAsync(params (string MvAttribute, string Expression)[] flows)
    {
        // HR: one person with two proxy addresses.
        var hr = await CreateConnectedSystemAsync("HR");
        var proxyAddresses = new ConnectedSystemObjectTypeAttribute
        {
            Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            proxyAddresses
        ]);

        // Target: a multi-valued proxyAddresses fed from the Metaverse.
        var target = await CreateConnectedSystemAsync("Directory");
        var targetProxyAddresses = new ConnectedSystemObjectTypeAttribute
        {
            Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var targetType = await CreateCsoTypeAsync(target.Id, "user",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            targetProxyAddresses
        ]);

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvProxyAddresses = await AddMvAttributeAsync(mvType, "Proxy Addresses", AttributePlurality.MultiValued);
        await AddMvAttributeAsync(mvType, "Email", AttributePlurality.SingleValued);

        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var hrEmployeeId = hrType.Attributes.Single(a => a.Name == "EmployeeId");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeId, ConnectedSystemAttributeId = hrEmployeeId.Id } }
        });
        foreach (var (mvAttributeName, expression) in flows)
        {
            var targetAttribute = mvType.Attributes.First(a => a.Name == mvAttributeName);
            importRule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = importRule, TargetMetaverseAttribute = targetAttribute, TargetMetaverseAttributeId = targetAttribute.Id,
                Sources = { new SyncRuleMappingSource { Order = 0, Expression = expression } }
            });
        }

        var exportRule = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "Directory Export");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, TargetConnectedSystemAttribute = targetProxyAddresses, TargetConnectedSystemAttributeId = targetProxyAddresses.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Proxy Addresses\"]" } }
        });
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(hr.Id, hrType, "Priya Shah", "EMP001");
        foreach (var address in new[] { Secondary, Primary })
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = proxyAddresses.Id, Attribute = proxyAddresses, StringValue = address, ConnectedSystemObject = cso
            });
        }

        return new Context(hr, target, targetProxyAddresses, mvProxyAddresses);
    }

    private async Task<MetaverseAttribute> AddMvAttributeAsync(MetaverseObjectType mvType, string name, AttributePlurality plurality)
    {
        var attribute = new MetaverseAttribute
        {
            Name = name,
            Type = AttributeDataType.Text,
            AttributePlurality = plurality,
            MetaverseObjectTypes = [mvType],
            PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(attribute);
        await DbContext.SaveChangesAsync();
        // The in-memory provider's relationship fix-up may already have added it.
        if (!mvType.Attributes.Contains(attribute))
            mvType.Attributes.Add(attribute);
        return attribute;
    }

    private static List<string?> StringValues(MetaverseObject mvo, string attributeName) =>
        mvo.AttributeValues.Where(av => av.Attribute?.Name == attributeName && !av.NullValue).Select(av => av.StringValue).ToList();

    private List<ActivityRunProfileExecutionItem> Errors(Activity activity) => SyncRepo.Rpeis.Values
        .Where(r => r.ActivityId == activity.Id)
        .Concat(activity.RunProfileExecutionItems)
        .DistinctBy(r => r.Id)
        .Where(r => r.ErrorType is not (null or ActivityRunProfileExecutionItemErrorType.NotSet))
        .ToList();

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, $"Full Sync {Guid.NewGuid():N}", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private sealed record Context(ConnectedSystem Hr, ConnectedSystem Target, ConnectedSystemObjectTypeAttribute TargetProxyAddresses, MetaverseAttribute MvProxyAddresses);
}
