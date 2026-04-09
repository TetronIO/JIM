using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Regression guard: verifies that JimDbContext defaults to NoTracking.
/// This prevents accidental reversion of the QueryTrackingBehavior configuration
/// introduced in #484 to reduce EF Core overhead on read-heavy paths.
/// </summary>
[TestFixture]
public class JimDbContextTrackingBehaviourTests
{
    [Test]
    public void DbContext_DefaultQueryTrackingBehaviour_IsNoTrackingAsync()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        using var context = new JimDbContext(options);
        Assert.That(context.ChangeTracker.QueryTrackingBehavior,
            Is.EqualTo(QueryTrackingBehavior.NoTracking),
            "JimDbContext must default to NoTracking. Write paths opt in with .AsTracking().");
    }
}
