// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for issue #1398's root cause: reference resolution read the referenced Connected System
/// Object's anchor with a coalesce that only knew StringValue, GuidValue and IntValue. An anchor held
/// in LongValue (an Oracle NUMBER(10) primary key discovers as LongNumber) or DecimalValue (a
/// high-precision NUMBER discovers as Decimal, #1283) therefore "resolved" to null, the change was
/// stamped resolved anyway, and the connector then threw per object ("a Reference carrying no anchor
/// value") instead of the export being deferred. Two invariants are asserted here: every typed anchor
/// slot resolves to its value, and a referenced object that genuinely holds no anchor value yet keeps
/// the export deferred rather than letting it reach the connector.
/// </summary>
[TestFixture]
public class ReferenceAnchorResolutionTests
{
    private static ConnectedSystemObjectTypeAttribute ReferenceAttribute() => new()
    {
        Id = 501,
        Name = "MANAGER_ID",
        Type = AttributeDataType.Reference
    };

    private static ConnectedSystemObject CsoWithAnchorValue(ConnectedSystemObjectAttributeValue? anchorValue)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        if (anchorValue != null)
            cso.AttributeValues.Add(anchorValue);
        return cso;
    }

    private static ConnectedSystemObjectAttributeValue AnchorValue(
        AttributeDataType type,
        Action<ConnectedSystemObjectAttributeValue> setValue,
        bool secondary = false)
    {
        var value = new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = secondary ? 601 : 600,
                Name = secondary ? "USER_DN" : "USER_ID",
                Type = type,
                IsExternalId = !secondary,
                IsSecondaryExternalId = secondary
            }
        };
        setValue(value);
        return value;
    }

    private static (PendingExport Export, PendingExportAttributeValueChange Change) ExportReferencing(Guid mvoId)
    {
        var change = new PendingExportAttributeValueChange
        {
            Attribute = ReferenceAttribute(),
            AttributeId = 501,
            UnresolvedReferenceValue = mvoId.ToString()
        };
        var export = new PendingExport();
        export.AttributeValueChanges.Add(change);
        return (export, change);
    }

    [Test]
    public void TryResolveReferencesFromLookup_LongNumberAnchor_ResolvesToTheAnchorString()
    {
        var mvoId = Guid.NewGuid();
        var referencedCso = CsoWithAnchorValue(AnchorValue(AttributeDataType.LongNumber, v => v.LongValue = 5000000123L));
        var (export, change) = ExportReferencing(mvoId);

        var resolved = ExportExecutionServer.TryResolveReferencesFromLookup(
            export, new Dictionary<Guid, ConnectedSystemObject> { [mvoId] = referencedCso });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True,
                "A LongNumber anchor (an Oracle NUMBER(10) primary key) must resolve like any other anchor type.");
            Assert.That(change.StringValue, Is.EqualTo("5000000123"));
            Assert.That(change.UnresolvedReferenceValue, Is.Null);
            Assert.That(change.ResolvedReferenceCsoId, Is.EqualTo(referencedCso.Id));
        }
    }

    [Test]
    public void TryResolveReferencesFromLookup_DecimalAnchor_ResolvesToTheCanonicalAnchorString()
    {
        var mvoId = Guid.NewGuid();
        var referencedCso = CsoWithAnchorValue(AnchorValue(AttributeDataType.Decimal, v => v.DecimalValue = 4200.00m));
        var (export, change) = ExportReferencing(mvoId);

        var resolved = ExportExecutionServer.TryResolveReferencesFromLookup(
            export, new Dictionary<Guid, ConnectedSystemObject> { [mvoId] = referencedCso });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(change.StringValue, Is.EqualTo("4200"),
                "A Decimal anchor must resolve in its canonical form (#1283): 4200.00 and 4200 are the same anchor.");
        }
    }

    [Test]
    public void TryResolveReferencesFromLookup_NumberAnchor_ResolvesToTheAnchorString()
    {
        var mvoId = Guid.NewGuid();
        var referencedCso = CsoWithAnchorValue(AnchorValue(AttributeDataType.Number, v => v.IntValue = 1000039));
        var (export, change) = ExportReferencing(mvoId);

        var resolved = ExportExecutionServer.TryResolveReferencesFromLookup(
            export, new Dictionary<Guid, ConnectedSystemObject> { [mvoId] = referencedCso });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(change.StringValue, Is.EqualTo("1000039"));
        }
    }

    [Test]
    public void TryResolveReferencesFromLookup_AnchorValueRowHoldsNoValue_KeepsTheExportDeferred()
    {
        // The referenced CSO exists and carries an external ID attribute value row, but every value
        // slot is empty (its anchor is database-generated and its own export has not confirmed yet).
        // Stamping the change resolved here is what sent a null anchor to the connector in #1398;
        // the export must stay deferred until the anchor is known.
        var mvoId = Guid.NewGuid();
        var referencedCso = CsoWithAnchorValue(AnchorValue(AttributeDataType.LongNumber, _ => { }));
        var (export, change) = ExportReferencing(mvoId);

        var resolved = ExportExecutionServer.TryResolveReferencesFromLookup(
            export, new Dictionary<Guid, ConnectedSystemObject> { [mvoId] = referencedCso });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.False,
                "An anchor with no value yet is not resolvable; the export must defer, not reach the connector.");
            Assert.That(change.UnresolvedReferenceValue, Is.EqualTo(mvoId.ToString()),
                "The unresolved marker must survive so the deferral machinery retries the export.");
            Assert.That(change.StringValue, Is.Null);
            Assert.That(change.ResolvedReferenceCsoId, Is.Null);
        }
    }

    [Test]
    public void TryResolveReferencesFromLookup_ReferencedCsoAbsent_KeepsTheExportDeferred()
    {
        var mvoId = Guid.NewGuid();
        var (export, change) = ExportReferencing(mvoId);

        var resolved = ExportExecutionServer.TryResolveReferencesFromLookup(
            export, new Dictionary<Guid, ConnectedSystemObject>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.False);
            Assert.That(change.UnresolvedReferenceValue, Is.EqualTo(mvoId.ToString()));
        }
    }

    [Test]
    public void TryResolveReferencesFromLookup_SecondaryExternalIdPresent_IsPreferredOverThePrimary()
    {
        var mvoId = Guid.NewGuid();
        var referencedCso = CsoWithAnchorValue(AnchorValue(AttributeDataType.Number, v => v.IntValue = 1000039));
        referencedCso.AttributeValues.Add(AnchorValue(AttributeDataType.Text,
            v => v.StringValue = "CN=Ada Ashcroft,OU=Corp", secondary: true));
        var (export, change) = ExportReferencing(mvoId);

        var resolved = ExportExecutionServer.TryResolveReferencesFromLookup(
            export, new Dictionary<Guid, ConnectedSystemObject> { [mvoId] = referencedCso });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.True);
            Assert.That(change.StringValue, Is.EqualTo("CN=Ada Ashcroft,OU=Corp"),
                "References are written with the secondary external ID (the DN for LDAP) when one exists.");
        }
    }

    [Test]
    public void ResolveCsoReferenceValue_LongNumberAnchor_ReturnsTheAnchorString()
    {
        // The reference-recall path (#908) resolves the same value with its own copy of the coalesce;
        // it must agree with export execution on every anchor type, or a LongNumber-anchored
        // reference removal is silently dropped as unresolvable.
        var cso = CsoWithAnchorValue(AnchorValue(AttributeDataType.LongNumber, v => v.LongValue = 5000000123L));

        Assert.That(ExportEvaluationServer.ResolveCsoReferenceValue(cso), Is.EqualTo("5000000123"));
    }

    [Test]
    public void ResolveCsoReferenceValue_AnchorValueRowHoldsNoValue_ReturnsNull()
    {
        var cso = CsoWithAnchorValue(AnchorValue(AttributeDataType.LongNumber, _ => { }));

        Assert.That(ExportEvaluationServer.ResolveCsoReferenceValue(cso), Is.Null);
    }
}
