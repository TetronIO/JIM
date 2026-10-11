// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What happens when a target refuses a value built from a generated value (Generated Value Forms, #2014), as the
/// "Values built from ..." table's "If a target refuses it" column decides it: the reasons a value is not corrected
/// take precedence over its switch.
/// </summary>
[TestFixture]
public class GeneratedValueBuiltValueWordingTests
{
    private static GeneratedValueBuiltValueDescription Value(GeneratedValueBuiltValueKind kind = GeneratedValueBuiltValueKind.Form,
        bool keepUnique = true, IReadOnlyList<string>? via = null, string? metaverseAttributeName = "Email", bool? checkedBeforeIssuing = null,
        IReadOnlyList<string>? probedIn = null, string? moveTo = null, string syncRuleName = "HR Import") => new()
    {
        CheckedBeforeIssuing = checkedBeforeIssuing ?? (kind == GeneratedValueBuiltValueKind.Form && keepUnique),
        ProbedIn = probedIn ?? [],
        MoveToSyncRuleName = moveTo,
        SyncRuleMappingId = 7,
        SyncRuleName = syncRuleName,
        GeneratedAttributeName = "Account Name",
        BuiltFromAttributeNames = kind == GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues ? ["Account Name", "Badge Number"] : ["Account Name"],
        MetaverseAttributeName = metaverseAttributeName,
        ConnectedSystemAttributeName = metaverseAttributeName == null ? "sAMAccountName" : null,
        Via = via ?? [],
        Kind = kind,
        KeepUnique = keepUnique
    };

    [Test]
    public void RefusalOutcome_SwitchOnAndRemediationOn_IsCorrected()
    {
        Assert.That(GeneratedValueBuiltValueWording.RefusalOutcome(Value(), collisionRemediation: true),
            Is.EqualTo(GeneratedValueBuiltValueRefusal.GeneratedValueCorrected));
    }

    [Test]
    public void RefusalOutcome_KeepUniqueOff_IsAnExportError()
    {
        Assert.That(GeneratedValueBuiltValueWording.RefusalOutcome(Value(keepUnique: false), collisionRemediation: true),
            Is.EqualTo(GeneratedValueBuiltValueRefusal.KeepUniqueOff));
    }

    [Test]
    public void RefusalOutcome_GeneratedFlowsCollisionRemediationOff_WinsOverTheSwitch()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueBuiltValueWording.RefusalOutcome(Value(), collisionRemediation: false),
                Is.EqualTo(GeneratedValueBuiltValueRefusal.CollisionRemediationOff));
            Assert.That(GeneratedValueBuiltValueWording.RefusalOutcome(Value(keepUnique: false), collisionRemediation: false),
                Is.EqualTo(GeneratedValueBuiltValueRefusal.CollisionRemediationOff));
        }
    }

    [Test]
    public void RefusalOutcome_BuiltFromSeveralGeneratedValues_IsNeverCorrectedWhateverTheSwitches()
    {
        var several = Value(GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueBuiltValueWording.RefusalOutcome(several, collisionRemediation: true),
                Is.EqualTo(GeneratedValueBuiltValueRefusal.BuiltFromSeveral));
            Assert.That(GeneratedValueBuiltValueWording.RefusalOutcome(several, collisionRemediation: false),
                Is.EqualTo(GeneratedValueBuiltValueRefusal.BuiltFromSeveral));
        }
    }

    [Test]
    public void HowBuilt_ThroughAnotherDerivedValue_NamesTheValuesItIsBuiltThrough()
    {
        Assert.That(GeneratedValueBuiltValueWording.HowBuilt(Value(via: ["Email"])), Does.Contain("Email"));
    }

    [Test]
    public void HowBuilt_FromSeveralGeneratedValues_NamesTheOthers()
    {
        Assert.That(GeneratedValueBuiltValueWording.HowBuilt(Value(GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues)), Does.Contain("Badge Number"));
    }

    // ---- Before issuing (Generated Value Forms, #2023) ----

    [Test]
    public void BeforeIssuingOutcome_EachKindOfValue_SaysWhetherAndWhyItIsChecked()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingOutcome(Value()), Is.EqualTo(GeneratedValueBuiltValueBeforeIssuing.Checked));
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingOutcome(Value(keepUnique: false)), Is.EqualTo(GeneratedValueBuiltValueBeforeIssuing.KeepUniqueOff));
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingOutcome(Value(GeneratedValueBuiltValueKind.BuiltOnAnotherRule)),
                Is.EqualTo(GeneratedValueBuiltValueBeforeIssuing.BuiltOnAnotherRule));
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingOutcome(Value(GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues, keepUnique: false)),
                Is.EqualTo(GeneratedValueBuiltValueBeforeIssuing.BuiltFromSeveral), "a value built from several is never checked, whatever its switch");
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingOutcome(Value(checkedBeforeIssuing: false)),
                Is.EqualTo(GeneratedValueBuiltValueBeforeIssuing.Excluded), "on the generated value's own rule and switched on, yet not checked: excluded");
        }
    }

    [Test]
    public void BeforeIssuingOutcome_SwitchTurnedOnSinceTheValueWasDescribed_FollowsTheSwitch()
    {
        // The flow's dialog describes the value once and edits the switch in place.
        var describedOff = Value(keepUnique: false, checkedBeforeIssuing: false);

        Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingOutcome(describedOff, keepUnique: true), Is.EqualTo(GeneratedValueBuiltValueBeforeIssuing.Checked));
    }

    [Test]
    public void BeforeIssuingDetail_CheckedValue_NamesTheConnectedSystemsItIsProbedIn()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingDetail(Value(probedIn: ["Corp AD"])), Does.Contain("Corp AD"));
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingDetail(Value()), Does.Not.Contain(" and "), "JIM's records alone");
        }
    }

    [Test]
    public void BeforeIssuingDetail_ValueBuiltOnAnotherRule_SuggestsTheMoveOnlyWhereItHelps()
    {
        var movable = Value(GeneratedValueBuiltValueKind.BuiltOnAnotherRule, moveTo: "HR Import", syncRuleName: "Mail Import");
        var notMovable = Value(GeneratedValueBuiltValueKind.BuiltOnAnotherRule, syncRuleName: "Mail Import");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingDetail(movable), Does.Contain("Mail Import").And.Contain("HR Import"));
            Assert.That(GeneratedValueBuiltValueWording.BeforeIssuingDetail(notMovable), Does.Contain("Mail Import").And.Not.Contain("HR Import"));
        }
    }

    [Test]
    public void SwitchHelpText_SwitchOn_SaysWhetherTheValueIsCheckedBeforeIssuing()
    {
        var onAnotherRule = Value(GeneratedValueBuiltValueKind.BuiltOnAnotherRule, moveTo: "HR Import", syncRuleName: "Mail Import");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueBuiltValueWording.SwitchHelpText(Value(), keepUnique: true), Does.StartWith("Checked before Account Name is issued"));
            Assert.That(GeneratedValueBuiltValueWording.SwitchHelpText(onAnotherRule, keepUnique: true),
                Does.StartWith("Not checked before Account Name is issued").And.Contain("Mail Import").And.Contain("HR Import"));
            Assert.That(GeneratedValueBuiltValueWording.SwitchHelpText(Value(), keepUnique: false), Does.Not.Contain("checked before"),
                "switched off, it is neither checked nor corrected");
        }
    }
}
