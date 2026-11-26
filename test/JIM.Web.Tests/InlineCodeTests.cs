// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Localisation;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A value placed into a message rendered as markup is encoded: values come from directories and administrators, and
/// markup bypasses Razor's own encoding.
/// </summary>
[TestFixture]
public class InlineCodeTests
{
    [Test]
    public void Format_ValueHoldingMarkup_IsEncoded()
    {
        var markup = InlineCode.Format("Remove {0} from {1}?", "<script>alert(1)</script>", "Audit & Log").Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(markup, Does.Not.Contain("<script>"));
            Assert.That(markup, Does.Contain("&lt;script&gt;"));
            Assert.That(markup, Does.Contain("Audit &amp; Log"));
        }
    }

    [Test]
    public void Format_EachValue_IsItsOwnInlineCode()
    {
        var markup = InlineCode.Format("Remove {0} from {1}?", "J Smith", "Account Audit Log").Value;

        Assert.That(markup, Does.Contain(">J Smith</code> from <code").And.EndWith(">Account Audit Log</code>?"));
    }
}
