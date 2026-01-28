using JIM.Application.Utilities;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Utilities;

/// <summary>
/// Tests for <see cref="AuditHelper"/> to ensure audit fields are correctly stamped on IAuditable entities.
/// </summary>
[TestFixture]
public class AuditHelperTests
{
    #region SetCreated with MetaverseObject

    [Test]
    public void SetCreated_WithUser_SetsTypeIdAndName()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };
        var user = CreateTestUser("Alice Smith");

        // Act
        AuditHelper.SetCreated(entity, user);

        // Assert
        Assert.That(entity.CreatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(entity.CreatedById, Is.EqualTo(user.Id));
        Assert.That(entity.CreatedByName, Is.EqualTo("Alice Smith"));
        Assert.That(entity.Created, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetCreated_WithNullUser_SetsOnlyTimestamp()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };

        // Act
        AuditHelper.SetCreated(entity, (MetaverseObject?)null);

        // Assert
        Assert.That(entity.CreatedByType, Is.EqualTo(ActivityInitiatorType.NotSet));
        Assert.That(entity.CreatedById, Is.Null);
        Assert.That(entity.CreatedByName, Is.Null);
        Assert.That(entity.Created, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetCreated_WithUser_DoesNotModifyLastUpdatedFields()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };
        var user = CreateTestUser("Bob Jones");

        // Act
        AuditHelper.SetCreated(entity, user);

        // Assert
        Assert.That(entity.LastUpdated, Is.Null);
        Assert.That(entity.LastUpdatedByType, Is.EqualTo(ActivityInitiatorType.NotSet));
        Assert.That(entity.LastUpdatedById, Is.Null);
        Assert.That(entity.LastUpdatedByName, Is.Null);
    }

    #endregion

    #region SetCreated with ApiKey

    [Test]
    public void SetCreated_WithApiKey_SetsTypeIdAndName()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "CI/CD Key"
        };

        // Act
        AuditHelper.SetCreated(entity, apiKey);

        // Assert
        Assert.That(entity.CreatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(entity.CreatedById, Is.EqualTo(apiKey.Id));
        Assert.That(entity.CreatedByName, Is.EqualTo("CI/CD Key"));
        Assert.That(entity.Created, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetCreated_WithNullApiKey_ThrowsArgumentNullException()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => AuditHelper.SetCreated(entity, (ApiKey)null!));
    }

    [Test]
    public void SetCreated_WithApiKey_DoesNotModifyLastUpdatedFields()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test Key"
        };

        // Act
        AuditHelper.SetCreated(entity, apiKey);

        // Assert
        Assert.That(entity.LastUpdated, Is.Null);
        Assert.That(entity.LastUpdatedByType, Is.EqualTo(ActivityInitiatorType.NotSet));
        Assert.That(entity.LastUpdatedById, Is.Null);
        Assert.That(entity.LastUpdatedByName, Is.Null);
    }

    #endregion

    #region SetCreatedBySystem

    [Test]
    public void SetCreatedBySystem_SetsSystemTypeAndNullId()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };

        // Act
        AuditHelper.SetCreatedBySystem(entity);

        // Assert
        Assert.That(entity.CreatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(entity.CreatedById, Is.Null);
        Assert.That(entity.CreatedByName, Is.EqualTo("System"));
        Assert.That(entity.Created, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetCreatedBySystem_DoesNotModifyLastUpdatedFields()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };

        // Act
        AuditHelper.SetCreatedBySystem(entity);

        // Assert
        Assert.That(entity.LastUpdated, Is.Null);
        Assert.That(entity.LastUpdatedByType, Is.EqualTo(ActivityInitiatorType.NotSet));
        Assert.That(entity.LastUpdatedById, Is.Null);
        Assert.That(entity.LastUpdatedByName, Is.Null);
    }

    #endregion

    #region SetUpdated with MetaverseObject

    [Test]
    public void SetUpdated_WithUser_SetsLastUpdatedFields()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };
        var user = CreateTestUser("Carol White");

        // Act
        AuditHelper.SetUpdated(entity, user);

        // Assert
        Assert.That(entity.LastUpdatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(entity.LastUpdatedById, Is.EqualTo(user.Id));
        Assert.That(entity.LastUpdatedByName, Is.EqualTo("Carol White"));
        Assert.That(entity.LastUpdated, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetUpdated_WithNullUser_SetsOnlyTimestamp()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };

        // Act
        AuditHelper.SetUpdated(entity, (MetaverseObject?)null);

        // Assert
        Assert.That(entity.LastUpdatedByType, Is.EqualTo(ActivityInitiatorType.NotSet));
        Assert.That(entity.LastUpdatedById, Is.Null);
        Assert.That(entity.LastUpdatedByName, Is.Null);
        Assert.That(entity.LastUpdated, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetUpdated_WithUser_DoesNotModifyCreatedFields()
    {
        // Arrange
        var originalCreated = DateTime.UtcNow.AddDays(-7);
        var entity = new ConnectedSystem
        {
            Name = "Test CS",
            Created = originalCreated,
            CreatedByType = ActivityInitiatorType.System,
            CreatedByName = "System"
        };
        var user = CreateTestUser("Dave Brown");

        // Act
        AuditHelper.SetUpdated(entity, user);

        // Assert
        Assert.That(entity.Created, Is.EqualTo(originalCreated));
        Assert.That(entity.CreatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(entity.CreatedByName, Is.EqualTo("System"));
    }

    #endregion

    #region SetUpdated with ApiKey

    [Test]
    public void SetUpdated_WithApiKey_SetsLastUpdatedFields()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Automation Key"
        };

        // Act
        AuditHelper.SetUpdated(entity, apiKey);

        // Assert
        Assert.That(entity.LastUpdatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(entity.LastUpdatedById, Is.EqualTo(apiKey.Id));
        Assert.That(entity.LastUpdatedByName, Is.EqualTo("Automation Key"));
        Assert.That(entity.LastUpdated, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetUpdated_WithNullApiKey_ThrowsArgumentNullException()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => AuditHelper.SetUpdated(entity, (ApiKey)null!));
    }

    #endregion

    #region SetUpdatedBySystem

    [Test]
    public void SetUpdatedBySystem_SetsSystemType()
    {
        // Arrange
        var entity = new ConnectedSystem { Name = "Test CS" };

        // Act
        AuditHelper.SetUpdatedBySystem(entity);

        // Assert
        Assert.That(entity.LastUpdatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(entity.LastUpdatedById, Is.Null);
        Assert.That(entity.LastUpdatedByName, Is.EqualTo("System"));
        Assert.That(entity.LastUpdated, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void SetUpdatedBySystem_DoesNotModifyCreatedFields()
    {
        // Arrange
        var originalCreated = DateTime.UtcNow.AddDays(-30);
        var entity = new ConnectedSystem
        {
            Name = "Test CS",
            Created = originalCreated,
            CreatedByType = ActivityInitiatorType.User,
            CreatedById = Guid.NewGuid(),
            CreatedByName = "Original Creator"
        };

        // Act
        AuditHelper.SetUpdatedBySystem(entity);

        // Assert
        Assert.That(entity.Created, Is.EqualTo(originalCreated));
        Assert.That(entity.CreatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(entity.CreatedByName, Is.EqualTo("Original Creator"));
    }

    #endregion

    #region Helpers

    private static MetaverseObject CreateTestUser(string displayName)
    {
        var user = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" }
        };
        user.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Name = "Display Name" },
            StringValue = displayName
        });
        return user;
    }

    #endregion
}
