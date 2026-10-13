// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Resolving an Object Type name a refresh result reports back to the type JIM held before the refresh (#2051). The
/// result names a type the way the Connector now does, and a case-only rename means that differs from the name the
/// pre-refresh snapshot carries.
/// </summary>
[TestFixture]
public class SchemaRefreshPreRefreshTypeLookupTests
{
    private static SchemaRefreshResult Result(params (int Id, string Name)[] types) => new()
    {
        Success = true,
        PreRefreshSchema = types.Select(t => new SchemaRefreshPreRefreshType { Id = t.Id, Name = t.Name }).ToList()
    };

    [Test]
    public void FindPreRefreshType_NameUnchanged_ReturnsTheType()
    {
        var result = Result((1, "user"), (2, "group"));

        Assert.That(result.FindPreRefreshType("group")?.Id, Is.EqualTo(2));
    }

    [Test]
    public void FindPreRefreshType_TypeRenamedOnlyInCase_ReturnsTheTypeUnderItsOldName()
    {
        var result = Result((1, "user"));
        result.RenamedObjectTypes.Add(new SchemaNameChange { OldName = "user", NewName = "User" });

        Assert.That(result.FindPreRefreshType("User")?.Id, Is.EqualTo(1));
    }

    [Test]
    public void FindPreRefreshType_TwoTypesDifferingOnlyInCase_ReturnsTheExactOne()
    {
        var result = Result((1, "user"), (2, "User"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FindPreRefreshType("user")?.Id, Is.EqualTo(1));
            Assert.That(result.FindPreRefreshType("User")?.Id, Is.EqualTo(2));
        }
    }

    [Test]
    public void FindPreRefreshType_UnknownName_ReturnsNull()
    {
        Assert.That(Result((1, "user")).FindPreRefreshType("computer"), Is.Null);
    }
}
