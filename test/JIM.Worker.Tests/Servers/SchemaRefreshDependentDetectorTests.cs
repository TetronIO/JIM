// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The schema refresh decision (#1485) turns a destructive diff into a disable plan: which Synchronisation
/// Rules and Attribute Flow mappings the removals and definition changes invalidate, with the reason each one
/// would be disabled for. These tests pin the detection: a rule falls with its Object Type, a mapping falls
/// with the attribute it reads (directly or through an Expression) or writes, a retyped attribute invalidates
/// the mappings validated against the old definition, and Object Matching Rules are reported for display.
/// </summary>
[TestFixture]
public class SchemaRefreshDependentDetectorTests
{
    private static readonly DateTime RefreshedAt = new(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

    private ConnectedSystemObjectType _userType = null!;
    private ConnectedSystemObjectType _computerType = null!;
    private ConnectedSystemObjectTypeAttribute _faxAttr = null!;
    private ConnectedSystemObjectTypeAttribute _mailAttr = null!;
    private ConnectedSystemObjectTypeAttribute _employeeNumberAttr = null!;

    [SetUp]
    public void SetUp()
    {
        _faxAttr = new ConnectedSystemObjectTypeAttribute { Id = 201, Name = "faxNumber", Type = AttributeDataType.Text };
        _mailAttr = new ConnectedSystemObjectTypeAttribute { Id = 202, Name = "mail", Type = AttributeDataType.Text };
        _employeeNumberAttr = new ConnectedSystemObjectTypeAttribute { Id = 203, Name = "employeeNumber", Type = AttributeDataType.Number };
        _userType = new ConnectedSystemObjectType { Id = 1, Name = "user", Attributes = [_faxAttr, _mailAttr, _employeeNumberAttr] };
        _computerType = new ConnectedSystemObjectType { Id = 2, Name = "computer", Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 301, Name = "hostName", Type = AttributeDataType.Text }] };
    }

    [Test]
    public void Detect_RuleBoundToARemovedObjectType_IsInvalidatedWithItsMappingsCounted()
    {
        var result = new SchemaRefreshResult { Success = true, RemovedObjectTypes = ["computer"] };
        var rule = ImportRule(10, "Directory Computers Inbound", _computerType);
        rule.AttributeFlowRules.Add(MappingReading(100, _computerType.Attributes[0]));
        rule.AttributeFlowRules.Add(MappingReading(101, _computerType.Attributes[0]));

        SeedPreRefreshSchema(result);
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], RefreshedAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependents.InvalidatedSyncRules, Has.Count.EqualTo(1));
            Assert.That(dependents.InvalidatedSyncRules[0].SyncRuleId, Is.EqualTo(10));
            Assert.That(dependents.InvalidatedSyncRules[0].MappingCount, Is.EqualTo(2));
            Assert.That(dependents.InvalidatedSyncRules[0].Reason, Does.Contain("computer").And.Contain("no longer reported"));
            Assert.That(dependents.InvalidatedMappings, Is.Empty,
                "A falling rule takes its mappings with it; listing them separately would double-count the same disable.");
            Assert.That(dependents.HasAny, Is.True);
        }
    }

    [Test]
    public void Detect_ImportMappingReadingARemovedAttribute_IsInvalidated()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.RemovedAttributes["user"] = ["faxNumber"];
        var rule = ImportRule(11, "HR Users Inbound", _userType);
        rule.AttributeFlowRules.Add(MappingReading(102, _faxAttr));
        rule.AttributeFlowRules.Add(MappingReading(103, _mailAttr));

        SeedPreRefreshSchema(result);
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], RefreshedAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependents.InvalidatedSyncRules, Is.Empty);
            Assert.That(dependents.InvalidatedMappings, Has.Count.EqualTo(1));
            Assert.That(dependents.InvalidatedMappings[0].MappingId, Is.EqualTo(102));
            Assert.That(dependents.InvalidatedMappings[0].Reason, Does.Contain("faxNumber").And.Contain("no longer reported"));
            Assert.That(dependents.InvalidatedMappings[0].ViaExpression, Is.False);
        }
    }

    [Test]
    public void Detect_ImportExpressionConsumingARemovedAttribute_IsInvalidated()
    {
        // Expressions reference attributes by name inside their text, not through a source foreign key, so
        // detection has to resolve the Expression's inputs; a mapping consuming the attribute this way is as
        // broken as one mapping it directly.
        var result = new SchemaRefreshResult { Success = true };
        result.RemovedAttributes["user"] = ["faxNumber"];
        var rule = ImportRule(12, "HR Users Inbound", _userType);
        var expressionMapping = new SyncRuleMapping
        {
            Id = 104,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 900, Name = "Contact Details", Type = AttributeDataType.Text }
        };
        expressionMapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "cs[\"mail\"] + \" / \" + cs[\"faxNumber\"]" });
        rule.AttributeFlowRules.Add(expressionMapping);

        SeedPreRefreshSchema(result);
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], RefreshedAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependents.InvalidatedMappings, Has.Count.EqualTo(1));
            Assert.That(dependents.InvalidatedMappings[0].MappingId, Is.EqualTo(104));
            Assert.That(dependents.InvalidatedMappings[0].ViaExpression, Is.True);
            Assert.That(dependents.InvalidatedMappings[0].Reason, Does.Contain("Expression").And.Contain("faxNumber"));
        }
    }

    [Test]
    public void Detect_ExportMappingWritingARemovedAttribute_IsInvalidated()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.RemovedAttributes["user"] = ["faxNumber"];
        var rule = new SyncRule
        {
            Id = 13,
            Name = "Directory Users Outbound",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = _userType.Id,
            ConnectedSystemObjectType = _userType
        };
        var mapping = new SyncRuleMapping { Id = 105, TargetConnectedSystemAttribute = _faxAttr, TargetConnectedSystemAttributeId = _faxAttr.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = new MetaverseAttribute { Id = 901, Name = "Fax Number", Type = AttributeDataType.Text } });
        rule.AttributeFlowRules.Add(mapping);

        SeedPreRefreshSchema(result);
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], RefreshedAt);

        Assert.That(dependents.InvalidatedMappings.Select(m => m.MappingId), Is.EqualTo(new[] { 105 }));
    }

    [Test]
    public void Detect_MappingOverARetypedAttribute_IsInvalidatedWithTheChangeNamed()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.AddChangedAttribute("user", new SchemaAttributeDefinitionChange
        {
            AttributeName = "employeeNumber",
            Aspect = SchemaAttributeChangeAspect.DataType,
            OldValue = "Number",
            NewValue = "Text"
        });
        var rule = ImportRule(14, "HR Users Inbound", _userType);
        rule.AttributeFlowRules.Add(MappingReading(106, _employeeNumberAttr));

        SeedPreRefreshSchema(result);
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], RefreshedAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependents.InvalidatedMappings, Has.Count.EqualTo(1));
            Assert.That(dependents.InvalidatedMappings[0].Reason,
                Does.Contain("employeeNumber").And.Contain("Number").And.Contain("Text"));
        }
    }

    [Test]
    public void Detect_ObjectMatchingRuleReferencingARemovedAttribute_IsReportedForDisplay()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.RemovedAttributes["user"] = ["mail"];
        var rule = ImportRule(15, "HR Users Inbound", _userType);
        var matchingRule = new ObjectMatchingRule { Id = 50 };
        matchingRule.Sources.Add(new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = _mailAttr, ConnectedSystemAttributeId = _mailAttr.Id });
        rule.ObjectMatchingRules.Add(matchingRule);

        SeedPreRefreshSchema(result);
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], RefreshedAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependents.ReferencedObjectMatchingRules, Has.Count.EqualTo(1));
            Assert.That(dependents.ReferencedObjectMatchingRules[0].ObjectMatchingRuleId, Is.EqualTo(50));
            Assert.That(dependents.ReferencedObjectMatchingRules[0].Reason, Does.Contain("mail"));
        }
    }

    [Test]
    public void Detect_AdditionsOnly_FindsNothing()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.AddedAttributes["user"] = ["mobile"];
        var rule = ImportRule(16, "HR Users Inbound", _userType);
        rule.AttributeFlowRules.Add(MappingReading(107, _mailAttr));

        SeedPreRefreshSchema(result);
        var dependents = SchemaRefreshDependentDetector.Detect(result, [rule], RefreshedAt);

        Assert.That(dependents.HasAny, Is.False);
    }

    /// <summary>
    /// The pre-refresh schema snapshot the merge captures in production: the detector resolves removal names
    /// against it, because the merged graph no longer holds removed entries.
    /// </summary>
    private void SeedPreRefreshSchema(SchemaRefreshResult result) =>
        result.PreRefreshSchema = new List<SchemaRefreshPreRefreshType>
        {
            new()
            {
                Id = _userType.Id,
                Name = _userType.Name,
                Attributes = _userType.Attributes.Select(a => new SchemaRefreshPreRefreshAttribute { Id = a.Id, Name = a.Name }).ToList()
            },
            new()
            {
                Id = _computerType.Id,
                Name = _computerType.Name,
                Attributes = _computerType.Attributes.Select(a => new SchemaRefreshPreRefreshAttribute { Id = a.Id, Name = a.Name }).ToList()
            }
        };

    private static SyncRule ImportRule(int id, string name, ConnectedSystemObjectType type) => new()
    {
        Id = id,
        Name = name,
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        ConnectedSystemObjectTypeId = type.Id,
        ConnectedSystemObjectType = type
    };

    private static SyncRuleMapping MappingReading(int id, ConnectedSystemObjectTypeAttribute attribute)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 800 + id, Name = $"MV {attribute.Name}", Type = AttributeDataType.Text }
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = attribute, ConnectedSystemAttributeId = attribute.Id });
        return mapping;
    }
}
