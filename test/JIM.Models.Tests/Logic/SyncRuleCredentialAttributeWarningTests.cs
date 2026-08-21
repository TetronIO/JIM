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
/// The credential-like attribute warning on a Synchronisation Rule (#1119, requirement 16).
/// <para>
/// The eight well-known credential attributes are blocked outright by <c>CredentialAttributes</c>, but an
/// administrator can rename an attribute, or a target system can use a name JIM has never heard of, and nothing
/// stops an Attribute Flow carrying a password as an ordinary text value. Doing so persists the secret as a
/// Connected System Object attribute value, a Metaverse Object attribute value, in both change histories, in
/// Pending Exports, in export previews, in search results and the API, and in every database backup. It is the
/// exact exposure the password channel exists to avoid, reintroduced by the back door.
/// </para>
/// <para>
/// A warning rather than a refusal, deliberately: the heuristic is a substring match on a name, so it will
/// sometimes be wrong, and JIM does not own an administrator's schema. It is a guardrail that makes the safe
/// path the obvious one and the dangerous path deliberate.
/// </para>
/// </summary>
[TestFixture]
public class SyncRuleCredentialAttributeWarningTests
{
    private static SyncRule RuleFlowingTo(string? metaverseAttributeName, string? connectedSystemAttributeName)
    {
        var mapping = new SyncRuleMapping();

        if (metaverseAttributeName != null)
        {
            mapping.TargetMetaverseAttributeId = 1;
            mapping.TargetMetaverseAttribute = new MetaverseAttribute { Id = 1, Name = metaverseAttributeName };
        }

        if (connectedSystemAttributeName != null)
        {
            mapping.TargetConnectedSystemAttributeId = 2;
            mapping.TargetConnectedSystemAttribute =
                new ConnectedSystemObjectTypeAttribute { Id = 2, Name = connectedSystemAttributeName };
        }

        return new SyncRule
        {
            Name = "Flow attributes",
            Direction = SyncRuleDirection.Import,
            ConnectedSystem = new ConnectedSystem { Id = 1, Name = "HR", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "employee" },
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User" },
            AttributeFlowRules = [mapping]
        };
    }

    private static string[] CredentialWarnings(SyncRule rule) => rule.Validate()
        .Where(v => v.Level == ValidityStatusItemLevel.Warning && v.Message.Contains("password", System.StringComparison.OrdinalIgnoreCase))
        .Select(v => v.Message)
        .ToArray();

    [Test]
    public void Validate_WhenAFlowTargetsACredentialLikeMetaverseAttribute_Warns()
    {
        // An import flow is the worse direction: the value lands in the Metaverse, where every other system's
        // export can then pick it up.
        var rule = RuleFlowingTo("staffPasswordText", null);

        var warnings = CredentialWarnings(rule);

        Assert.That(warnings, Has.Length.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("staffPasswordText"));
    }

    [Test]
    public void Validate_WhenAFlowTargetsACredentialLikeConnectedSystemAttribute_Warns()
    {
        var rule = RuleFlowingTo(null, "userSecret");

        Assert.That(CredentialWarnings(rule), Has.Length.EqualTo(1));
    }

    [Test]
    public void Validate_WhenAFlowTargetsOrdinaryAttributes_DoesNotWarn()
    {
        var rule = RuleFlowingTo("displayName", "cn");

        Assert.That(CredentialWarnings(rule), Is.Empty);
    }

    [Test]
    public void Validate_TheWarningNamesThePasswordChannelAsTheAlternative()
    {
        // A warning that only says "this looks like a password" leaves the administrator with nowhere to go.
        var rule = RuleFlowingTo("accountPassword", null);

        Assert.That(CredentialWarnings(rule).Single(), Does.Contain("Password Synchronisation"));
    }

    [Test]
    public void Validate_ACredentialLikeFlow_DoesNotMakeTheRuleInvalid()
    {
        // Warn, never block: the heuristic matches on a name, so it will sometimes be wrong, and JIM does not
        // own an administrator's schema.
        var rule = RuleFlowingTo("passwordHint", null);

        Assert.That(rule.IsValid(), Is.True);
    }

    [Test]
    public void Validate_SeveralCredentialLikeFlows_WarnsOncePerAttribute()
    {
        // One warning naming every attribute would be easy to skim past; one per attribute is what an
        // administrator can act on.
        var rule = RuleFlowingTo("staffPassword", "userPasswordText");

        Assert.That(CredentialWarnings(rule), Has.Length.EqualTo(2));
    }
}
