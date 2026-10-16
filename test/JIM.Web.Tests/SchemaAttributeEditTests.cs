// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the Schema tab's attribute dialog offers and what confirming it changes (#2051). The data type override is
/// a Connector capability; the case-exact declaration is offered for every Text attribute on every Connector.
/// </summary>
[TestFixture]
public class SchemaAttributeEditTests
{
    [TestCase(AttributeDataType.Text, false, true, Description = "Case-exact alone is reason enough to open the dialog")]
    [TestCase(AttributeDataType.Number, false, false, Description = "Nothing to edit: no type override, no case")]
    [TestCase(AttributeDataType.Number, true, true, Description = "The type override is")]
    public void CanEdit_DependsOnTheTypeAndTheConnector(AttributeDataType type, bool supportsUserSelectedTypes, bool expected)
    {
        Assert.That(SchemaAttributeEdit.CanEdit(new ConnectedSystemObjectTypeAttribute { Type = type }, supportsUserSelectedTypes), Is.EqualTo(expected));
    }

    [TestCase(AttributeDataType.Text, true)]
    [TestCase(AttributeDataType.Reference, false)]
    [TestCase(AttributeDataType.Guid, false)]
    public void OffersCaseExact_OnlyForText(AttributeDataType type, bool expected)
    {
        Assert.That(SchemaAttributeEdit.OffersCaseExact(type), Is.EqualTo(expected));
    }

    [Test]
    public void ApplyCaseExact_Changed_SetsItAndRecordsItAsTheAdministrators()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text };

        SchemaAttributeEdit.ApplyCaseExact(attribute, caseExact: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.CaseExact, Is.True);
            Assert.That(attribute.CaseExactSetByAdministrator, Is.True);
        }
    }

    [Test]
    public void ApplyCaseExact_Unchanged_LeavesItDiscovered()
    {
        // Confirming the dialog to change the data type must not quietly pin the declaration discovery stated.
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text, CaseExact = true };

        SchemaAttributeEdit.ApplyCaseExact(attribute, caseExact: true);

        Assert.That(attribute.CaseExactSetByAdministrator, Is.False);
    }

    [Test]
    public void ApplyCaseExact_OnAnAttributeRetypedAwayFromText_IsNotDeclared()
    {
        // The type override is applied first (by the server), so a retyped attribute is no longer Text here.
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number };

        SchemaAttributeEdit.ApplyCaseExact(attribute, caseExact: true);

        Assert.That(attribute.CaseExact, Is.False);
    }
}
