// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="GeneratedValueParticipation.DescribeParticipants"/> (Unique Value Generation, #242, release 3): which
/// Connected System attributes a generated value is exported to, and how each is checked for availability. The rows
/// must say exactly what the engine does, so these pin each check and reason against the same configuration shapes
/// the engine's <see cref="GeneratedValueParticipation.ComputeParticipatingTargets"/> and
/// <see cref="GeneratedValueParticipation.ComputeProbeTargets"/> read.
/// </summary>
[TestFixture]
public class GeneratedValueParticipantsTests
{
    private const int HostSystemId = 1;

    private static MetaverseAttribute Generated() => new() { Id = 1, Name = "Account Name", Type = AttributeDataType.Text };

    private static SyncRuleMapping ImportGeneratedMapping(MetaverseAttribute attribute, params int[] excludedSystemIds)
    {
        var generation = new SyncRuleMappingGeneration { Id = 1 };
        foreach (var systemId in excludedSystemIds)
            generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = systemId });

        return new SyncRuleMapping { Id = 100, TargetMetaverseAttribute = attribute, TargetMetaverseAttributeId = attribute.Id, Generation = generation };
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(int id, string name, AttributeDataType type = AttributeDataType.Text, bool isExternalId = false) =>
        new() { Id = id, Name = name, Type = type, IsExternalId = isExternalId };

    private static SyncRule ExportRule(int connectedSystemId, params SyncRuleMapping[] mappings) => ExportRule(connectedSystemId, true, mappings);

    private static SyncRule ExportRule(int connectedSystemId, bool enabled, params SyncRuleMapping[] mappings)
    {
        var rule = new SyncRule { Id = connectedSystemId * 10 + mappings.Length, ConnectedSystemId = connectedSystemId, Enabled = enabled, Direction = SyncRuleDirection.Export };
        foreach (var mapping in mappings)
        {
            mapping.SyncRule = rule;
            mapping.SyncRuleId = rule.Id;
            rule.AttributeFlowRules.Add(mapping);
        }
        return rule;
    }

    private static SyncRuleMapping Direct(MetaverseAttribute source, ConnectedSystemObjectTypeAttribute target, bool enabled = true) => new()
    {
        Enabled = enabled,
        TargetConnectedSystemAttribute = target,
        TargetConnectedSystemAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttributeId = source.Id, MetaverseAttribute = source } }
    };

    private static SyncRuleMapping ThroughExpression(string expression, ConnectedSystemObjectTypeAttribute target) => new()
    {
        Enabled = true,
        TargetConnectedSystemAttribute = target,
        TargetConnectedSystemAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, Expression = expression } }
    };

    private static GeneratedValueParticipantSystem Probing(int id, string name, params string[] unprobedAttributes) =>
        new(id, name, "JIM LDAP Connector", true, attributeName => !unprobedAttributes.Contains(attributeName, StringComparer.OrdinalIgnoreCase));

    private static GeneratedValueParticipantSystem NotProbing(int id, string name) =>
        new(id, name, "JIM SQL Connector", false, _ => throw new AssertionException("a Connector that cannot probe must not be asked about attributes"));

    private static Dictionary<int, GeneratedValueParticipantSystem> Systems(params GeneratedValueParticipantSystem[] systems) =>
        systems.ToDictionary(s => s.Id);

    [Test]
    public void DescribeParticipants_NotAGeneratedMapping_ReturnsNoRows()
    {
        var attribute = Generated();
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = attribute, TargetMetaverseAttributeId = attribute.Id };
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "sAMAccountName")));

        var rows = GeneratedValueParticipation.DescribeParticipants(mapping, HostSystemId, [rule], Systems(Probing(2, "AD")));

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public void DescribeParticipants_DirectTextFlowToAProbingConnector_IsCheckedByRecordsAndProbeAndCanBeExcluded()
    {
        var attribute = Generated();
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "sAMAccountName")));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemId, Is.EqualTo(2));
            Assert.That(row.ConnectedSystemName, Is.EqualTo("AD"));
            Assert.That(row.ConnectorName, Is.EqualTo("JIM LDAP Connector"));
            Assert.That(row.ConnectedSystemObjectTypeAttributeId, Is.EqualTo(20));
            Assert.That(row.AttributeName, Is.EqualTo("sAMAccountName"));
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsAndProbe));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.None));
            Assert.That(row.IsExcluded, Is.False);
            Assert.That(row.CanBeExcluded, Is.True);
        }
    }

    [Test]
    public void DescribeParticipants_ConnectorCannotProbe_IsCheckedByRecordsOnly()
    {
        var attribute = Generated();
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "login")));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(NotProbing(2, "Payroll"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsOnly));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.ConnectorCannotProbe));
            Assert.That(row.CanBeExcluded, Is.True);
        }
    }

    [Test]
    public void DescribeParticipants_ConnectorDoesNotProbeTheAttribute_IsCheckedByRecordsOnly()
    {
        var attribute = Generated();
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "cn")));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD", "cn"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsOnly));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.AttributeNotProbed));
        }
    }

    [Test]
    public void DescribeParticipants_ExternalIdTarget_IsNotProbed()
    {
        var attribute = Generated();
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "distinguishedName", isExternalId: true)));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsOnly));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.AttributeNotProbed));
        }
    }

    [Test]
    public void DescribeParticipants_NonTextTarget_IsCheckedByRecordsOnly()
    {
        var attribute = new MetaverseAttribute { Id = 1, Name = "Employee Number", Type = AttributeDataType.Number };
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "employeeNumber", AttributeDataType.Number)));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsOnly));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.NotTextValue));
        }
    }

    [Test]
    public void DescribeParticipants_ExportedThroughAnExpression_IsNotCheckedAndCannotBeExcluded()
    {
        var attribute = Generated();
        var rule = ExportRule(2, ThroughExpression("mv[\"account name\"] + \"@corp.example\"", Attribute(21, "userPrincipalName")));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.NotChecked));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.ExportedThroughExpression));
            Assert.That(row.CanBeExcluded, Is.False);
        }
    }

    [Test]
    public void DescribeParticipants_ExpressionThatDoesNotReadTheGeneratedAttribute_IsNotARow()
    {
        var attribute = Generated();
        var rule = ExportRule(2, ThroughExpression("mv[\"Display Name\"]", Attribute(21, "displayName")));

        var rows = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD")));

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public void DescribeParticipants_CombinedWithOtherSources_IsNotCheckedAndCannotBeExcluded()
    {
        var attribute = Generated();
        var other = new MetaverseAttribute { Id = 2, Name = "Department", Type = AttributeDataType.Text };
        var mapping = Direct(attribute, Attribute(22, "info"));
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 1, MetaverseAttributeId = other.Id, MetaverseAttribute = other });
        var rule = ExportRule(2, mapping);

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.NotChecked));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.CombinedWithOtherSources));
            Assert.That(row.CanBeExcluded, Is.False);
        }
    }

    [Test]
    public void DescribeParticipants_ExcludedSystem_IsNotCheckedAndMarkedExcluded()
    {
        var attribute = Generated();
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "sAMAccountName")));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute, 2), HostSystemId, [rule], Systems(Probing(2, "AD"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.NotChecked));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.Excluded));
            Assert.That(row.IsExcluded, Is.True);
            Assert.That(row.CanBeExcluded, Is.True, "an excluded system must stay switchable back on");
        }
    }

    [Test]
    public void DescribeParticipants_DisabledFlowToASystemThatIsNotExcluded_IsNotARow()
    {
        var attribute = Generated();
        var disabledMapping = ExportRule(2, Direct(attribute, Attribute(20, "sAMAccountName"), enabled: false));
        var disabledRule = ExportRule(3, false, Direct(attribute, Attribute(30, "uid")));

        var rows = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [disabledMapping, disabledRule],
            Systems(Probing(2, "AD"), Probing(3, "OpenLDAP")));

        Assert.That(rows, Is.Empty, "the engine ignores disabled flows, so the panel must not list them as checked");
    }

    [Test]
    public void DescribeParticipants_ExcludedSystemWithOnlyADisabledFlow_StaysListedSoTheExclusionCanBeRemoved()
    {
        var attribute = Generated();
        var rule = ExportRule(2, Direct(attribute, Attribute(20, "sAMAccountName"), enabled: false));

        var row = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute, 2), HostSystemId, [rule], Systems(Probing(2, "AD"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.IsExcluded, Is.True);
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.Excluded));
            Assert.That(row.CanBeExcluded, Is.True);
        }
    }

    [Test]
    public void DescribeParticipants_FlowOfADifferentAttribute_IsNotARow()
    {
        var attribute = Generated();
        var other = new MetaverseAttribute { Id = 2, Name = "Department", Type = AttributeDataType.Text };
        var rule = ExportRule(2, Direct(other, Attribute(20, "department")));

        var rows = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [rule], Systems(Probing(2, "AD")));

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public void DescribeParticipants_TwoRulesToTheSameSystemAndAttribute_GiveOneRow()
    {
        var attribute = Generated();
        var target = Attribute(20, "sAMAccountName");
        var first = ExportRule(2, Direct(attribute, target));
        var second = ExportRule(2, Direct(attribute, target), Direct(attribute, Attribute(23, "mailNickname")));

        var rows = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [first, second], Systems(Probing(2, "AD")));

        Assert.That(rows.Select(r => r.AttributeName), Is.EqualTo(new[] { "mailNickname", "sAMAccountName" }));
    }

    [Test]
    public void DescribeParticipants_SeveralSystems_AreOrderedBySystemNameThenAttributeName()
    {
        var attribute = Generated();
        var zeta = ExportRule(2, Direct(attribute, Attribute(20, "uid")));
        var alpha = ExportRule(3, Direct(attribute, Attribute(31, "sAMAccountName")), ThroughExpression("mv[\"Account Name\"] + \"@x\"", Attribute(30, "mail")));

        var rows = GeneratedValueParticipation.DescribeParticipants(ImportGeneratedMapping(attribute), HostSystemId, [zeta, alpha],
            Systems(Probing(2, "Zeta Directory"), Probing(3, "Alpha Directory")));

        Assert.That(rows.Select(r => $"{r.ConnectedSystemName}/{r.AttributeName}"),
            Is.EqualTo(new[] { "Alpha Directory/mail", "Alpha Directory/sAMAccountName", "Zeta Directory/uid" }));
    }

    [Test]
    public void DescribeParticipants_ExportModeGeneratedMapping_IsOneRowForItsOwnTargetThatCannotBeExcluded()
    {
        var target = Attribute(40, "employeeID");
        var mapping = new SyncRuleMapping
        {
            Id = 200,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Generation = new SyncRuleMappingGeneration { Id = 2, TokenKind = GeneratedValueTokenKind.Random }
        };
        var unrelated = ExportRule(3, Direct(Generated(), Attribute(30, "uid")));

        var row = GeneratedValueParticipation.DescribeParticipants(mapping, hostConnectedSystemId: 2, [unrelated],
            Systems(Probing(2, "AD"), Probing(3, "OpenLDAP"))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemId, Is.EqualTo(2));
            Assert.That(row.AttributeName, Is.EqualTo("employeeID"));
            Assert.That(row.Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsAndProbe));
            Assert.That(row.CanBeExcluded, Is.False);
            Assert.That(row.IsExcluded, Is.False);
        }
    }

    [Test]
    public void DescribeParticipants_ProbedRows_AreExactlyTheEnginesProbeTargets()
    {
        // A mixed configuration, every Connector able to probe every attribute, so the panel's "probe" rows can be
        // compared one for one with what the worker hands its probe session.
        var attribute = Generated();
        var mapping = ImportGeneratedMapping(attribute, 5);
        var rules = new[]
        {
            ExportRule(2, Direct(attribute, Attribute(20, "sAMAccountName")), Direct(attribute, Attribute(21, "distinguishedName", isExternalId: true))),
            ExportRule(3, Direct(attribute, Attribute(30, "uid")), ThroughExpression("mv[\"Account Name\"] + \"@x\"", Attribute(31, "mail"))),
            ExportRule(4, Direct(attribute, Attribute(40, "badge", AttributeDataType.Number))),
            ExportRule(5, Direct(attribute, Attribute(50, "login"))),
            ExportRule(6, Direct(attribute, Attribute(60, "user"), enabled: false))
        };
        var systems = Systems(Probing(2, "AD"), Probing(3, "OpenLDAP"), Probing(4, "Badges"), Probing(5, "Excluded"), Probing(6, "Disabled"));

        var probed = GeneratedValueParticipation.DescribeParticipants(mapping, HostSystemId, rules, systems)
            .Where(r => r.Check == GeneratedValueParticipantCheck.JimRecordsAndProbe)
            .Select(r => new UniquenessProbeTarget(r.ConnectedSystemId, r.ConnectedSystemObjectTypeAttributeId));

        Assert.That(probed, Is.EquivalentTo(GeneratedValueParticipation.ComputeProbeTargets(mapping, rules)));
    }

    [Test]
    public void GeneratedValueParticipantEnums_Ordinals_ArePinned()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)GeneratedValueParticipantCheck.JimRecordsAndProbe, Is.EqualTo(0));
            Assert.That((int)GeneratedValueParticipantCheck.JimRecordsOnly, Is.EqualTo(1));
            Assert.That((int)GeneratedValueParticipantCheck.NotChecked, Is.EqualTo(2));
            Assert.That((int)GeneratedValueParticipantReason.None, Is.EqualTo(0));
            Assert.That((int)GeneratedValueParticipantReason.ConnectorCannotProbe, Is.EqualTo(1));
            Assert.That((int)GeneratedValueParticipantReason.AttributeNotProbed, Is.EqualTo(2));
            Assert.That((int)GeneratedValueParticipantReason.NotTextValue, Is.EqualTo(3));
            Assert.That((int)GeneratedValueParticipantReason.ExportedThroughExpression, Is.EqualTo(4));
            Assert.That((int)GeneratedValueParticipantReason.CombinedWithOtherSources, Is.EqualTo(5));
            Assert.That((int)GeneratedValueParticipantReason.Excluded, Is.EqualTo(6));
        }
    }
}
