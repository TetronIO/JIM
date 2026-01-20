using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class HierarchyRefreshResultTests
{
    #region GetSummary Tests

    [Test]
    public void GetSummary_WithNoChanges_ReturnsNoChanges()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            TotalPartitions = 5,
            TotalContainers = 20
        };

        // Act
        var summary = result.GetSummary();

        // Assert
        Assert.That(summary, Is.EqualTo("No changes"));
    }

    [Test]
    public void GetSummary_WithOnlyAdditions_ReturnsAddedCount()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            AddedPartitions = { new HierarchyChangeItem { Name = "P1", ExternalId = "DC=test" } },
            AddedContainers = { new HierarchyChangeItem { Name = "C1", ExternalId = "OU=Users" } }
        };

        // Act
        var summary = result.GetSummary();

        // Assert
        Assert.That(summary, Is.EqualTo("2 added"));
    }

    [Test]
    public void GetSummary_WithOnlyRemovals_ReturnsRemovedCount()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            RemovedContainers =
            {
                new HierarchyChangeItem { Name = "C1", ExternalId = "OU=Old1" },
                new HierarchyChangeItem { Name = "C2", ExternalId = "OU=Old2" },
                new HierarchyChangeItem { Name = "C3", ExternalId = "OU=Old3" }
            }
        };

        // Act
        var summary = result.GetSummary();

        // Assert
        Assert.That(summary, Is.EqualTo("3 removed"));
    }

    [Test]
    public void GetSummary_WithMixedChanges_ReturnsAllCounts()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            AddedContainers = { new HierarchyChangeItem { Name = "New", ExternalId = "OU=New" } },
            RemovedContainers = { new HierarchyChangeItem { Name = "Old", ExternalId = "OU=Old" } },
            RenamedContainers = { new HierarchyRenameItem { OldName = "A", NewName = "B", ExternalId = "OU=X" } },
            MovedContainers = { new HierarchyMoveItem { Name = "Moved", ExternalId = "OU=M" } }
        };

        // Act
        var summary = result.GetSummary();

        // Assert
        Assert.That(summary, Is.EqualTo("1 added, 1 removed, 2 updated"));
    }

    #endregion

    #region HasChanges Tests

    [Test]
    public void HasChanges_WithNoChanges_ReturnsFalse()
    {
        // Arrange
        var result = new HierarchyRefreshResult { Success = true };

        // Act & Assert
        Assert.That(result.HasChanges, Is.False);
    }

    [Test]
    public void HasChanges_WithAddedPartition_ReturnsTrue()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            AddedPartitions = { new HierarchyChangeItem { Name = "P1", ExternalId = "DC=test" } }
        };

        // Act & Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void HasChanges_WithRemovedContainer_ReturnsTrue()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            RemovedContainers = { new HierarchyChangeItem { Name = "C1", ExternalId = "OU=test" } }
        };

        // Act & Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void HasChanges_WithRenamedItem_ReturnsTrue()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            RenamedPartitions = { new HierarchyRenameItem { OldName = "Old", NewName = "New", ExternalId = "DC=test" } }
        };

        // Act & Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void HasChanges_WithMovedContainer_ReturnsTrue()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            MovedContainers = { new HierarchyMoveItem { Name = "M1", ExternalId = "OU=test" } }
        };

        // Act & Assert
        Assert.That(result.HasChanges, Is.True);
    }

    #endregion

    #region HasSelectedItemsRemoved Tests

    [Test]
    public void HasSelectedItemsRemoved_WithNoRemovals_ReturnsFalse()
    {
        // Arrange
        var result = new HierarchyRefreshResult { Success = true };

        // Act & Assert
        Assert.That(result.HasSelectedItemsRemoved, Is.False);
    }

    [Test]
    public void HasSelectedItemsRemoved_WithRemovedUnselectedItems_ReturnsFalse()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            RemovedPartitions = { new HierarchyChangeItem { Name = "P1", ExternalId = "DC=test", WasSelected = false } },
            RemovedContainers = { new HierarchyChangeItem { Name = "C1", ExternalId = "OU=test", WasSelected = false } }
        };

        // Act & Assert
        Assert.That(result.HasSelectedItemsRemoved, Is.False);
    }

    [Test]
    public void HasSelectedItemsRemoved_WithRemovedSelectedPartition_ReturnsTrue()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            RemovedPartitions = { new HierarchyChangeItem { Name = "P1", ExternalId = "DC=test", WasSelected = true } }
        };

        // Act & Assert
        Assert.That(result.HasSelectedItemsRemoved, Is.True);
    }

    [Test]
    public void HasSelectedItemsRemoved_WithRemovedSelectedContainer_ReturnsTrue()
    {
        // Arrange
        var result = new HierarchyRefreshResult
        {
            Success = true,
            RemovedContainers = { new HierarchyChangeItem { Name = "C1", ExternalId = "OU=test", WasSelected = true } }
        };

        // Act & Assert
        Assert.That(result.HasSelectedItemsRemoved, Is.True);
    }

    #endregion

    #region Static Factory Methods Tests

    [Test]
    public void NoChanges_ReturnsSuccessfulResultWithCounts()
    {
        // Act
        var result = HierarchyRefreshResult.NoChanges(3, 15);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.TotalPartitions, Is.EqualTo(3));
            Assert.That(result.TotalContainers, Is.EqualTo(15));
            Assert.That(result.HasChanges, Is.False);
            Assert.That(result.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void Failed_ReturnsFailedResultWithMessage()
    {
        // Act
        var result = HierarchyRefreshResult.Failed("Connection failed");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo("Connection failed"));
        });
    }

    #endregion
}
