// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// The in-memory lookups an import resolves references through find an object by an anchor value in another case unless
/// the anchor attribute is case-exact (#2051), matching the database fallback behind them.
/// </summary>
[TestFixture]
public class ExternalIdLookupsCaseExactTests
{
    [TestCase(false, true)]
    [TestCase(true, false)]
    public void BuildExternalIdLookups_PrimaryTextAnchorInAnotherCase_FollowsTheDeclaration(bool caseExact, bool expectFound)
    {
        var (type, primary, _) = ObjectType(primaryCaseExact: caseExact);
        var cso = Cso(type, primary, "JBloggs");

        var partition = SyncImportTaskProcessor.BuildExternalIdLookups([cso], []).Partitions[type.Id];

        Assert.That(partition.PrimaryTextLookup!.ContainsKey("jbloggs"), Is.EqualTo(expectFound));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void BuildExternalIdLookups_SecondaryTextAnchorInAnotherCase_FollowsTheDeclaration(bool caseExact, bool expectFound)
    {
        var (type, primary, secondary) = ObjectType(secondaryCaseExact: caseExact);
        var cso = Cso(type, primary, "id-1", secondary, "CN=Joe,DC=PANOPLY");

        var partition = SyncImportTaskProcessor.BuildExternalIdLookups([cso], []).Partitions[type.Id];

        Assert.That(partition.SecondaryTextLookup!.ContainsKey("cn=joe,dc=panoply"), Is.EqualTo(expectFound));
    }

    [Test]
    public void BuildExternalIdLookups_CaseExactAnchor_TwoObjectsDifferingOnlyInCase_AreTwoObjects()
    {
        var (type, primary, _) = ObjectType(primaryCaseExact: true);

        Assert.That(() => SyncImportTaskProcessor.BuildExternalIdLookups([Cso(type, primary, "Abc"), Cso(type, primary, "abc")], []),
            Throws.Nothing);
    }

    [Test]
    public void BuildExternalIdLookups_AnchorIgnoringCase_TwoObjectsDifferingOnlyInCase_IsADuplicate()
    {
        var (type, primary, _) = ObjectType(primaryCaseExact: false);

        Assert.That(() => SyncImportTaskProcessor.BuildExternalIdLookups([Cso(type, primary, "Abc"), Cso(type, primary, "abc")], []),
            Throws.InvalidOperationException);
    }

    private static (ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute Primary, ConnectedSystemObjectTypeAttribute Secondary) ObjectType(
        bool primaryCaseExact = false, bool secondaryCaseExact = false)
    {
        var primary = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "accountId", Type = AttributeDataType.Text, IsExternalId = true, CaseExact = primaryCaseExact };
        var secondary = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "dn", Type = AttributeDataType.Text, IsSecondaryExternalId = true, CaseExact = secondaryCaseExact };
        var type = new ConnectedSystemObjectType { Id = 10, Name = "user", Attributes = [primary, secondary] };
        return (type, primary, secondary);
    }

    private static ConnectedSystemObject Cso(ConnectedSystemObjectType type, ConnectedSystemObjectTypeAttribute primary, string primaryValue,
        ConnectedSystemObjectTypeAttribute? secondary = null, string? secondaryValue = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = type,
            TypeId = type.Id,
            ExternalIdAttributeId = primary.Id,
            SecondaryExternalIdAttributeId = secondary?.Id
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = primary.Id, Attribute = primary, StringValue = primaryValue });
        if (secondary != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = secondary.Id, Attribute = secondary, StringValue = secondaryValue });
        return cso;
    }
}
