// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for SynchronisationController.TestExpression (#2179, F1-15): the REST tester returns its result as the
/// lines the portal's tester shows, one value per line in JIM's one text form, so the same expression reads the same
/// on every surface.
/// </summary>
[TestFixture]
public class SynchronisationControllerTestExpressionTests
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
            new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new List<Claim> { new("sub", Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Administrator") }, "TestAuth"))
                }
            }
        };
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    private TestExpressionResponse Test(string expression, Dictionary<string, object?>? mvAttributes = null)
    {
        var result = _controller.TestExpression(new TestExpressionRequest { Expression = expression, MvAttributes = mvAttributes });
        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        return (TestExpressionResponse)ok!.Value!;
    }

    [Test]
    public void TestExpression_ListResult_ReturnsOneLinePerValue()
    {
        var response = Test("Split(\"a;b\", \";\")");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True);
            Assert.That(response.ResultLines, Is.EqualTo(new[] { "a", "b" }));
        }
    }

    [Test]
    public void TestExpression_DateResult_ReturnsJimsDateTextForm()
    {
        var response = Test("mv[\"When\"]", new Dictionary<string, object?> { ["When"] = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc) });

        Assert.That(response.ResultLines, Is.EqualTo(new[] { "2026-03-15 10:30:00Z" }));
    }

    [Test]
    public void TestExpression_BinaryResult_DescribesTheValueRatherThanEncodingIt()
    {
        var response = Test("mv[\"Photo\"]", new Dictionary<string, object?> { ["Photo"] = new byte[] { 1, 2, 3 } });

        Assert.That(response.ResultLines, Is.EqualTo(new[] { "(Binary value, 3 bytes)" }));
    }

    [Test]
    public void TestExpression_NullResult_ReturnsNoLines()
    {
        var response = Test("mv[\"Missing\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True);
            Assert.That(response.ResultLines, Is.Empty);
        }
    }

    [Test]
    public void TestExpression_ExpressionFails_ReturnsNoLines()
    {
        var response = Test("Split(\"a,b\", \",\")[5]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.ResultLines, Is.Null);
        }
    }
}
