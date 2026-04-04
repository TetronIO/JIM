using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests that UpdateMetaverseObjectsAsync correctly deletes attribute values
/// that have been removed from the MVO's AttributeValues collection, even when
/// AutoDetectChangesEnabled is disabled.
///
/// This reproduces a bug where single-valued MVO attributes accumulated multiple
/// values because removed attribute values were not marked as Deleted when
/// AutoDetectChangesEnabled was turned off before SaveChangesAsync.
/// </summary>
[TestFixture]
public class MetaverseRepositoryAttributeValueDeletionTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task UpdateMetaverseObjectsAsync_RemovedAttributeValue_IsDeletedFromDatabase()
    {
        // Arrange — create an MVO with two attribute values for 'Job Title'
        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        _dbContext.MetaverseObjectTypes.Add(mvoType);

        var jobTitleAttr = new MetaverseAttribute
        {
            Name = "Job Title",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _dbContext.MetaverseAttributes.Add(jobTitleAttr);
        await _dbContext.SaveChangesAsync();

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvoType,
            Created = DateTime.UtcNow
        };

        var directorValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = jobTitleAttr,
            AttributeId = jobTitleAttr.Id,
            StringValue = "Director"
        };
        mvo.AttributeValues.Add(directorValue);

        _dbContext.MetaverseObjects.Add(mvo);
        await _dbContext.SaveChangesAsync();

        // Verify initial state — MVO has 1 attribute value
        var initialCount = await _dbContext.MetaverseObjectAttributeValues
            .CountAsync(av => av.MetaverseObject.Id == mvo.Id);
        Assert.That(initialCount, Is.EqualTo(1), "MVO should start with 1 attribute value");

        // Act — simulate what sync does: remove old value, add new value, then persist
        // with AutoDetectChangesEnabled disabled (as the sync processor does)
        mvo.AttributeValues.Remove(directorValue);
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = jobTitleAttr,
            AttributeId = jobTitleAttr.Id,
            StringValue = "Senior Developer"
        });

        // Disable auto-detect changes (reproducing the sync processor's behaviour)
        _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await _repository.Metaverse.UpdateMetaverseObjectsAsync(new List<MetaverseObject> { mvo });
        }
        finally
        {
            _dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
        }

        // Assert — MVO should have exactly 1 attribute value ('Senior Developer'),
        // NOT 2 values ('Director' + 'Senior Developer')
        _dbContext.ChangeTracker.Clear();
        var freshMvo = await _dbContext.MetaverseObjects
            .Include(m => m.AttributeValues)
            .FirstAsync(m => m.Id == mvo.Id);

        Assert.That(freshMvo.AttributeValues, Has.Count.EqualTo(1),
            "MVO should have exactly 1 attribute value after replacing 'Director' with 'Senior Developer'");
        Assert.That(freshMvo.AttributeValues[0].StringValue, Is.EqualTo("Senior Developer"),
            "The remaining attribute value should be 'Senior Developer'");
    }

    [Test]
    public async Task UpdateMetaverseObjectsAsync_RemovedAttributeValue_WithAutoDetectEnabled_IsDeletedFromDatabase()
    {
        // Arrange — same setup but with AutoDetectChanges enabled (control test)
        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        _dbContext.MetaverseObjectTypes.Add(mvoType);

        var jobTitleAttr = new MetaverseAttribute
        {
            Name = "Job Title",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _dbContext.MetaverseAttributes.Add(jobTitleAttr);
        await _dbContext.SaveChangesAsync();

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvoType,
            Created = DateTime.UtcNow
        };

        var directorValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = jobTitleAttr,
            AttributeId = jobTitleAttr.Id,
            StringValue = "Director"
        };
        mvo.AttributeValues.Add(directorValue);

        _dbContext.MetaverseObjects.Add(mvo);
        await _dbContext.SaveChangesAsync();

        // Act — same replacement but with AutoDetectChanges left enabled
        mvo.AttributeValues.Remove(directorValue);
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = jobTitleAttr,
            AttributeId = jobTitleAttr.Id,
            StringValue = "Senior Developer"
        });

        await _repository.Metaverse.UpdateMetaverseObjectsAsync(new List<MetaverseObject> { mvo });

        // Assert — should work correctly with auto-detect enabled
        _dbContext.ChangeTracker.Clear();
        var freshMvo = await _dbContext.MetaverseObjects
            .Include(m => m.AttributeValues)
            .FirstAsync(m => m.Id == mvo.Id);

        Assert.That(freshMvo.AttributeValues, Has.Count.EqualTo(1),
            "MVO should have exactly 1 attribute value after replacing 'Director' with 'Senior Developer'");
        Assert.That(freshMvo.AttributeValues[0].StringValue, Is.EqualTo("Senior Developer"),
            "The remaining attribute value should be 'Senior Developer'");
    }
}
