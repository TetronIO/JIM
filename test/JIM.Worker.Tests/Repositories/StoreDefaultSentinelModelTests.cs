// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Guards every store-level default in the JIM model against the EF Core sentinel trap. EF Core omits a column from
/// an INSERT when the property holds its sentinel (the CLR default unless configured otherwise), so the database
/// default is written instead of the value the caller set. Where the store default differs from the CLR default, a
/// deliberately chosen value (an enum's zero member, <c>false</c>, <c>0</c>) is silently replaced on create.
/// Configuring the sentinel as the store default itself makes the omission harmless: the column is only left out
/// when the value would be the same either way.
/// </summary>
[TestFixture]
public class StoreDefaultSentinelModelTests
{
    [Test]
    public void Model_EveryPropertyWithAStoreDefault_HasTheDefaultAsItsSentinel()
    {
        using var ctx = new JimDbContext(new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options);

        var offenders = ctx.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Entity: e, Property: p)))
            .Where(x => x.Property.TryGetDefaultValue(out var defaultValue)
                        && !Equals(defaultValue, x.Property.Sentinel))
            .Select(x => $"{x.Entity.ClrType.Name}.{x.Property.Name} (default {x.Property.GetDefaultValue()}, sentinel {x.Property.Sentinel ?? "null"})")
            .ToList();

        Assert.That(offenders, Is.Empty,
            "Each property below has a store default that differs from its sentinel, so an insert holding the sentinel " +
            "is stored with the default instead. Add .HasSentinel(<the default>) alongside its HasDefaultValue:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
