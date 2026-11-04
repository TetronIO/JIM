// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How the LDAP Connector works out its Exchange sign-in from the settings it already has (#2021, D8 as amended): the
/// account is the directory account, found through the directory whichever form its Username takes; the Kerberos
/// principal is its sAMAccountName at the realm its entry sits in; and the KDC is the domain controller the bind
/// reached. Nothing about Kerberos is entered by the administrator.
/// </summary>
[TestFixture]
public class LdapExchangeSignInTests
{
    private const string DomainNamingContext = "DC=panoply,DC=local";
    private const string ServiceAccountDn = "CN=svc-jim,OU=Services,DC=panoply,DC=local";

    private static readonly LdapExchangeDomain Panoply = new(DomainNamingContext, "PANOPLY", "dc1.panoply.local");

    // The lookup kind is internal to the Connector, so cases name it.
    [TestCase(@"PANOPLY\svc-jim", "SamAccountName", "svc-jim", "PANOPLY")]
    [TestCase("svc-jim@panoply.local", "UserPrincipalName", "svc-jim@panoply.local", null)]
    [TestCase(ServiceAccountDn, "DistinguishedName", ServiceAccountDn, null)]
    [TestCase("svc-jim", "SamAccountName", "svc-jim", null)]
    [TestCase("  PANOPLY\\svc-jim  ", "SamAccountName", "svc-jim", "PANOPLY")]
    [TestCase(@"CN=Smith\, Jo,OU=Services,DC=panoply,DC=local", "DistinguishedName", @"CN=Smith\, Jo,OU=Services,DC=panoply,DC=local", null)]
    public void ParseUsername_EachForm_LooksTheAccountUpByIt(string username, string kind, string value, string? netBiosDomain)
    {
        var lookup = LdapExchangeSignIn.ParseUsername(username);

        Assert.That(lookup, Is.EqualTo(new LdapExchangeAccountLookup(Enum.Parse<LdapExchangeAccountLookupKind>(kind), value, netBiosDomain)));
    }

