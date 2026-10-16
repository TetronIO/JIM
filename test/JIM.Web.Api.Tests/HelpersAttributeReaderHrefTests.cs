// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Where a refusal links each piece of configuration that reads an attribute (#2265).
/// </summary>
[TestFixture]
public class HelpersAttributeReaderHrefTests
{
    [Test]
    public void AttributeReaderHref_ReaderOnASynchronisationRule_LinksTheRule()
    {
        Assert.That(Helpers.AttributeReaderHref(new AttributeReader { SyncRuleId = 12 }), Is.EqualTo("/admin/sync-rules/12"));
    }

    [Test]
    public void AttributeReaderHref_ReaderOnAnExampleDataTemplate_LinksTheTemplate()
    {
        Assert.That(Helpers.AttributeReaderHref(new AttributeReader { ExampleDataTemplateId = 3 }), Is.EqualTo("/admin/example-data/templates/3"));
    }

    [Test]
    public void AttributeReaderHref_ObjectMatchingRuleOnAnObjectType_HasNoLink()
    {
        Assert.That(Helpers.AttributeReaderHref(new AttributeReader { Kind = AttributeReaderKind.ObjectMatchingRuleExpression }), Is.Null);
    }
}
