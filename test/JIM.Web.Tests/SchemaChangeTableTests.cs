// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Staging.DTOs;
using JIM.Web.Pages.Admin.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Which slice of a schema refresh the change table lists a case-only rename in (#2051). A rename keeps the Object Type
/// or attribute and everything referring to it, so it is a safe change, listed with the additions and never with the
/// destructive changes that call for a decision.
/// </summary>
[TestFixture]
public class SchemaChangeTableTests : JimComponentTestContext
{
    private static SchemaRefreshResult RenameResult()
    {
        var result = new SchemaRefreshResult { Success = true };
        result.RenamedObjectTypes.Add(new SchemaNameChange { OldName = "user", NewName = "User" });
        result.RenamedAttributes["User"] = [new SchemaNameChange { OldName = "mail", NewName = "Mail" }];
        return result;
    }

    [Test]
    public void SchemaChangeTable_AdditionsSection_ListsObjectTypeAndAttributeRenames()
    {
        var cut = Render<SchemaChangeTable>(p => p
            .Add(c => c.Result, RenameResult())
            .Add(c => c.Section, SchemaChangeTable.SchemaChangeSection.Additions));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("user → User"));
            Assert.That(cut.Markup, Does.Contain("mail → Mail"));
        }
    }

    [Test]
    public void SchemaChangeTable_DestructiveSection_DoesNotListRenames()
    {
        var cut = Render<SchemaChangeTable>(p => p
            .Add(c => c.Result, RenameResult())
            .Add(c => c.Section, SchemaChangeTable.SchemaChangeSection.Destructive));

        Assert.That(cut.Markup, Does.Not.Contain("mail → Mail"));
    }
}