    [Test]
    public void SearchFor_SamAccountName_SearchesTheDomainWithTheValueEscaped()
    {
        var search = LdapExchangeSignIn.SearchFor(new LdapExchangeAccountLookup(LdapExchangeAccountLookupKind.SamAccountName, "svc*jim", null), DomainNamingContext);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(search.BaseDn, Is.EqualTo(DomainNamingContext));
            Assert.That(search.Filter, Is.EqualTo(@"(&(objectCategory=person)(sAMAccountName=svc\2ajim))"), "a Username can never widen the search");
            Assert.That(search.WholeSubtree, Is.True);
        }
    }

    [Test]
    public void SearchFor_UserPrincipalName_SearchesTheDomainByIt()
    {
        var search = LdapExchangeSignIn.SearchFor(new LdapExchangeAccountLookup(LdapExchangeAccountLookupKind.UserPrincipalName, "svc-jim@panoply.com", null), DomainNamingContext);

        Assert.That(search.Filter, Is.EqualTo("(&(objectCategory=person)(userPrincipalName=svc-jim@panoply.com))"));
    }

    [Test]
    public void SearchFor_DistinguishedName_ReadsThatEntryAlone()
    {
        var search = LdapExchangeSignIn.SearchFor(new LdapExchangeAccountLookup(LdapExchangeAccountLookupKind.DistinguishedName, ServiceAccountDn, null), DomainNamingContext);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(search.BaseDn, Is.EqualTo(ServiceAccountDn));
            Assert.That(search.WholeSubtree, Is.False);
        }
    }

    [TestCase("CN=svc-jim,OU=Services,DC=panoply,DC=local", "PANOPLY.LOCAL")]
    [TestCase("cn=svc-jim,ou=Services,dc=corp,dc=panoply,dc=local", "CORP.PANOPLY.LOCAL")]
    [TestCase("CN=svc-jim,OU=Services", null)]
    [TestCase("not a dn", null)]
    public void RealmFrom_DistinguishedName_IsItsDomainComponentsInCapitals(string distinguishedName, string? realm)
    {
        Assert.That(LdapExchangeSignIn.RealmFrom(distinguishedName), Is.EqualTo(realm));
    }

    [TestCase(@"PANOPLY\svc-jim")]
    [TestCase("svc-jim@panoply.local")]
    [TestCase("svc-jim@panoply.com")]
    [TestCase(ServiceAccountDn)]
    public void Resolve_AccountFoundInTheConnectedDomain_SignsInAsItsSamAccountNameAtTheRealmThroughTheDomainController(string username)
    {
        var resolution = LdapExchangeSignIn.Resolve(LdapExchangeSignIn.ParseUsername(username), new LdapExchangeAccount(ServiceAccountDn, "svc-jim"), Panoply);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Refusal, Is.Null);
            Assert.That(resolution.SignIn, Is.EqualTo(new LdapExchangeKerberosSignIn("svc-jim@PANOPLY.LOCAL", "PANOPLY.LOCAL", "dc1.panoply.local")));
        }
    }

    [Test]
    public void Resolve_AccountNotFound_RefusesNamingTheAccountAndTheDomain()
    {
        var resolution = LdapExchangeSignIn.Resolve(LdapExchangeSignIn.ParseUsername("svc-gone@panoply.local"), account: null, Panoply);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.SignIn, Is.Null);
            Assert.That(resolution.Refusal, Does.Contain("svc-gone@panoply.local").And.Contain("panoply.local"));
        }
    }

    [Test]
    public void Resolve_UsernameNamesAnotherDomain_RefusesWithoutLookingFurther()
    {
        // A trusted domain's account of the same name would otherwise be signed in as this domain's.
        var resolution = LdapExchangeSignIn.Resolve(LdapExchangeSignIn.ParseUsername(@"TRUSTED\svc-jim"), new LdapExchangeAccount(ServiceAccountDn, "svc-jim"), Panoply);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.SignIn, Is.Null);
            Assert.That(resolution.Refusal, Does.Contain("TRUSTED").And.Contain("PANOPLY"));
        }
    }

    [Test]
    public void Resolve_NetBiosDomainInAnotherCase_IsTheSameDomain()
    {
        var resolution = LdapExchangeSignIn.Resolve(LdapExchangeSignIn.ParseUsername(@"panoply\svc-jim"), new LdapExchangeAccount(ServiceAccountDn, "svc-jim"), Panoply);

        Assert.That(resolution.SignIn, Is.Not.Null);
    }

    [Test]
    public void Resolve_AccountEntryOutsideTheConnectedDomain_Refuses()
    {
        var resolution = LdapExchangeSignIn.Resolve(LdapExchangeSignIn.ParseUsername("CN=svc-jim,OU=Services,DC=other,DC=local"),
            new LdapExchangeAccount("CN=svc-jim,OU=Services,DC=other,DC=local", "svc-jim"), Panoply);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.SignIn, Is.Null);
            Assert.That(resolution.Refusal, Does.Contain("panoply.local"));
        }
    }

    [Test]
    public void Resolve_DomainControllerGaveNoDnsName_RefusesSayingWhy()
    {
        var resolution = LdapExchangeSignIn.Resolve(LdapExchangeSignIn.ParseUsername(@"PANOPLY\svc-jim"), new LdapExchangeAccount(ServiceAccountDn, "svc-jim"),
            Panoply with { KdcHost = null });

        Assert.That(resolution.Refusal, Does.Contain("DNS name"));
    }

    [TestCase("ex1.panoply.local", "http://ex1.panoply.local/PowerShell/")]
    [TestCase("  ex1.panoply.local  ", "http://ex1.panoply.local/PowerShell/")]
    [TestCase("ex1.panoply.local:8080", "http://ex1.panoply.local:8080/PowerShell/")]
    [TestCase("http://ex1.panoply.local/PowerShell/", "http://ex1.panoply.local/PowerShell/")]
    [TestCase("https://ex1.panoply.local/PowerShell/", "https://ex1.panoply.local/PowerShell/")]
    [TestCase("HTTPS://ex1.panoply.local/PowerShell", "https://ex1.panoply.local/PowerShell")]
    public void ParseExchangeServer_HostOrEndpoint_IsTheRemotePowerShellEndpoint(string value, string endpoint)
    {
        var parsed = LdapExchangeSignIn.ParseExchangeServer(value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.Refusal, Is.Null);
            Assert.That(parsed.Endpoint, Is.EqualTo(new Uri(endpoint)));
        }
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("ftp://ex1.panoply.local/")]
    [TestCase("ex1 panoply local")]
    [TestCase("http://")]
    public void ParseExchangeServer_NotAHostOrAnHttpEndpoint_RefusesSayingWhatItTakes(string? value)
    {
        var parsed = LdapExchangeSignIn.ParseExchangeServer(value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.Endpoint, Is.Null);
            Assert.That(parsed.Refusal, Does.Contain("host name").And.Contain("https://"));
        }
    }
}
