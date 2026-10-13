// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Every expression dictionary reads every value of a multi-valued attribute (#2053, PRD Phase 2 requirement 8).
/// Import used to read the first stored value and export, derived flows and drift detection the last (drift detection
/// now reads through export's own dictionary, #2221); directories
/// hold such attributes as unordered sets, so either was arbitrary and could change after another import.
/// </summary>
[TestFixture]
public class ExpressionDictionaryMultiValuedTests
{
    private const string Primary = "SMTP:priya.shah@example.org";
    private const string Secondary = "smtp:p.shah@example.org";

    [Test]
    public void BuildCsoAttributeDictionary_SeveralValues_ReadsEveryValueInOrdinalOrder()
    {
        var proxyAddresses = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Name = "user", Attributes = [proxyAddresses] };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = csoType };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 1, Attribute = proxyAddresses, StringValue = Secondary });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 1, Attribute = proxyAddresses, StringValue = Primary });

        var read = SyncEngine.BuildCsoAttributeDictionary(cso, csoType)["proxyAddresses"];

        Assert.That(read, Is.InstanceOf<ExpressionValues>());
        Assert.That((ExpressionValues)read!, Is.EqualTo(new object[] { Primary, Secondary }));
    }

    [Test]
    public void BuildCsoAttributeDictionary_OneValue_ReadsAsItself()
    {
        var mail = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var csoType = new ConnectedSystemObjectType { Id = 1, Name = "user", Attributes = [mail] };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = csoType };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 1, Attribute = mail, StringValue = "a@example.org" });

        Assert.That(SyncEngine.BuildCsoAttributeDictionary(cso, csoType)["mail"], Is.EqualTo("a@example.org"));
    }

    [Test]
    public void BuildAttributeDictionary_Metaverse_SeveralValues_ReadsEveryValue()
    {
        var mvo = MetaverseObjectWithProxyAddresses(out _);

        var read = SyncEngine.BuildAttributeDictionary(mvo)["Proxy Addresses"];

        Assert.That((ExpressionValues)read!, Is.EqualTo(new object[] { Primary, Secondary }));
    }

    [Test]
    public void BuildEffectiveAttributeDictionary_SeveralValues_ReadsEveryValue()
    {
        var mvo = MetaverseObjectWithProxyAddresses(out _);

        var read = SyncEngine.BuildEffectiveAttributeDictionary(mvo)["Proxy Addresses"];

        Assert.That((ExpressionValues)read!, Is.EqualTo(new object[] { Primary, Secondary }));
    }

    [Test]
    public void BuildAttributeDictionary_Metaverse_NullMarkerAmongValues_IsNotAValue()
    {
        var mvo = MetaverseObjectWithProxyAddresses(out var attribute);
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = attribute, AttributeId = attribute.Id, NullValue = true });

        Assert.That(((ExpressionValues)SyncEngine.BuildAttributeDictionary(mvo)["Proxy Addresses"]!).Count, Is.EqualTo(2));
    }

    private static MetaverseObject MetaverseObjectWithProxyAddresses(out MetaverseAttribute attribute)
    {
        attribute = new MetaverseAttribute
        {
            Id = 3, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
        };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [attribute] }
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = attribute, AttributeId = attribute.Id, StringValue = Secondary });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = attribute, AttributeId = attribute.Id, StringValue = Primary });
        return mvo;
    }
}
