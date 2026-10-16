// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for ExampleDataController's test-expression endpoint (#2055 Phase 2d): an Example Data Template attribute's
/// expression tested against sample values by the same evaluation generating objects runs.
/// </summary>
[TestFixture]
public class ExampleDataControllerTestExpressionTests
{
    private const int TemplateId = 4;
    private const int AccountNameId = 13;
    private const int PatternAttributeId = 14;

    private JimApplication _application = null!;
    private ExampleDataController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        var objectType = new ExampleDataObjectType { Id = 1, MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" } };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 11, MetaverseAttribute = new MetaverseAttribute { Id = 101, Name = "First Name", Type = AttributeDataType.Text }
        });
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 12, MetaverseAttribute = new MetaverseAttribute { Id = 102, Name = "Employee Number", Type = AttributeDataType.Number }
        });
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = AccountNameId, Expression = "Lower(mv[\"First Name\"]) + \".\" + mv[\"Employee Number\"].ToString()",
            MetaverseAttribute = new MetaverseAttribute { Id = 103, Name = "Account Name", Type = AttributeDataType.Text }
        });
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = PatternAttributeId, Pattern = "{First Name}",
            MetaverseAttribute = new MetaverseAttribute { Id = 104, Name = "Nickname", Type = AttributeDataType.Text }
        });
        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Corporation" };
        template.ObjectTypes.Add(objectType);

        var exampleData = new Mock<IExampleDataRepository>();
        exampleData.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ExampleData).Returns(exampleData.Object);

        _application = new JimApplication(repository.Object);
        _controller = new ExampleDataController(new Mock<ILogger<ExampleDataController>>().Object, _application);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    private static TestExampleDataExpressionResponse Ok(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>(), (result as ObjectResult)?.Value?.ToString());
        return (TestExampleDataExpressionResponse)((OkObjectResult)result).Value!;
    }

    [Test]
    public async Task TestExpressionAsync_AttributesOwnExpressionWithSamples_ReturnsTheValueGenerationWouldAsync()
    {
        var response = Ok(await _controller.TestExpressionAsync(TemplateId, new TestExampleDataExpressionRequest
        {
            TemplateAttributeId = AccountNameId,
            Attributes = new Dictionary<string, object?> { ["First Name"] = "Ada", ["Employee Number"] = "7" }
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True, response.ErrorMessage);
            Assert.That(response.Outcome, Is.EqualTo(ExampleDataExpressionOutcome.Value));
            Assert.That(response.Value, Is.EqualTo("ada.7"));
            Assert.That(response.Attribute, Is.EqualTo("Account Name"));
            Assert.That(response.Inputs.Select(i => (i.Attribute, i.Type)),
                Is.EquivalentTo(new[] { ("First Name", AttributeDataType.Text), ("Employee Number", AttributeDataType.Number) }));
            Assert.That(response.OutcomeDescription, Does.Contain("Account Name"));
        }
    }

    [Test]
    public async Task TestExpressionAsync_ExpressionGiven_TestsItInPlaceOfTheSavedOneAsync()
    {
        var response = Ok(await _controller.TestExpressionAsync(TemplateId, new TestExampleDataExpressionRequest
        {
            TemplateAttributeId = AccountNameId, Expression = "Upper(mv[\"First Name\"])",
            Attributes = new Dictionary<string, object?> { ["First Name"] = "Ada" }
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Value, Is.EqualTo("ADA"));
            Assert.That(response.Expression, Is.EqualTo("Upper(mv[\"First Name\"])"));
        }
    }

    [Test]
    public async Task TestExpressionAsync_SeveralValues_IsNotValidWithTheReasonAsync()
    {
        var response = Ok(await _controller.TestExpressionAsync(TemplateId, new TestExampleDataExpressionRequest
        {
            TemplateAttributeId = AccountNameId, Expression = "Split(\"a;b\", \";\")"
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.ErrorReason, Is.EqualTo(ExampleDataExpressionErrorReason.SeveralValues));
            Assert.That(response.ErrorMessage, Is.Not.Empty);
        }
    }

    [Test]
    public async Task TestExpressionAsync_SampleNotOfItsType_ReturnsTheInputErrorAsync()
    {
        var response = Ok(await _controller.TestExpressionAsync(TemplateId, new TestExampleDataExpressionRequest
        {
            TemplateAttributeId = AccountNameId, Attributes = new Dictionary<string, object?> { ["Employee Number"] = "seven" }
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.InputErrors, Is.Not.Empty);
            Assert.That(response.Outcome, Is.Null);
        }
    }

    [Test]
    public async Task TestExpressionAsync_AttributeWithNoExpressionAndNoneGiven_IsBadRequestAsync()
    {
        var result = await _controller.TestExpressionAsync(TemplateId, new TestExampleDataExpressionRequest { TemplateAttributeId = PatternAttributeId });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task TestExpressionAsync_UnknownTemplate_IsNotFoundAsync()
    {
        var result = await _controller.TestExpressionAsync(99, new TestExampleDataExpressionRequest { TemplateAttributeId = AccountNameId });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task TestExpressionAsync_UnknownTemplateAttribute_IsNotFoundAsync()
    {
        var result = await _controller.TestExpressionAsync(TemplateId, new TestExampleDataExpressionRequest { TemplateAttributeId = 999 });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }
}
