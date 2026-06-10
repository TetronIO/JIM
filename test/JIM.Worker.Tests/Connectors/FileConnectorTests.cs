// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    #region IConnectorSettings tests

    [Test]
    public void GetSettings_ObjectTypeSettings_ShareARequiredGroup()
    {
        // the File Connector needs either an Object Type Column or an Object Type to determine object types,
        // so both settings must declare the same RequiredGroup for JIM to enforce the either/or requirement (#792)
        var settings = _connector.GetSettings();

        var objectTypeColumn = settings.Single(s => s.Name == "Object Type Column");
        var objectType = settings.Single(s => s.Name == "Object Type");

        Assert.That(objectTypeColumn.RequiredGroup, Is.Not.Null.And.Not.Empty);
        Assert.That(objectType.RequiredGroup, Is.EqualTo(objectTypeColumn.RequiredGroup));
    }

    #endregion
}
