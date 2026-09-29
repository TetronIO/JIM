// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// <see cref="SyncEngine.BuildEffectiveAttributeDictionary"/> (#1750, FR 6): the Metaverse Object's values as of
/// this pass (persisted, minus pending removals, plus pending additions), keyed and typed exactly as the export
/// expression dictionary (<see cref="SyncEngine.BuildAttributeDictionary"/>) so a derived flow and an export
/// expression reading the same attribute see the same value.
/// </summary>
[TestFixture]
public class SyncEngineEffectiveAttributeDictionaryTests
{
    private MetaverseAttribute _accountName = null!;
    private MetaverseAttribute _employeeNumber = null!;
    private MetaverseAttribute _proxyAddresses = null!;
    private MetaverseObject _mvo = null!;

    [SetUp]
    public void SetUp()
    {
        _accountName = new MetaverseAttribute { Id = 1, Name = "Account Name", Type = AttributeDataType.Text };
        _employeeNumber = new MetaverseAttribute { Id = 2, Name = "Employee Number", Type = AttributeDataType.Number };
        _proxyAddresses = new MetaverseAttribute
        {
            Id = 3, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
        };
        _mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [_accountName, _employeeNumber, _proxyAddresses] }
        };
    }

    private MetaverseObjectAttributeValue Value(MetaverseAttribute attribute, string? text = null, int? number = null, bool nullValue = false) => new()
    {
        MetaverseObject = _mvo,
        Attribute = attribute,
        AttributeId = attribute.Id,
        StringValue = text,
        IntValue = number,
        NullValue = nullValue
    };

    [Test]
    public void BuildEffectiveAttributeDictionary_NoPendingChanges_MatchesTheExportDictionary()
    {
        _mvo.AttributeValues.Add(Value(_accountName, "jbloggs"));
        _mvo.AttributeValues.Add(Value(_employeeNumber, number: 42));
        _mvo.AttributeValues.Add(Value(_proxyAddresses, "smtp:a@corp.local"));
        _mvo.AttributeValues.Add(Value(_proxyAddresses, "smtp:b@corp.local"));

        var effective = SyncEngine.BuildEffectiveAttributeDictionary(_mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(effective, Is.EquivalentTo(SyncEngine.BuildAttributeDictionary(_mvo)));
            Assert.That(effective["Employee Number"], Is.TypeOf<int>().And.EqualTo(42), "typed exactly as an export expression sees it");
        }
    }

    [Test]
    public void BuildEffectiveAttributeDictionary_KeysAreCaseInsensitive()
    {
        _mvo.AttributeValues.Add(Value(_accountName, "jbloggs"));

        var effective = SyncEngine.BuildEffectiveAttributeDictionary(_mvo);

        Assert.That(effective["account NAME"], Is.EqualTo("jbloggs"));
    }

    [Test]
    public void BuildEffectiveAttributeDictionary_PendingAdditionReplacingAPendingRemoval_ReadsTheNewValue()
    {
        var old = Value(_accountName, "jbloggs");
        _mvo.AttributeValues.Add(old);
        _mvo.PendingAttributeValueRemovals.Add(old);
        _mvo.PendingAttributeValueAdditions.Add(Value(_accountName, "jbloggs1"));

        var effective = SyncEngine.BuildEffectiveAttributeDictionary(_mvo);

        Assert.That(effective["Account Name"], Is.EqualTo("jbloggs1"));
    }

    [Test]
    public void BuildEffectiveAttributeDictionary_PendingRemovalWithNoReplacement_IsAbsent()
    {
        var old = Value(_accountName, "jbloggs");
        _mvo.AttributeValues.Add(old);
        _mvo.PendingAttributeValueRemovals.Add(old);

        var effective = SyncEngine.BuildEffectiveAttributeDictionary(_mvo);

        Assert.That(effective.ContainsKey("Account Name"), Is.False);
    }

    [Test]
    public void BuildEffectiveAttributeDictionary_NullValueMarkers_AreAbsentWhetherPersistedOrPending()
    {
        _mvo.AttributeValues.Add(Value(_accountName, nullValue: true));
        _mvo.PendingAttributeValueAdditions.Add(Value(_employeeNumber, nullValue: true));

        var effective = SyncEngine.BuildEffectiveAttributeDictionary(_mvo);

        Assert.That(effective, Is.Empty);
    }

    [Test]
    public void BuildEffectiveAttributeDictionary_AfterApplyingThePendingChanges_MatchesTheExportDictionary()
    {
        // The derived pass and export evaluation must agree: what the derived pass reads before the changes are
        // applied is exactly what an export expression reads after.
        var removed = Value(_proxyAddresses, "smtp:old@corp.local");
        _mvo.AttributeValues.Add(Value(_accountName, "jbloggs"));
        _mvo.AttributeValues.Add(removed);
        _mvo.AttributeValues.Add(Value(_proxyAddresses, "smtp:kept@corp.local"));
        _mvo.PendingAttributeValueRemovals.Add(removed);
        _mvo.PendingAttributeValueAdditions.Add(Value(_proxyAddresses, "smtp:new@corp.local"));
        _mvo.PendingAttributeValueAdditions.Add(Value(_employeeNumber, number: 7));

        var effective = SyncEngine.BuildEffectiveAttributeDictionary(_mvo);
        new SyncEngine().ApplyPendingAttributeChanges(_mvo);

        Assert.That(effective, Is.EquivalentTo(SyncEngine.BuildAttributeDictionary(_mvo)));
    }

    [Test]
    public void BuildEffectiveAttributeDictionary_DoesNotMutateTheObject()
    {
        var old = Value(_accountName, "jbloggs");
        _mvo.AttributeValues.Add(old);
        _mvo.PendingAttributeValueRemovals.Add(old);
        _mvo.PendingAttributeValueAdditions.Add(Value(_accountName, "jbloggs1"));

        SyncEngine.BuildEffectiveAttributeDictionary(_mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_mvo.AttributeValues, Has.Count.EqualTo(1));
            Assert.That(_mvo.PendingAttributeValueRemovals, Has.Count.EqualTo(1));
            Assert.That(_mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        }
    }
}
