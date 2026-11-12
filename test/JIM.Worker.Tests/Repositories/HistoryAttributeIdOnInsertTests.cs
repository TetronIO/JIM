// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// A change record's attribute row names its attribute by a plain id with no relationship (#2931), so EF Core no longer
/// fixes the id up from the attribute when both are saved together. A row added to the context must therefore name a
/// saved attribute: the context takes the id from the working reference when only that is set, and refuses a row that
/// names no saved attribute rather than letting it be written with no id, which nothing in the database would catch.
/// </summary>
/// <remarks>Nothing here reaches a database: adding an entity to the context is enough to raise the check.</remarks>
[TestFixture]
public class HistoryAttributeIdOnInsertTests
{
    private static JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql("Host=unused.invalid;Database=unused")
        .Options);

    [Test]
    public void Add_MetaverseHistoryRowWithSavedWorkingReference_TakesTheAttributesId()
    {
        using var ctx = NewContext();
        var row = new MetaverseObjectChangeAttribute
        {
            Attribute = new MetaverseAttribute { Id = 42, Name = "title" }, AttributeName = "title"
        };

        ctx.Add(row);

        Assert.That(row.AttributeId, Is.EqualTo(42));
    }

    [Test]
    public void Add_ConnectedSystemHistoryRowWithSavedWorkingReference_TakesTheAttributesId()
    {
        using var ctx = NewContext();
        var row = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 42, Name = "title" }, AttributeName = "title"
        };

        ctx.Add(row);

        Assert.That(row.AttributeId, Is.EqualTo(42));
    }

    [Test]
    public void Add_MetaverseHistoryRowNamingAnUnsavedAttribute_Throws()
    {
        using var ctx = NewContext();
        var row = new MetaverseObjectChangeAttribute
        {
            Attribute = new MetaverseAttribute { Name = "title" }, AttributeName = "title"
        };

        Assert.That(() => ctx.Add(row), Throws.InvalidOperationException.With.Message.Contains("title"));
    }

    [Test]
    public void Add_ConnectedSystemHistoryRowNamingAnUnsavedAttribute_Throws()
    {
        using var ctx = NewContext();
        var row = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = new ConnectedSystemObjectTypeAttribute { Name = "title" }, AttributeName = "title"
        };

        Assert.That(() => ctx.Add(row), Throws.InvalidOperationException.With.Message.Contains("title"));
    }

    [Test]
    public void Add_HistoryRowWithZeroIdAndUnsavedAttribute_Throws()
    {
        // A builder that copies an unsaved attribute's id copies 0, which names no attribute either.
        using var ctx = NewContext();
        var unsaved = new MetaverseAttribute { Name = "title" };
        var row = new MetaverseObjectChangeAttribute { Attribute = unsaved, AttributeId = unsaved.Id, AttributeName = "title" };

        Assert.That(() => ctx.Add(row), Throws.InvalidOperationException.With.Message.Contains("title"));
    }

    [Test]
    public void Add_HistoryRowNamingNoAttribute_Throws()
    {
        using var ctx = NewContext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => ctx.Add(new MetaverseObjectChangeAttribute { AttributeName = "title" }),
                Throws.InvalidOperationException);
            Assert.That(() => ctx.Add(new ConnectedSystemObjectChangeAttribute { AttributeName = "title" }),
                Throws.InvalidOperationException);
        }
    }
}
