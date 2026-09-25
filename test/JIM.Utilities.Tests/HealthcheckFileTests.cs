// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Utilities.Tests;

/// <summary>
/// The container health-check heartbeat: written from every loop that proves a service alive, so a write that fails
/// must never stop the loop.
/// </summary>
[TestFixture]
public class HealthcheckFileTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"jim-healthcheck-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task TouchAsync_WritablePath_WritesTheCurrentTimeAsync()
    {
        var path = Path.Combine(_directory, "healthcheck");
        var before = DateTime.UtcNow;

        var written = await HealthcheckFile.TouchAsync(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.True);
            var stamp = DateTime.Parse(await File.ReadAllTextAsync(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.That(stamp, Is.GreaterThanOrEqualTo(before));
        }
    }

    [Test]
    public async Task TouchAsync_UnwritablePath_ReportsFailureWithoutThrowingAsync()
    {
        // A missing directory is the simplest file-system failure to arrange; a full disk or a read-only mount
        // surfaces the same way.
        var path = Path.Combine(_directory, "missing", "healthcheck");

        var written = await HealthcheckFile.TouchAsync(path);

        Assert.That(written, Is.False);
    }
}
