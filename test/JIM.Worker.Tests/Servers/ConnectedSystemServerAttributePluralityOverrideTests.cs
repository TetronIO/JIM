// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The one rule set for an administrator saying whether a Connected System attribute holds several values (#2186),
/// shared by the portal's Schema tab and the REST API (and so PowerShell): refused on a Connector that reads plurality
/// from its Connected System's schema, for a Boolean (which holds one value, #2153), and once the attribute is
/// referenced or holds values, exactly as a data type override is.
/// </summary>
[TestFixture]
public class ConnectedSystemServerAttributePluralityOverrideTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _csRepo.Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>())).ReturnsAsync(false);
        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim.Dispose();

    private static ConnectedSystem System(bool supportsOverrides = true) => new()
    {
        Id = 1,
        Name = "HR File",
        ConnectorDefinition = new ConnectorDefinition { Name = "JIM File Connector", SupportsUserSelectedAttributePlurality = supportsOverrides }
    };

    private static ConnectedSystemObjectTypeAttribute Attribute(AttributeDataType type = AttributeDataType.Text, AttributePlurality plurality = AttributePlurality.SingleValued) =>
        new() { Id = 10, Name = "member", Type = type, AttributePlurality = plurality };

    [Test]
    public async Task OverrideAttributePluralityAsync_Allowed_SetsThePluralityAndRecordsItAsTheAdministratorsAsync()
    {
        var attribute = Attribute();

        var result = await _jim.ConnectedSystems.OverrideAttributePluralityAsync(System(), attribute, AttributePlurality.MultiValued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(attribute.PluralitySetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task OverrideAttributePluralityAsync_SameAsDiscovered_IsStillRecordedAsTheAdministratorsAsync()
    {
        // Recorded, not inferred from a difference: a later schema refresh must leave the administrator's choice alone.
        var attribute = Attribute();

        var result = await _jim.ConnectedSystems.OverrideAttributePluralityAsync(System(), attribute, AttributePlurality.SingleValued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(attribute.PluralitySetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task OverrideAttributePluralityAsync_ConnectorReadsPluralityFromItsSchema_IsRefusedAndChangesNothingAsync()
    {
        var attribute = Attribute();

        var result = await _jim.ConnectedSystems.OverrideAttributePluralityAsync(System(supportsOverrides: false), attribute, AttributePlurality.MultiValued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(attribute.PluralitySetByAdministrator, Is.False);
        }
    }

    [Test]
    public async Task OverrideAttributePluralityAsync_NoConnectorDefinition_IsRefusedAsync()
    {
        var connectedSystem = System();
        connectedSystem.ConnectorDefinition = null!;

        var result = await _jim.ConnectedSystems.OverrideAttributePluralityAsync(connectedSystem, Attribute(), AttributePlurality.MultiValued);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task OverrideAttributePluralityAsync_MultiValuedBoolean_IsRefusedQuotingTheBooleanRuleAsync()
    {
        var attribute = Attribute(AttributeDataType.Boolean);

        var result = await _jim.ConnectedSystems.OverrideAttributePluralityAsync(System(), attribute, AttributePlurality.MultiValued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain(AttributeDataTypeCapabilities.MultiValuedBooleanRule));
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public async Task OverrideAttributePluralityAsync_ChangeWhileReferencedOrHoldingValues_IsRefusedAsync()
    {
        _csRepo.Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>())).ReturnsAsync(true);
        var attribute = Attribute();

        var result = await _jim.ConnectedSystems.OverrideAttributePluralityAsync(System(), attribute, AttributePlurality.MultiValued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("member"));
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public async Task OverrideAttributePluralityAsync_UnchangedWhileReferenced_IsAllowedAsync()
    {
        // Confirming the plurality it already has reinterprets nothing, so a referenced attribute may still be pinned.
        _csRepo.Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>())).ReturnsAsync(true);
        var attribute = Attribute(plurality: AttributePlurality.MultiValued);

        var result = await _jim.ConnectedSystems.OverrideAttributePluralityAsync(System(), attribute, AttributePlurality.MultiValued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(attribute.PluralitySetByAdministrator, Is.True);
        }
    }
}
