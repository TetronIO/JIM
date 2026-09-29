// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// A small, hand-built Metaverse schema and Synchronisation Rule factory shared by the Metaverse-Derived Attribute
/// Flow tests (#1750): the graph, the validator and the server wiring all reason about the same Person and Group
/// types, so the fixtures read the same way everywhere.
/// </summary>
internal sealed class DerivedFlowTestModel
{
    public const int PersonTypeId = 1;
    public const int GroupTypeId = 2;

    public MetaverseAttribute AccountName { get; } = Attribute(10, "Account Name");
    public MetaverseAttribute Email { get; } = Attribute(11, "Email");
    public MetaverseAttribute UserPrincipalName { get; } = Attribute(12, "User Principal Name");
    public MetaverseAttribute DisplayName { get; } = Attribute(13, "Display Name");
    public MetaverseAttribute MailNickname { get; } = Attribute(14, "Mail Nickname");
    public MetaverseAttribute Region { get; } = Attribute(15, "Region");
    public MetaverseAttribute Manager { get; } = Attribute(16, "Manager", AttributeDataType.Reference);
    public MetaverseAttribute FirstName { get; } = Attribute(17, "First Name");
    public MetaverseAttribute LastName { get; } = Attribute(18, "Last Name");
    public MetaverseAttribute GroupName { get; } = Attribute(20, "Name");

    public MetaverseObjectType Person { get; }
    public MetaverseObjectType Group { get; }

    public DerivedFlowTestModel()
    {
        Person = new MetaverseObjectType
        {
            Id = PersonTypeId,
            Name = "Person",
            PluralName = "People",
            Attributes = [AccountName, Email, UserPrincipalName, DisplayName, MailNickname, Region, Manager, FirstName, LastName]
        };
        // Email is shared with Group deliberately: the graph is keyed per Metaverse Object Type, so a Group rule
        // deriving Email must not leak into Person's levels.
        Group = new MetaverseObjectType
        {
            Id = GroupTypeId,
            Name = "Group",
            PluralName = "Groups",
            Attributes = [GroupName, Email]
        };
    }

    public IReadOnlyList<MetaverseObjectType> Types => [Person, Group];

    public static MetaverseAttribute Attribute(int id, string name, AttributeDataType type = AttributeDataType.Text) => new()
    {
        Id = id,
        Name = name,
        Type = type,
        AttributePlurality = AttributePlurality.SingleValued
    };

    public static SyncRule ImportRule(int id, string name, int connectedSystemId, int metaverseObjectTypeId = PersonTypeId, bool enabled = true) => new()
    {
        Id = id,
        Name = name,
        Direction = SyncRuleDirection.Import,
        ConnectedSystemId = connectedSystemId,
        MetaverseObjectTypeId = metaverseObjectTypeId,
        Enabled = enabled
    };

    public static SyncRule ExportRule(int id, string name, int connectedSystemId, int metaverseObjectTypeId = PersonTypeId) => new()
    {
        Id = id,
        Name = name,
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = connectedSystemId,
        MetaverseObjectTypeId = metaverseObjectTypeId
    };

    /// <summary>
    /// Adds an import expression mapping to <paramref name="rule"/> targeting <paramref name="target"/>.
    /// </summary>
    public static SyncRuleMapping Expression(SyncRule rule, int id, MetaverseAttribute target, string expression, int priority = int.MaxValue, bool enabled = true)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Priority = priority,
            Enabled = enabled
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    /// <summary>
    /// Adds a generated import mapping (#242) whose base expression, if any, is <paramref name="baseExpression"/>.
    /// </summary>
    public static SyncRuleMapping Generated(SyncRule rule, int id, MetaverseAttribute target, string? baseExpression)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken }
        };
        if (baseExpression != null)
            mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = baseExpression });
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    /// <summary>
    /// Adds a plain (non-expression) import attribute mapping from a Connected System attribute.
    /// </summary>
    public static SyncRuleMapping Direct(SyncRule rule, int id, MetaverseAttribute target, string connectedSystemAttributeName)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1000 + id, Name = connectedSystemAttributeName, Type = AttributeDataType.Text }
        });
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }
}
