// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers how the facts a password policy reader needs are derived from the rootDSE.
/// </summary>
[TestFixture]
public class LdapPasswordPolicyScopeTests
{
    [Test]
    public void From_WithAFullRootDse_CarriesEveryFact()
    {
        var rootDse = new LdapConnectorRootDse
        {
            DefaultNamingContext = "dc=example,dc=local",
            NamingContexts = ["dc=example,dc=local", "cn=config"],
            ConfigContext = "cn=config",
            SupportedControls = ["2.16.840.1.113730.3.4.18", LdapPasswordPolicyScope.PasswordPolicyRequestControlOid]
        };

        var scope = LdapPasswordPolicyScope.From(rootDse);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.DefaultNamingContext, Is.EqualTo("dc=example,dc=local"));
            Assert.That(scope.NamingContexts, Is.EqualTo(new[] { "dc=example,dc=local" }));
            Assert.That(scope.ConfigContext, Is.EqualTo("cn=config"));
            Assert.That(scope.AdvertisesPasswordPolicyControl, Is.True);
        }
    }

    /// <summary>
    /// The configuration, schema, monitor and log contexts are not places users live, so a probe there is a search
    /// spent on nothing. Only the contexts that could hold accounts are kept.
    /// </summary>
    [Test]
    public void From_FiltersNamingContextsToThoseThatCouldHoldUsers()
    {
        var rootDse = new LdapConnectorRootDse
        {
            NamingContexts = ["cn=config", "cn=schema", "cn=monitor", "cn=accesslog", "cn=changelog", "o=netscaperoot", "dc=yellowstone,dc=local", "o=acme"],
            ConfigContext = "cn=config"
        };

        var scope = LdapPasswordPolicyScope.From(rootDse);

        Assert.That(scope.NamingContexts, Is.EqualTo(new[] { "dc=yellowstone,dc=local", "o=acme" }));
    }

    [Test]
    public void From_WithNothingPublished_IsEmptyAndDoesNotAdvertiseTheControl()
    {
        var scope = LdapPasswordPolicyScope.From(new LdapConnectorRootDse());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.DefaultNamingContext, Is.Null);
            Assert.That(scope.NamingContexts, Is.Empty);
            Assert.That(scope.ConfigContext, Is.Null);
            Assert.That(scope.AdvertisesPasswordPolicyControl, Is.False);
        }
    }

    [Test]
    public void PrimaryNamingContext_PrefersTheDefaultNamingContextOverTheFirstUserContext()
    {
        var scope = new LdapPasswordPolicyScope("dc=default,dc=local", ["dc=first,dc=local"], null, false);

        Assert.That(scope.PrimaryNamingContext, Is.EqualTo("dc=default,dc=local"));
    }

    [Test]
    public void PrimaryNamingContext_WithoutADefault_IsTheFirstUserContext()
    {
        var scope = new LdapPasswordPolicyScope(null, ["dc=first,dc=local", "dc=second,dc=local"], null, false);

        Assert.That(scope.PrimaryNamingContext, Is.EqualTo("dc=first,dc=local"));
    }
}
