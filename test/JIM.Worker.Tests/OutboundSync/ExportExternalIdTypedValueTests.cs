// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for issue #1386's root cause: after a successful export, the connector-returned external ID
/// must be stored in the typed slot the anchor attribute declares, not unconditionally in StringValue.
/// A Number anchor stored as a string is invisible to the confirming import's typed diff, which then
/// stages a typed duplicate alongside it; the duplicate kills the run (see
/// <see cref="Servers.CsoChangeRecordExternalIdGuardTests"/>) and, because nothing is ever confirmed,
/// every subsequent synchronisation cycle exports the same objects again, duplicating rows in the
/// customer's target database.
/// </summary>
[TestFixture]
public class ExportExternalIdTypedValueTests
{
    private static ConnectedSystemObjectAttributeValue Apply(AttributeDataType? declaredType, string externalId)
    {
        var value = new ConnectedSystemObjectAttributeValue();
        ExportExecutionServer.ApplyExternalIdToAttributeValue(value, declaredType, externalId, Guid.NewGuid());
        return value;
    }

    [Test]
    public void ApplyExternalIdToAttributeValue_NumberAnchor_StoresIntValueOnly()
    {
        var value = Apply(AttributeDataType.Number, "1000039");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.IntValue, Is.EqualTo(1000039),
                "A Number anchor must store the generated key in IntValue, where the confirming import's typed diff reads it.");
            Assert.That(value.StringValue, Is.Null, "Nothing may be left in the untyped slot.");
            Assert.That(value.LongValue, Is.Null);
            Assert.That(value.GuidValue, Is.Null);
        }
    }

    [Test]
    public void ApplyExternalIdToAttributeValue_LongNumberAnchor_StoresLongValueOnly()
    {
        var value = Apply(AttributeDataType.LongNumber, "5000000001");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.LongValue, Is.EqualTo(5000000001L));
            Assert.That(value.StringValue, Is.Null);
            Assert.That(value.IntValue, Is.Null);
            Assert.That(value.GuidValue, Is.Null);
        }
    }

    [Test]
    public void ApplyExternalIdToAttributeValue_GuidAnchor_StoresGuidValueOnly()
    {
        var guid = Guid.NewGuid();
        var value = Apply(AttributeDataType.Guid, guid.ToString());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.GuidValue, Is.EqualTo(guid));
            Assert.That(value.StringValue, Is.Null);
            Assert.That(value.IntValue, Is.Null);
            Assert.That(value.LongValue, Is.Null);
        }
    }

    [Test]
    public void ApplyExternalIdToAttributeValue_TextAnchor_StoresStringValue()
    {
        var value = Apply(AttributeDataType.Text, "CN=Ada Ashcroft,OU=Corp");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.StringValue, Is.EqualTo("CN=Ada Ashcroft,OU=Corp"));
            Assert.That(value.IntValue, Is.Null);
            Assert.That(value.LongValue, Is.Null);
            Assert.That(value.GuidValue, Is.Null);
        }
    }

    [Test]
    public void ApplyExternalIdToAttributeValue_UnknownAnchorType_FallsBackToStringValue()
    {
        // A null attribute definition (the lookup could not resolve it) has no declared type to parse
        // into; the string preserves the value rather than losing it.
        var value = Apply(null, "opaque-identifier");

        Assert.That(value.StringValue, Is.EqualTo("opaque-identifier"));
    }

    [Test]
    public void ApplyExternalIdToAttributeValue_NumberAnchorWithUnparseableValue_FallsBackToStringValue()
    {
        // A connector returning a non-numeric key for a declared-Number anchor is a connector defect;
        // the value is preserved as a string (and logged as an error) rather than silently dropped.
        // The confirming import cannot match it, which the per-object guard reports rather than throws.
        var value = Apply(AttributeDataType.Number, "not-a-number");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.StringValue, Is.EqualTo("not-a-number"));
            Assert.That(value.IntValue, Is.Null);
        }
    }

    [Test]
    public void ApplyExternalIdToAttributeValue_ReplacingAnExistingValue_ClearsEveryOtherSlot()
    {
        // The export confirm reuses an existing attribute value instance where one is present; a stale
        // value left in another slot would make the instance ambiguous.
        var value = new ConnectedSystemObjectAttributeValue { StringValue = "1000039" };
        ExportExecutionServer.ApplyExternalIdToAttributeValue(value, AttributeDataType.Number, "1000040", Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.IntValue, Is.EqualTo(1000040));
            Assert.That(value.StringValue, Is.Null);
        }
    }
}
