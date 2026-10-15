// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Expressions;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for SynchronisationController.GetExpressionFunctions (#2253): the expression function catalogue over REST,
/// so a script can read what the portal's Insert function list shows.
/// </summary>
[TestFixture]
public class SynchronisationControllerExpressionFunctionsTests
{
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _application = new JimApplication(new Mock<IRepository>().Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    private List<ExpressionFunctionDto> Get(string? name = null, ExpressionFunctionCategory? category = null)
    {
        var ok = _controller.GetExpressionFunctions(name, category) as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        return ((IEnumerable<ExpressionFunctionDto>)ok!.Value!).ToList();
    }

    [Test]
    public void GetExpressionFunctions_NoFilter_ReturnsEveryFunctionInCatalogueOrder()
    {
        var functions = Get();

        Assert.That(functions.Select(f => f.Name), Is.EqualTo(ExpressionFunctionCatalogue.All.Select(f => f.Name)));
    }

    [Test]
    public void GetExpressionFunctions_Function_CarriesItsDescriptionFromTheCatalogue()
    {
        var startsWith = Get().Single(f => f.Name == "StartsWith");
        var source = ExpressionFunctionCatalogue.Find("StartsWith")!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(startsWith.Category, Is.EqualTo(ExpressionFunctionCategory.Text));
            Assert.That(startsWith.CategoryName, Is.EqualTo(ExpressionFunctionCatalogue.CategoryName(ExpressionFunctionCategory.Text)));
            Assert.That(startsWith.Signature, Is.EqualTo("StartsWith(value, prefix[, ignoreCase])"));
            Assert.That(startsWith.Returns, Is.EqualTo(source.Returns));
            Assert.That(startsWith.Description, Is.EqualTo(source.Description));
            Assert.That(startsWith.Example, Is.EqualTo(source.Example));
            Assert.That(startsWith.IsRepeatable, Is.True);
            Assert.That(startsWith.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "value", "prefix", "ignoreCase" }));
            Assert.That(startsWith.Parameters.Select(p => p.IsOptional), Is.EqualTo(new[] { false, false, true }));
            Assert.That(startsWith.Parameters[1].Description, Is.EqualTo(source.Parameters[1].Description));
        }
    }

    [Test]
    public void GetExpressionFunctions_NonRepeatableFunction_SaysSo()
    {
        Assert.That(Get().Single(f => f.Name == "Now").IsRepeatable, Is.False);
    }

    [Test]
    public void GetExpressionFunctions_Name_MatchesAnyPartIgnoringCase()
    {
        var functions = Get(name: "with");

        Assert.That(functions.Select(f => f.Name), Is.EquivalentTo(
            ExpressionFunctionCatalogue.All.Where(f => f.Name.Contains("with", System.StringComparison.OrdinalIgnoreCase)).Select(f => f.Name)));
        Assert.That(functions.Select(f => f.Name), Does.Contain("StartsWith").And.Contain("EndsWith"));
    }

    [Test]
    public void GetExpressionFunctions_Category_ReturnsOnlyThatCategory()
    {
        var functions = Get(category: ExpressionFunctionCategory.Date);

        Assert.That(functions, Is.Not.Empty);
        Assert.That(functions.Select(f => f.Category), Is.All.EqualTo(ExpressionFunctionCategory.Date));
        Assert.That(functions, Has.Count.EqualTo(ExpressionFunctionCatalogue.All.Count(f => f.Category == ExpressionFunctionCategory.Date)));
    }

    [Test]
    public void GetExpressionFunctions_NameAndCategory_AppliesBoth()
    {
        var functions = Get(name: "date", category: ExpressionFunctionCategory.Date);

        Assert.That(functions, Is.Not.Empty);
        Assert.That(functions, Is.All.Matches<ExpressionFunctionDto>(f =>
            f.Category == ExpressionFunctionCategory.Date && f.Name.Contains("date", System.StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public void GetExpressionFunctions_NameMatchingNothing_ReturnsEmptyList()
    {
        Assert.That(Get(name: "NoSuchFunction"), Is.Empty);
    }

    [Test]
    public void GetExpressionFunctions_BlankName_IsNoFilter()
    {
        Assert.That(Get(name: "  "), Has.Count.EqualTo(ExpressionFunctionCatalogue.All.Count));
    }
}
