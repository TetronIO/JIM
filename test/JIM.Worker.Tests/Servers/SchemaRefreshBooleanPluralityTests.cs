// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The Boolean rule on the Connected System schema (#2153, S2): a Boolean a Connected System declares multi-valued is
/// materialised as single-valued, following the attribute's effective type (an administrator's override wins over
/// discovery), and an existing attribute that changes is shown as a Plurality definition change.
/// </summary>
[TestFixture]
public class SchemaRefreshBooleanPluralityTests
{
    private const int UserTypeId = 1;
    private const int FlagAttributeId = 10;
    private const int MailAttributeId = 11;

    [Test]
    public void MergeSchemaIntoConnectedSystem_NewObjectTypeDeclaringAMultiValuedBoolean_MaterialisesItSingleValued()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Directory", ConnectorDefinition = new ConnectorDefinition(), ObjectTypes = [] };

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributeDataType.Boolean, AttributePlurality.MultiValued));

        var attributes = connectedSystem.ObjectTypes!.Single().Attributes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributes.Single(a => a.Name == "flag").AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(attributes.Single(a => a.Name == "mail").AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued),
                "Every other type keeps the plurality the Connected System declares.");
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_NewAttributeDeclaringAMultiValuedBoolean_MaterialisesItSingleValued()
    {
        var connectedSystem = ExistingConnectedSystem(includeFlag: false);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributeDataType.Boolean, AttributePlurality.MultiValued));

        var flag = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "flag");
        Assert.That(flag.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ExistingMultiValuedBoolean_BecomesSingleValuedAndIsReportedAsAPluralityChange()
    {
        var connectedSystem = ExistingConnectedSystem(flagType: AttributeDataType.Boolean, flagPlurality: AttributePlurality.MultiValued);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributeDataType.Boolean, AttributePlurality.MultiValued));

        var flag = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "flag");
        var change = result.ChangedAttributes.GetValueOrDefault("user")?.SingleOrDefault(c => c.AttributeName == "flag");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flag.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(flag.Id, Is.EqualTo(FlagAttributeId), "The attribute keeps its id, so mappings survive.");
            Assert.That(change, Is.Not.Null);
            Assert.That(change!.Aspect, Is.EqualTo(SchemaAttributeChangeAspect.Plurality));
            Assert.That(change.OldValue, Is.EqualTo(nameof(AttributePlurality.MultiValued)));
            Assert.That(change.NewValue, Is.EqualTo(nameof(AttributePlurality.SingleValued)));
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_SingleValuedBooleanStillDeclaredMultiValued_IsNotReportedOnEveryRefresh()
    {
        var connectedSystem = ExistingConnectedSystem(flagType: AttributeDataType.Boolean, flagPlurality: AttributePlurality.SingleValued);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributeDataType.Boolean, AttributePlurality.MultiValued));

        var flag = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "flag");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flag.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(result.ChangedAttributes, Is.Empty);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_AdministratorOverrideToBoolean_KeepsTheAttributeSingleValued()
    {
        // Discovery says multi-valued Text; the administrator knows it holds a Boolean. The override is the
        // effective type, so the Boolean rule applies to it.
        var connectedSystem = ExistingConnectedSystem(flagType: AttributeDataType.Boolean, flagPlurality: AttributePlurality.SingleValued, flagTypeSetByAdministrator: true);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributeDataType.Text, AttributePlurality.MultiValued));

        var flag = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "flag");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flag.Type, Is.EqualTo(AttributeDataType.Boolean));
            Assert.That(flag.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(result.ChangedAttributes, Is.Empty);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_AdministratorOverrideAwayFromBoolean_TakesTheDeclaredPlurality()
    {
        // Discovery says multi-valued Boolean; the administrator overrode it to Text, which can hold several values.
        var connectedSystem = ExistingConnectedSystem(flagType: AttributeDataType.Text, flagPlurality: AttributePlurality.SingleValued, flagTypeSetByAdministrator: true);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributeDataType.Boolean, AttributePlurality.MultiValued));

        var flag = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "flag");
        var change = result.ChangedAttributes.GetValueOrDefault("user")?.SingleOrDefault(c => c.AttributeName == "flag");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flag.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(flag.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(change?.Aspect, Is.EqualTo(SchemaAttributeChangeAspect.Plurality));
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ScimPrimaryNoLongerDiscoveredWhileAMappingReadsIt_IsReportedRemovedAndInvalidatesTheMapping()
    {
        // Upgrade and Migration item 5 (#2153): after S2 the SCIM Connector stops discovering roles.primary. A
        // mapping that reads it is not broken silently: the refresh reports the attribute removed, keeps its id in
        // the pre-refresh snapshot (the row itself is retained on apply, #782), and the schema refresh decision
        // names the mapping as invalidated.
        var rolesValue = new ConnectedSystemObjectTypeAttribute { Id = 30, Name = "roles.value", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true };
        var rolesPrimary = new ConnectedSystemObjectTypeAttribute { Id = 31, Name = "roles.primary", Type = AttributeDataType.Boolean, AttributePlurality = AttributePlurality.MultiValued, Selected = true };
        var userType = new ConnectedSystemObjectType { Id = UserTypeId, Name = "User", Selected = true, Attributes = [rolesValue, rolesPrimary] };
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "SCIM", ConnectorDefinition = new ConnectorDefinition(), ObjectTypes = [userType] };

        var rule = new SyncRule { Id = 5, Name = "SCIM Users Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemObjectTypeId = userType.Id, ConnectedSystemObjectType = userType };
        var mapping = new SyncRuleMapping { Id = 50, TargetMetaverseAttribute = new MetaverseAttribute { Id = 500, Name = "Has Primary Role", Type = AttributeDataType.Boolean } };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = rolesPrimary, ConnectedSystemAttributeId = rolesPrimary.Id });
        rule.AttributeFlowRules.Add(mapping);

        var roles = new ScimSchemaAttribute
        {
            Name = "roles",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            SubAttributes =
            [
                new ScimSchemaAttribute { Name = "value", Type = ScimAttributeTypes.String },
                new ScimSchemaAttribute { Name = "primary", Type = ScimAttributeTypes.Boolean }
            ]
        };
        var discovered = new ConnectorSchemaObjectType("User");
        discovered.Attributes.AddRange(ScimAttributeMapper.Flatten(roles, ScimUrns.User).Select(a => a.ToConnectorSchemaAttribute()));

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, new ConnectorSchema { ObjectTypes = [discovered] });
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], DateTime.UtcNow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RemovedAttributes.GetValueOrDefault("User"), Is.EqualTo(new[] { "roles.primary" }));
            Assert.That(result.PreRefreshSchema.Single().Attributes.Single(a => a.Name == "roles.primary").Id, Is.EqualTo(31));
            Assert.That(connectedSystem.ObjectTypes!.Single().Attributes.Select(a => a.Name), Is.EqualTo(new[] { "roles.value" }));
            Assert.That(dependents.InvalidatedMappings.Select(m => m.MappingId), Is.EqualTo(new[] { 50 }));
            Assert.That(dependents.InvalidatedSyncRules, Is.Empty, "The rule's other mappings are unaffected, so only the mapping falls.");
        }
    }

    private static ConnectorSchema DiscoveredSchema(AttributeDataType flagType, AttributePlurality flagPlurality)
    {
        var user = new ConnectorSchemaObjectType("user");
        user.Attributes.Add(new ConnectorSchemaAttribute("flag", flagType, flagPlurality));
        user.Attributes.Add(new ConnectorSchemaAttribute("mail", AttributeDataType.Text, AttributePlurality.MultiValued));
        return new ConnectorSchema { ObjectTypes = [user] };
    }

    private static ConnectedSystem ExistingConnectedSystem(
        AttributeDataType flagType = AttributeDataType.Boolean,
        AttributePlurality flagPlurality = AttributePlurality.SingleValued,
        bool flagTypeSetByAdministrator = false,
        bool includeFlag = true)
    {
        var attributes = new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Id = MailAttributeId, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true }
        };
        if (includeFlag)
        {
            attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Id = FlagAttributeId,
                Name = "flag",
                Type = flagType,
                AttributePlurality = flagPlurality,
                TypeSetByAdministrator = flagTypeSetByAdministrator,
                Selected = true
            });
        }

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Directory",
            ConnectorDefinition = new ConnectorDefinition(),
            ObjectTypes = [new ConnectedSystemObjectType { Id = UserTypeId, Name = "user", Selected = true, Attributes = attributes }]
        };
    }
}
