using JIM.Connectors.File;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class FileConnectorTests
{
    private FileConnector _connector = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
    }

    #region IConnectorCapabilities tests

    [Test]
    public void SupportsFilePaths_ReturnsTrue()
    {
        Assert.That(_connector.SupportsFilePaths, Is.True);
    }

    [Test]
    public void SupportsPaging_ReturnsFalse()
    {
        Assert.That(_connector.SupportsPaging, Is.False);
    }

    #endregion
}
