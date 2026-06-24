// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Exceptions;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
namespace JIM.Models.Tests;

public class ExampleDataObjectTypeDependencyOrderTests
{
    private static ExampleDataTemplateAttribute TextAttribute(string name, string? expression = null) => new()
    {
        MetaverseAttribute = new MetaverseAttribute { Name = name, Type = AttributeDataType.Text },
        PopulatedValuesPercentage = 100,
        Expression = expression
    };

    [Test]
    public void GetTemplateAttributesInDependencyOrder_ExpressionReferences_AreGeneratedFirst()
    {
        // Email expression references First Name, Last Name and Company; declare Email FIRST to prove ordering is by
        // dependency, not declaration order.
        var email = TextAttribute("Email", "Lower(mv[\"First Name\"]) + \".\" + Lower(mv[\"Last Name\"]) + \"@\" + Lower(Replace(mv[\"Company\"], \" \", \"\")) + \".io\"");
        var firstName = TextAttribute("First Name");
        var lastName = TextAttribute("Last Name");
        var company = TextAttribute("Company");

        var objectType = new ExampleDataObjectType { MetaverseObjectType = new MetaverseObjectType { Name = "User" } };
        objectType.TemplateAttributes.AddRange(new[] { email, firstName, lastName, company });

        var ordered = objectType.GetTemplateAttributesInDependencyOrder();

        Assert.That(ordered, Has.Count.EqualTo(4));
        var emailIndex = ordered.IndexOf(email);
        Assert.That(emailIndex, Is.GreaterThan(ordered.IndexOf(firstName)));
        Assert.That(emailIndex, Is.GreaterThan(ordered.IndexOf(lastName)));
        Assert.That(emailIndex, Is.GreaterThan(ordered.IndexOf(company)));
    }

    [Test]
    public void GetTemplateAttributesInDependencyOrder_ChainedExpressions_AreOrderedTransitively()
    {
        // Login depends on First Name + Last Name; Email depends on Login. Email must come after Login, which must come
        // after First Name and Last Name.
        var firstName = TextAttribute("First Name");
        var lastName = TextAttribute("Last Name");
        var login = TextAttribute("Login", "Lower(mv[\"First Name\"]) + \".\" + Lower(mv[\"Last Name\"])");
        var email = TextAttribute("Email", "mv[\"Login\"] + \"@example.io\"");

        var objectType = new ExampleDataObjectType { MetaverseObjectType = new MetaverseObjectType { Name = "User" } };
        objectType.TemplateAttributes.AddRange(new[] { email, login, firstName, lastName });

        var ordered = objectType.GetTemplateAttributesInDependencyOrder();

        Assert.That(ordered.IndexOf(login), Is.GreaterThan(ordered.IndexOf(firstName)));
        Assert.That(ordered.IndexOf(login), Is.GreaterThan(ordered.IndexOf(lastName)));
        Assert.That(ordered.IndexOf(email), Is.GreaterThan(ordered.IndexOf(login)));
    }

    [Test]
    public void GetTemplateAttributesInDependencyOrder_CircularExpressionReferences_Throws()
    {
        // A references B, B references A.
        var a = TextAttribute("A", "mv[\"B\"] + \"x\"");
        var b = TextAttribute("B", "mv[\"A\"] + \"y\"");

        var objectType = new ExampleDataObjectType { MetaverseObjectType = new MetaverseObjectType { Name = "User" } };
        objectType.TemplateAttributes.AddRange(new[] { a, b });

        Assert.Catch<ExampleDataTemplateException>(() => objectType.GetTemplateAttributesInDependencyOrder());
    }

    [Test]
    public void GetTemplateAttributesInDependencyOrder_ConditionalDependency_IsGeneratedFirst()
    {
        // The dependent attribute (conditional generation) must come after the attribute it depends on.
        var company = TextAttribute("Company");
        var division = TextAttribute("Division");
        division.AttributeDependency = new ExampleDataTemplateAttributeDependency
        {
            MetaverseAttribute = company.MetaverseAttribute!,
            StringValue = "Acme",
            ComparisonType = ComparisonType.Equals
        };

        var objectType = new ExampleDataObjectType { MetaverseObjectType = new MetaverseObjectType { Name = "User" } };
        objectType.TemplateAttributes.AddRange(new[] { division, company });

        var ordered = objectType.GetTemplateAttributesInDependencyOrder();

        Assert.That(ordered.IndexOf(division), Is.GreaterThan(ordered.IndexOf(company)));
    }

    [Test]
    public void GetTemplateAttributesInDependencyOrder_NoDependencies_ReturnsAllAttributes()
    {
        var first = TextAttribute("First Name");
        var last = TextAttribute("Last Name");

        var objectType = new ExampleDataObjectType { MetaverseObjectType = new MetaverseObjectType { Name = "User" } };
        objectType.TemplateAttributes.AddRange(new[] { first, last });

        var ordered = objectType.GetTemplateAttributesInDependencyOrder();

        Assert.That(ordered, Has.Count.EqualTo(2));
        Assert.That(ordered, Does.Contain(first));
        Assert.That(ordered, Does.Contain(last));
    }
}
