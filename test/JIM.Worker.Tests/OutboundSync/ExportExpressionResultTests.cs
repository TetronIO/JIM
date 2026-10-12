// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// How an export Attribute Flow Expression's result becomes a Pending Export value (#2179): converted to the target
/// attribute's type, several values never written as a type name, and a result that cannot be converted reported
/// rather than sent. <see cref="SyncEngine.ComputeAttributeValueChanges"/> is called directly with the real evaluator.
/// </summary>
[TestFixture]
public class ExportExpressionResultTests
{
    private SyncEngine _engine = null!;
    private MetaverseObjectType _mvType = null!;
    private MetaverseAttribute _mvCodes = null!;
    private MetaverseAttribute _mvWhen = null!;
    private SyncRule _exportRule = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();
        _mvType = new MetaverseObjectType { Id = 1, Name = "Person" };
        _mvCodes = new MetaverseAttribute { Id = 1, Name = "Codes", Type = AttributeDataType.Text };
        _mvWhen = new MetaverseAttribute { Id = 2, Name = "When", Type = AttributeDataType.DateTime };
        _exportRule = new SyncRule { Id = 1, Name = "Export", Direction = SyncRuleDirection.Export, Enabled = true };
    }

    private ConnectedSystemObjectTypeAttribute Target(AttributeDataType type, AttributePlurality plurality = AttributePlurality.SingleValued)
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 50, Name = "target", Type = type, AttributePlurality = plurality };
        var mapping = new SyncRuleMapping
        {
            Id = 1,
            SyncRule = _exportRule,
            SyncRuleId = _exportRule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id
        };
        _exportRule.AttributeFlowRules.Add(mapping);
        return target;
    }

    private void Expression(string expression) =>
        _exportRule.AttributeFlowRules.Single().Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });

    private MetaverseObject Person(string? codes = null, DateTime? when = null)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvType };
        if (codes != null)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = _mvCodes, AttributeId = _mvCodes.Id, StringValue = codes });
        if (when != null)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = _mvWhen, AttributeId = _mvWhen.Id, DateTimeValue = when });
        return mvo;
    }

    private List<PendingExportAttributeValueChange> Create(MetaverseObject mvo, List<AttributeFlowError> flowErrors) =>
        _engine.ComputeAttributeValueChanges(mvo, _exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, out _, flowErrors: flowErrors);

    private List<PendingExportAttributeValueChange> Update(MetaverseObject mvo, List<AttributeFlowError> flowErrors,
        params ConnectedSystemObjectAttributeValue[] held)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var cache = held.ToLookup(v => (cso.Id, v.AttributeId));
        return _engine.ComputeAttributeValueChanges(mvo, _exportRule, [], PendingExportChangeType.Update,
            existingCso: cso, csoAttributeCache: cache, out _, flowErrors: flowErrors);
    }

    [Test]
    public void ComputeAttributeValueChanges_SplitIntoSingleValuedTarget_StagesNothingAndRecordsAnError()
    {
        Target(AttributeDataType.Text);
        Expression("Split(mv[\"Codes\"], \";\")");
        var flowErrors = new List<AttributeFlowError>();

        var changes = Create(Person("a;b"), flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty, "a single-valued attribute cannot hold two values, and JIM will not pick one");
            Assert.That(flowErrors, Has.Count.EqualTo(1));
            Assert.That(flowErrors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionResultMultiValued));
            Assert.That(flowErrors[0].ValueCount, Is.EqualTo(2));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_SplitWithOneValueIntoSingleValuedTarget_StagesThatValue()
    {
        Target(AttributeDataType.Text);
        Expression("Split(mv[\"Codes\"], \";\")");
        var flowErrors = new List<AttributeFlowError>();

        var changes = Create(Person("a"), flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => c.StringValue), Is.EqualTo(new[] { "a" }));
            Assert.That(flowErrors, Is.Empty);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_SplitIntoMultiValuedTarget_StagesEachValueNeverTheTypeName()
    {
        Target(AttributeDataType.Text, AttributePlurality.MultiValued);
        Expression("Split(mv[\"Codes\"], \";\")");
        var flowErrors = new List<AttributeFlowError>();

        var changes = Create(Person("a;b"), flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => c.StringValue), Has.None.EqualTo("System.String[]"));
            Assert.That(changes.Select(c => c.StringValue), Is.EquivalentTo(new[] { "a", "b" }));
            Assert.That(flowErrors, Is.Empty);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_SplitIntoMultiValuedTargetHoldingAStaleValue_StagesTheDifference()
    {
        var target = Target(AttributeDataType.Text, AttributePlurality.MultiValued);
        Expression("Split(mv[\"Codes\"], \";\")");
        var flowErrors = new List<AttributeFlowError>();

        var changes = Update(Person("a;b"), flowErrors,
            new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, StringValue = "a" },
            new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, StringValue = "stale" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => (c.ChangeType, c.StringValue)), Is.EquivalentTo(new[]
            {
                (PendingExportAttributeChangeType.Add, (string?)"b"),
                (PendingExportAttributeChangeType.Remove, (string?)"stale")
            }), "the Connected System must end up holding exactly the expression's values");
            Assert.That(flowErrors, Is.Empty);
        }
    }

    [TestCase("a", "b")]
    [TestCase("b", "a")]
    public void ComputeAttributeValueChanges_SplitToOneValueIntoMultiValuedTargetHoldingAStaleValue_RemovesTheStaleValue(
        string firstHeld, string secondHeld)
    {
        // A list is the whole set the target should hold whatever its length: one value left must remove the other,
        // exactly as it would had two remained, and whichever order the Connected System Object's values are cached in.
        var target = Target(AttributeDataType.Text, AttributePlurality.MultiValued);
        Expression("Split(mv[\"Codes\"], \";\")");
        var flowErrors = new List<AttributeFlowError>();

        var changes = Update(Person("a"), flowErrors,
            new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, StringValue = firstHeld },
            new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, StringValue = secondHeld });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => (c.ChangeType, c.StringValue)), Is.EquivalentTo(new[]
            {
                (PendingExportAttributeChangeType.Remove, (string?)"b")
            }), "the Connected System must end up holding exactly the expression's one value");
            Assert.That(flowErrors, Is.Empty);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_TextResultIntoNumberTarget_StagesANumber()
    {
        Target(AttributeDataType.Number);
        Expression("\"512\"");

        var changes = Create(Person(), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Single().IntValue, Is.EqualTo(512), "the value is converted to the target attribute's type");
            Assert.That(changes.Single().StringValue, Is.Null);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_TextResultIntoNumberTargetAlreadyHoldingIt_StagesNothing()
    {
        var target = Target(AttributeDataType.Number);
        Expression("\"512\"");

        var changes = Update(Person(), [], new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, IntValue = 512 });

        Assert.That(changes, Is.Empty, "a typed comparison finds the Connected System already holds the value");
    }

    [Test]
    public void ComputeAttributeValueChanges_TextResultIntoBooleanTarget_StagesABoolean()
    {
        Target(AttributeDataType.Boolean);
        Expression("\"TRUE\"");

        var changes = Create(Person(), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Single().BoolValue, Is.True, "the value is converted to the target attribute's type");
            Assert.That(changes.Single().StringValue, Is.Null, "a Boolean attribute can record only a true or false value");
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_TextResultIntoBooleanTargetAlreadyHoldingIt_StagesNothing()
    {
        var target = Target(AttributeDataType.Boolean);
        Expression("\"TRUE\"");

        var changes = Update(Person(), [], new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, BoolValue = true });

        Assert.That(changes, Is.Empty, "the export must not be staged again on every synchronisation");
    }

    [Test]
    public void ComputeAttributeValueChanges_ResultNotConvertibleToTheTargetType_StagesNothingAndRecordsAnError()
    {
        Target(AttributeDataType.Number);
        Expression("mv[\"Codes\"]");
        var flowErrors = new List<AttributeFlowError>();

        var changes = Create(Person("EMP-48213"), flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty, "text that is not a number must never be sent to a Number attribute");
            Assert.That(flowErrors, Has.Count.EqualTo(1));
            Assert.That(flowErrors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionResultNotConvertible));
            Assert.That(flowErrors[0].TargetAttributeName, Is.EqualTo("target"));
        }
    }

    [TestCase("en-GB")]
    [TestCase("en-US")]
    public void ComputeAttributeValueChanges_DateIntoTextTarget_UsesJimsDateTextFormUnderAnyCulture(string culture)
    {
        Target(AttributeDataType.Text);
        Expression("mv[\"When\"]");
        var mvo = Person(when: new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc));

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        List<PendingExportAttributeValueChange> changes;
        try
        {
            changes = Create(mvo, []);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.That(changes.Single().StringValue, Is.EqualTo("2026-03-15 10:30:00Z"));
    }

    [Test]
    public void ComputeAttributeValueChanges_IsoTextIntoDateTimeTarget_StagesTheUtcInstant()
    {
        Target(AttributeDataType.DateTime);
        Expression("\"2026-03-15T11:30:00+01:00\"");

        var staged = Create(Person(), []).Single().DateTimeValue!.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged, Is.EqualTo(new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc)));
            Assert.That(staged.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }
}
