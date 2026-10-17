// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// An Attribute Flow that reads or writes an attribute on the credential denylist (<c>password</c>, <c>unicodePwd</c>)
/// is an Error, so the Synchronisation Rule cannot be saved until it is removed (#2091). The warning-only heuristic
/// covers names that merely look like credentials; a denied name is certain, and adding <c>password</c> to the denylist
/// would otherwise have silenced the very warning a mapping to it raised.
/// </summary>
[TestFixture]
public class SyncRuleCredentialAttributeErrorTests
{
    private static SyncRule Rule(SyncRuleDirection direction, SyncRuleMapping mapping) => new()
    {
        Name = "Flow attributes",
        Direction = direction,
        ConnectedSystem = new ConnectedSystem { Id = 1, Name = "SCIM", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem },
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "User" },
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User" },
        AttributeFlowRules = [mapping]
    };

    private static ConnectedSystemObjectTypeAttribute CsAttribute(string name) => new() { Id = 2, Name = name };

    private static string[] Errors(SyncRule rule) => rule.Validate()
        .Where(item => item.Level == ValidityStatusItemLevel.Error)
        .Select(item => item.Message)
        .ToArray();

    [TestCase("password")]
    [TestCase("Password")]
    [TestCase("unicodePwd")]
    public void Validate_ExportFlowWritingACredentialAttribute_IsAnErrorAndTheRuleIsInvalid(string name)
    {
        var target = CsAttribute(name);
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = new MetaverseAttribute { Id = 1, Name = "Initial Secret" } });
        var rule = Rule(SyncRuleDirection.Export, mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(rule), Has.Length.EqualTo(1));
            Assert.That(Errors(rule)[0], Does.Contain($"'{name}'").And.Contain("Password Synchronisation"));
            Assert.That(rule.IsValid(), Is.False);
        }
    }

    [Test]
    public void Validate_ImportFlowReadingACredentialAttribute_IsAnError()
    {
        var source = CsAttribute("password");
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = new MetaverseAttribute { Id = 1, Name = "Secret" }, TargetMetaverseAttributeId = 1 };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id });

        Assert.That(Rule(SyncRuleDirection.Import, mapping).IsValid(), Is.False);
    }

    [Test]
    public void Validate_FlowWritingACredentialAttribute_IsNotAlsoWarnedAbout()
    {
        var target = CsAttribute("password");
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id };
        var rule = Rule(SyncRuleDirection.Export, mapping);

        Assert.That(rule.Validate().Where(item => item.Level == ValidityStatusItemLevel.Warning).Select(item => item.Message),
            Has.None.Contains("'password'"));
    }

    [Test]
    public void Validate_OrdinaryFlow_IsNotAnError()
    {
        var target = CsAttribute("pwdLastSet");
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id };

        Assert.That(Errors(Rule(SyncRuleDirection.Export, mapping)), Is.Empty);
    }
}
