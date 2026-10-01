// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// Schema discovery against a stock Windows Server forest (#1853).
/// </summary>
/// <remarks>
/// A stock forest publishes well over 1,000 non-defunct <c>attributeSchema</c> entries, and Active Directory caps an
/// unpaged search at its MaxPageSize (1,000 by default) with sizeLimitExceeded. Samba AD applies no such cap, which
/// is why the integration lab never saw the connector's unpaged schema fetch fail.
/// </remarks>
[TestFixture]
[Category(ActiveDirectoryLab.Category)]
public class ActiveDirectorySchemaProbeTests
{
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => _logger.Dispose();

    [Test]
    public async Task GetSchemaAsync_StockForest_DiscoversTheUserClassWithItsAttributesAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        using var connector = ActiveDirectoryLab.NewConnector(lab);

        ConnectorSchema? schema = null;
        Assert.That(
            async () => schema = await connector.GetSchemaAsync(ActiveDirectoryLab.ConnectorSettings(lab), _logger),
            Throws.Nothing,
            "Schema discovery must read every attributeSchema entry under the directory's MaxPageSize, which needs the paged-results control.");

        var user = schema!.ObjectTypes.SingleOrDefault(objectType => objectType.Name.Equals("user", StringComparison.OrdinalIgnoreCase));
        Assert.That(user, Is.Not.Null, "The user class was not discovered.");

        var attributeNames = user!.Attributes.Select(attribute => attribute.Name).ToList();
        TestContext.Out.WriteLine($"user: {attributeNames.Count} attributes across {schema.ObjectTypes.Count} object types");
        Assert.Multiple(() =>
        {
            // A real user class carries several hundred attributes once its parents and auxiliary classes are
            // walked; a count this low means the walk stopped short.
            Assert.That(attributeNames, Has.Count.GreaterThan(300));
            Assert.That(attributeNames, Does.Contain("sAMAccountName").IgnoreCase);
            Assert.That(attributeNames, Does.Contain("userAccountControl").IgnoreCase);
            Assert.That(attributeNames, Does.Contain("unicodePwd").IgnoreCase);
        });
    }
}
