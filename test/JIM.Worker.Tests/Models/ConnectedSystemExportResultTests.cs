using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Models;

/// <summary>
/// Tests for the ConnectedSystemExportResult model used to capture connector export results.
/// </summary>
[TestFixture]
public class ConnectedSystemExportResultTests
{
    #region Factory Method Tests

    [Test]
    public void Succeeded_WithNoParameters_ReturnsSuccessResultWithNullIds()
    {
        // Act
        var result = ConnectedSystemExportResult.Succeeded();

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorMessage, Is.Null);
        Assert.That(result.ExternalId, Is.Null);
        Assert.That(result.SecondaryExternalId, Is.Null);
    }

    [Test]
    public void Succeeded_WithExternalId_ReturnsSuccessResultWithExternalId()
    {
        // Arrange
        var externalId = "12345678-1234-1234-1234-123456789012";

        // Act
        var result = ConnectedSystemExportResult.Succeeded(externalId);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorMessage, Is.Null);
        Assert.That(result.ExternalId, Is.EqualTo(externalId));
        Assert.That(result.SecondaryExternalId, Is.Null);
    }

    [Test]
    public void Succeeded_WithExternalIdAndSecondaryId_ReturnsSuccessResultWithBothIds()
    {
        // Arrange
        var externalId = "12345678-1234-1234-1234-123456789012";
        var secondaryId = "CN=John Smith,OU=Users,DC=example,DC=com";

        // Act
        var result = ConnectedSystemExportResult.Succeeded(externalId, secondaryId);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorMessage, Is.Null);
        Assert.That(result.ExternalId, Is.EqualTo(externalId));
        Assert.That(result.SecondaryExternalId, Is.EqualTo(secondaryId));
    }

    [Test]
    public void Succeeded_WithNullExternalId_ReturnsSuccessResultWithNullExternalId()
    {
        // Act
        var result = ConnectedSystemExportResult.Succeeded(null);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ExternalId, Is.Null);
    }

    [Test]
    public void Failed_WithErrorMessage_ReturnsFailedResultWithMessage()
    {
        // Arrange
        var errorMessage = "Connection refused by target server";

        // Act
        var result = ConnectedSystemExportResult.Failed(errorMessage);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo(errorMessage));
        Assert.That(result.ExternalId, Is.Null);
        Assert.That(result.SecondaryExternalId, Is.Null);
    }

    [Test]
    public void Failed_WithEmptyErrorMessage_ReturnsFailedResultWithEmptyMessage()
    {
        // Act
        var result = ConnectedSystemExportResult.Failed(string.Empty);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo(string.Empty));
    }

    #endregion

    #region Property Tests

    [Test]
    public void ConnectedSystemExportResult_CanSetAllPropertiesDirectly()
    {
        // Arrange
        var result = new ConnectedSystemExportResult
        {
            Success = true,
            ErrorMessage = "Some message",
            ExternalId = "ext-123",
            SecondaryExternalId = "secondary-456"
        };

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorMessage, Is.EqualTo("Some message"));
        Assert.That(result.ExternalId, Is.EqualTo("ext-123"));
        Assert.That(result.SecondaryExternalId, Is.EqualTo("secondary-456"));
    }

    [Test]
    public void ConnectedSystemExportResult_DefaultValuesAreCorrect()
    {
        // Act
        var result = new ConnectedSystemExportResult();

        // Assert
        Assert.That(result.Success, Is.False); // bool default is false
        Assert.That(result.ErrorMessage, Is.Null);
        Assert.That(result.ExternalId, Is.Null);
        Assert.That(result.SecondaryExternalId, Is.Null);
    }

    #endregion

    #region LDAP-Specific Scenarios

    [Test]
    public void Succeeded_WithObjectGuidAndDn_ReturnsCorrectLdapResult()
    {
        // Arrange - simulating what LDAP connector would return after creating a user
        var objectGuid = Guid.NewGuid().ToString();
        var distinguishedName = "CN=Test User,OU=Users,DC=contoso,DC=com";

        // Act
        var result = ConnectedSystemExportResult.Succeeded(objectGuid, distinguishedName);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ExternalId, Is.EqualTo(objectGuid));
        Assert.That(result.SecondaryExternalId, Is.EqualTo(distinguishedName));

        // Verify the external ID is a valid GUID
        Assert.That(Guid.TryParse(result.ExternalId, out _), Is.True);
    }

    [Test]
    public void Failed_WithLdapError_ReturnsCorrectErrorResult()
    {
        // Arrange - simulating an LDAP error
        var ldapError = "LDAP error code 68: Entry already exists";

        // Act
        var result = ConnectedSystemExportResult.Failed(ldapError);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo(ldapError));
    }

    #endregion
}
