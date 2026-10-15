// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// An Attribute Flow's expressions are checked when it is saved (#2055 Phase 3), on every surface, so an expression a
/// synchronisation would fail for every object is caught with a reason instead. The validator reports what is wrong;
/// the save decides what blocks (a new or changed mapping) and what is only a warning (an unchanged one).
/// </summary>
[TestFixture]
public class AttributeFlowExpressionValidatorTests
{
    private AttributeFlowExpressionValidator _validator = null!;
    private ConnectedSystemObjectType _csType = null!;
    private MetaverseObjectType _mvType = null!;
    private MetaverseAttribute _displayName = null!;
    private MetaverseAttribute _hireDate = null!;
    private ConnectedSystemObjectTypeAttribute _cn = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new AttributeFlowExpressionValidator(new DynamicExpressoEvaluator());
        _cn = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "cn", Type = AttributeDataType.Text };
        _csType = new ConnectedSystemObjectType
        {
            Id = 7,
            Name = "user",
            Attributes = [_cn, new ConnectedSystemObjectTypeAttribute { Id = 102, Name = "sn", Type = AttributeDataType.Text }]
        };
        _displayName = new MetaverseAttribute { Id = 201, Name = "Display Name", Type = AttributeDataType.Text };
        _hireDate = new MetaverseAttribute { Id = 202, Name = "Hire Date", Type = AttributeDataType.DateTime };
        _mvType = new MetaverseObjectType { Id = 1, Name = "User", Attributes = [_displayName, _hireDate] };
    }

    private static SyncRuleMapping Mapping(string expression, MetaverseAttribute? mvTarget = null, ConnectedSystemObjectTypeAttribute? csTarget = null)
    {
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvTarget, TargetConnectedSystemAttribute = csTarget };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        return mapping;
    }

    private IReadOnlyList<AttributeFlowExpressionProblem> Import(string expression, MetaverseAttribute? target = null) =>
        _validator.Validate(Mapping(expression, mvTarget: target ?? _displayName), SyncRuleDirection.Import, _csType, _mvType);

    private IReadOnlyList<AttributeFlowExpressionProblem> Export(string expression) =>
        _validator.Validate(Mapping(expression, csTarget: _cn), SyncRuleDirection.Export, _csType, _mvType);

    [Test]
    public void Validate_AWorkableImportExpression_HasNoProblem()
    {
        Assert.That(Import("Upper(cs[\"sn\"])"), Is.Empty);
    }

    [Test]
    public void Validate_AWorkableExportExpression_HasNoProblem()
    {
        Assert.That(Export("Lower(mv[\"Display Name\"])"), Is.Empty);
    }

    [Test]
    public void Validate_ExpressionThatDoesNotParse_IsDoesNotParseAndNothingElse()
    {
        var problems = Import("Upper(cs[\"sn\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problems.Select(p => p.Kind), Is.EqualTo(new[] { AttributeFlowExpressionProblemKind.DoesNotParse }));
            Assert.That(problems[0].Message, Does.Contain("Display Name"));
        }
    }

    [Test]
    public void Validate_UnknownFunction_DoesNotParse()
    {
        Assert.That(Import("Lowercase(cs[\"sn\"])").Single().Kind, Is.EqualTo(AttributeFlowExpressionProblemKind.DoesNotParse));
    }

    [Test]
    public void Validate_ExportReadingTheConnectedSystem_IsWrongSide()
    {
        var problem = Export("Upper(cs[\"sn\"])").Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Kind, Is.EqualTo(AttributeFlowExpressionProblemKind.WrongSide));
            Assert.That(problem.Message, Does.Contain("cs[\"sn\"]"));
        }
    }

    [Test]
    public void Validate_ImportReadingTheMetaverse_IsLeftToTheDerivedFlowCheck()
    {
        // A Metaverse-Derived Attribute Flow (#1750) reads mv[...] on import; DerivedFlowValidator owns its checks.
        Assert.That(Import("Upper(mv[\"Display Name\"])", target: _hireDate), Has.None.Matches<AttributeFlowExpressionProblem>(
            p => p.Kind == AttributeFlowExpressionProblemKind.WrongSide || p.Kind == AttributeFlowExpressionProblemKind.UnknownAttribute));
    }

    [Test]
    public void Validate_ImportReadingAnAttributeTheObjectTypeDoesNotHave_IsUnknownAttribute()
    {
        var problem = Import("Upper(cs[\"surname\"])").Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Kind, Is.EqualTo(AttributeFlowExpressionProblemKind.UnknownAttribute));
            Assert.That(problem.Message, Does.Contain("surname").And.Contain("user"));
        }
    }

    [Test]
    public void Validate_AttributeNameInADifferentCase_IsKnown()
    {
        Assert.That(Import("Upper(cs[\"SN\"])"), Is.Empty);
    }

    [Test]
    public void Validate_ExportReadingAnAttributeTheMetaverseObjectTypeDoesNotHave_IsUnknownAttribute()
    {
        var problem = Export("Lower(mv[\"Nickname\"])").Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Kind, Is.EqualTo(AttributeFlowExpressionProblemKind.UnknownAttribute));
            Assert.That(problem.Message, Does.Contain("Nickname").And.Contain("User"));
        }
    }

    [Test]
    public void Validate_ResultTheTargetCanNeverHold_IsResultTypeMismatch()
    {
        var problem = Import("HasValue(cs[\"sn\"])", target: _hireDate).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Kind, Is.EqualTo(AttributeFlowExpressionProblemKind.ResultTypeMismatch));
            Assert.That(problem.Message, Does.Contain("Hire Date"));
        }
    }

    [Test]
    public void Validate_GeneratedValueBase_IsNotCheckedAgainstTheTargetType()
    {
        // The base has a token added and is held as text first; the generation validator owns the target's type.
        var mapping = Mapping("HasValue(cs[\"sn\"])", mvTarget: _hireDate);
        mapping.Generation = new SyncRuleMappingGeneration();

        Assert.That(_validator.Validate(mapping, SyncRuleDirection.Import, _csType, _mvType), Is.Empty);
    }

    [Test]
    public void Validate_NonRepeatableFunction_IsAWarningOnly()
    {
        var problem = Import("Now()", target: _hireDate).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Kind, Is.EqualTo(AttributeFlowExpressionProblemKind.NonRepeatable));
            Assert.That(problem.IsWarningOnly, Is.True);
            Assert.That(problem.Message, Does.Contain("Now()"));
        }
    }

    [Test]
    public void Validate_DerivedFlowCallingANonRepeatableFunction_IsNotWarnedTwice()
    {
        // DerivedFlowValidator already warns about a derived Attribute Flow calling one (#1750).
        Assert.That(Import("FormatDate(Now(), \"yyyy\") + mv[\"Display Name\"]"), Is.Empty);
    }

    [Test]
    public void Validate_UnknownObjectTypes_ChecksWhatItCan()
    {
        // A type that could not be read is no reason to skip the checks that do not need it.
        var problems = _validator.Validate(Mapping("Upper(cs[\"anything\"]", mvTarget: _displayName), SyncRuleDirection.Import, null, null);

        Assert.That(problems.Single().Kind, Is.EqualTo(AttributeFlowExpressionProblemKind.DoesNotParse));
    }

    [Test]
    public void Validate_EveryProblemButNonRepeatable_IsBlocking()
    {
        var problems = Import("Upper(cs[\"surname\"])").Concat(Export("Upper(cs[\"sn\"])")).Concat(Import("Upper(", null));

        Assert.That(problems.All(p => !p.IsWarningOnly), Is.True);
    }

    [Test]
    public void Validate_MappingWithNoExpression_HasNoProblem()
    {
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = _displayName };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = _cn, ConnectedSystemAttributeId = _cn.Id });

        Assert.That(_validator.Validate(mapping, SyncRuleDirection.Import, _csType, _mvType), Is.Empty);
    }
}
