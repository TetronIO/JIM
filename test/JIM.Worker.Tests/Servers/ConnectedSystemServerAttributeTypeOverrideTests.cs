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
/// The one rule set for an administrator overriding a Connected System attribute's data type (#1354), shared by the
/// portal's Schema tab and the REST API (and so PowerShell): refused for NotSet, on a Connector whose schema states
/// types definitively, and once the attribute is referenced or holds values; and, by the Boolean rule (#2153), an
/// override to Boolean makes the attribute single-valued.
/// </summary>
[TestFixture]
public class ConnectedSystemServerAttributeTypeOverrideTests
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
        ConnectorDefinition = new ConnectorDefinition { Name = "JIM File Connector", SupportsUserSelectedAttributeTypes = supportsOverrides }
    };

    private static ConnectedSystemObjectTypeAttribute Attribute(AttributeDataType type = AttributeDataType.Text, AttributePlurality plurality = AttributePlurality.SingleValued) =>
        new() { Id = 10, Name = "isContractor", Type = type, AttributePlurality = plurality };

    [Test]
    public async Task OverrideAttributeTypeAsync_Allowed_SetsTheTypeAndRecordsItAsTheAdministratorsAsync()
    {
        var attribute = Attribute();

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(System(), attribute, AttributeDataType.Number);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Number));
            Assert.That(attribute.TypeSetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task OverrideAttributeTypeAsync_ToBooleanOnAMultiValuedAttribute_MakesItSingleValuedAsync()
    {
        var attribute = Attribute(plurality: AttributePlurality.MultiValued);

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(System(), attribute, AttributeDataType.Boolean);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.MadeSingleValued, Is.True);
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Boolean));
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public async Task OverrideAttributeTypeAsync_ToAnotherTypeOnAMultiValuedAttribute_KeepsItMultiValuedAsync()
    {
        var attribute = Attribute(plurality: AttributePlurality.MultiValued);

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(System(), attribute, AttributeDataType.DateTime);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MadeSingleValued, Is.False);
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
        }
    }

    [Test]
    public async Task OverrideAttributeTypeAsync_NotSet_IsRefusedAndChangesNothingAsync()
    {
        var attribute = Attribute();

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(System(), attribute, AttributeDataType.NotSet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(attribute.TypeSetByAdministrator, Is.False);
        }
    }

    [Test]
    public async Task OverrideAttributeTypeAsync_ConnectorStatesTypesDefinitively_IsRefusedAsync()
    {
        var attribute = Attribute();

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(System(supportsOverrides: false), attribute, AttributeDataType.Boolean);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Text));
        }
    }

    [Test]
    public async Task OverrideAttributeTypeAsync_NoConnectorDefinition_IsRefusedAsync()
    {
        var connectedSystem = System();
        connectedSystem.ConnectorDefinition = null!;

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(connectedSystem, Attribute(), AttributeDataType.Number);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task OverrideAttributeTypeAsync_ReferencedAttributeToANewType_IsRefusedAndChangesNothingAsync()
    {
        var attribute = Attribute(plurality: AttributePlurality.MultiValued);
        _csRepo.Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(attribute)).ReturnsAsync(true);

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(System(), attribute, AttributeDataType.Boolean);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
        }
    }

    [Test]
    public async Task OverrideAttributeTypeAsync_ReferencedAttributeToItsOwnType_IsAppliedAsync()
    {
        // Confirming the type it already has reinterprets nothing, so references do not block it.
        var attribute = Attribute();
        _csRepo.Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(attribute)).ReturnsAsync(true);

        var result = await _jim.ConnectedSystems.OverrideAttributeTypeAsync(System(), attribute, AttributeDataType.Text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(attribute.TypeSetByAdministrator, Is.True);
        }
        _csRepo.Verify(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()), Times.Never);
    }
}
