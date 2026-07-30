// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the stack trace's disclosure behaviour: the error message is what an administrator reads, and the trace
/// stays out of the way until it is asked for.
/// </summary>
[TestFixture]
public class CollapsibleStackTraceTests : JimComponentTestContext
{
    private const string Trace = "at JIM.Connectors.LDAP.LdapConnector.OpenImportConnection(List`1 settingValues)";

    [Test]
    public void CollapsibleStackTrace_WithNoStackTrace_RendersNothing()
    {
        var cut = Render<CollapsibleStackTrace>(p => p.Add(c => c.StackTrace, null));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void CollapsibleStackTrace_WithWhitespaceStackTrace_RendersNothing()
    {
        var cut = Render<CollapsibleStackTrace>(p => p.Add(c => c.StackTrace, "   "));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void CollapsibleStackTrace_ByDefault_KeepsTheStackTraceOutOfTheMarkup()
    {
        var cut = Render<CollapsibleStackTrace>(p => p.Add(c => c.StackTrace, Trace));

        Assert.Multiple(() =>
        {
            // Not merely hidden: a stack trace can run to thousands of characters, and there is no reason to send
            // them to the browser before anybody has asked to read them.
            Assert.That(cut.Markup, Does.Not.Contain("OpenImportConnection"));
            Assert.That(cut.Markup, Does.Contain("Show stack trace"));
        });
    }

    [Test]
    public void CollapsibleStackTrace_WhenShown_RendersTheStackTrace()
    {
        var cut = Render<CollapsibleStackTrace>(p => p.Add(c => c.StackTrace, Trace));

        cut.Find("button").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("OpenImportConnection"));
            Assert.That(cut.Markup, Does.Contain("Hide stack trace"));
        });
    }

    [Test]
    public void CollapsibleStackTrace_WhenHiddenAgain_DropsTheStackTrace()
    {
        var cut = Render<CollapsibleStackTrace>(p => p.Add(c => c.StackTrace, Trace));

        cut.Find("button").Click();
        cut.Find("button").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("OpenImportConnection"));
            Assert.That(cut.Markup, Does.Contain("Show stack trace"));
        });
    }
}
