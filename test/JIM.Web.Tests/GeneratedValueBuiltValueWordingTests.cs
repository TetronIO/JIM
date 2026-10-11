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
        bool keepUnique = true, IReadOnlyList<string>? via = null, string? metaverseAttributeName = "Email") => new()
    {
        SyncRuleMappingId = 7,
        SyncRuleName = "HR Import",
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
}
