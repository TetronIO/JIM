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
    public void Apply_ChangedCaseExact_SetsItAndRecordsItAsTheAdministrators()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text };

        SchemaAttributeEdit.Apply(attribute, supportsUserSelectedTypes: false, typeEditable: false, chosenType: null, caseExact: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.CaseExact, Is.True);
            Assert.That(attribute.CaseExactSetByAdministrator, Is.True);
        }
    }

    [Test]
    public void Apply_UnchangedCaseExact_LeavesItDiscovered()
    {
        // Confirming the dialog to change the data type must not quietly pin the declaration discovery stated.
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text, CaseExact = true };

        SchemaAttributeEdit.Apply(attribute, supportsUserSelectedTypes: true, typeEditable: true, chosenType: AttributeDataType.Text, caseExact: true);

        Assert.That(attribute.CaseExactSetByAdministrator, Is.False);
    }

    [Test]
    public void Apply_TypeOnAConnectorWithoutOverrides_IsIgnored()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text };

        SchemaAttributeEdit.Apply(attribute, supportsUserSelectedTypes: false, typeEditable: true, chosenType: AttributeDataType.Number, caseExact: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(attribute.TypeSetByAdministrator, Is.False);
        }
    }

    [Test]
    public void Apply_TypeOnAReferencedAttribute_IsIgnoredButCaseExactStillApplies()
    {
        // An attribute already referenced or holding values keeps its type, but how its values compare is still
        // the administrator's to declare.
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text };

        SchemaAttributeEdit.Apply(attribute, supportsUserSelectedTypes: true, typeEditable: false, chosenType: AttributeDataType.Number, caseExact: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(attribute.CaseExact, Is.True);
        }
    }

    [Test]
    public void Apply_TypeOverride_SetsItAndRecordsItAsTheAdministrators()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Decimal };

        SchemaAttributeEdit.Apply(attribute, supportsUserSelectedTypes: true, typeEditable: true, chosenType: AttributeDataType.Number, caseExact: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Number));
            Assert.That(attribute.TypeSetByAdministrator, Is.True);
        }
    }

    [Test]
    public void Apply_CaseExactOnAnAttributeBeingRetypedAwayFromText_IsNotDeclared()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text };

        SchemaAttributeEdit.Apply(attribute, supportsUserSelectedTypes: true, typeEditable: true, chosenType: AttributeDataType.Number, caseExact: true);

        Assert.That(attribute.CaseExact, Is.False);
    }
}
