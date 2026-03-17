using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class MetaverseServerAttributePaginationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_DelegatesToRepositoryAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        var attributeName = "StaticMembers";
        var page = 2;
        var pageSize = 25;
        var searchText = "Alice";

        var expectedResult = new PagedResultSet<MetaverseObjectAttributeValue>
        {
            Results = new List<MetaverseObjectAttributeValue>
            {
                new MetaverseObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    StringValue = "Alice Smith"
                }
            },
            TotalResults = 50,
            CurrentPage = page,
            PageSize = pageSize
        };

        _mockMetaverseRepo
            .Setup(r => r.GetAttributeValuesPagedAsync(mvoId, attributeName, page, pageSize, searchText))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _jim.Metaverse.GetAttributeValuesPagedAsync(
            mvoId, attributeName, page, pageSize, searchText);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalResults, Is.EqualTo(50));
        Assert.That(result.Results, Has.Count.EqualTo(1));
        Assert.That(result.Results[0].StringValue, Is.EqualTo("Alice Smith"));
        Assert.That(result.CurrentPage, Is.EqualTo(page));
        Assert.That(result.PageSize, Is.EqualTo(pageSize));

        _mockMetaverseRepo.Verify(
            r => r.GetAttributeValuesPagedAsync(mvoId, attributeName, page, pageSize, searchText),
            Times.Once);
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_WithNullSearch_PassesNullToRepositoryAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        var attributeName = "StaticMembers";

        var expectedResult = new PagedResultSet<MetaverseObjectAttributeValue>
        {
            Results = new List<MetaverseObjectAttributeValue>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 10
        };

        _mockMetaverseRepo
            .Setup(r => r.GetAttributeValuesPagedAsync(mvoId, attributeName, 1, 10, null))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _jim.Metaverse.GetAttributeValuesPagedAsync(
            mvoId, attributeName, 1, 10, null);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalResults, Is.EqualTo(0));
        Assert.That(result.Results, Is.Empty);
    }
}
