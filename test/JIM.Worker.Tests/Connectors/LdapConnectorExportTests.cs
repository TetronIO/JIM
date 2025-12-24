using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapConnectorExportTests
{
    #region BuildContainerChain tests

    [Test]
    public void BuildContainerChain_SingleOu_ReturnsSingleOu()
    {
        var result = LdapConnectorExport.BuildContainerChain("OU=Users,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo("OU=Users,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_NestedOus_ReturnsRootToLeafOrder()
    {
        var result = LdapConnectorExport.BuildContainerChain("OU=Engineering,OU=Departments,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(2));
        // Should be ordered from root to leaf (parent first, then child)
        Assert.That(result[0], Is.EqualTo("OU=Departments,DC=testdomain,DC=local"));
        Assert.That(result[1], Is.EqualTo("OU=Engineering,OU=Departments,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_DeeplyNestedOus_ReturnsAllInCorrectOrder()
    {
        var result = LdapConnectorExport.BuildContainerChain("OU=Team1,OU=Engineering,OU=Departments,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.EqualTo("OU=Departments,DC=testdomain,DC=local"));
        Assert.That(result[1], Is.EqualTo("OU=Engineering,OU=Departments,DC=testdomain,DC=local"));
        Assert.That(result[2], Is.EqualTo("OU=Team1,OU=Engineering,OU=Departments,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_CnContainer_ReturnsCn()
    {
        var result = LdapConnectorExport.BuildContainerChain("CN=Users,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo("CN=Users,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_MixedOuAndCn_ReturnsBoth()
    {
        // Example: OU inside a CN container
        var result = LdapConnectorExport.BuildContainerChain("OU=Admins,CN=Users,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo("CN=Users,DC=testdomain,DC=local"));
        Assert.That(result[1], Is.EqualTo("OU=Admins,CN=Users,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_DomainOnlyDn_ReturnsEmptyList()
    {
        var result = LdapConnectorExport.BuildContainerChain("DC=testdomain,DC=local");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void BuildContainerChain_EmptyString_ReturnsEmptyList()
    {
        var result = LdapConnectorExport.BuildContainerChain("");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void BuildContainerChain_SingleDc_ReturnsEmptyList()
    {
        var result = LdapConnectorExport.BuildContainerChain("DC=local");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void BuildContainerChain_OuWithEscapedComma_HandlesCorrectly()
    {
        // Test an OU with an escaped comma in the name
        var result = LdapConnectorExport.BuildContainerChain(@"OU=Research\, Development,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(@"OU=Research\, Development,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_NestedOusWithEscapedComma_HandlesCorrectly()
    {
        var result = LdapConnectorExport.BuildContainerChain(@"OU=Team A,OU=Research\, Development,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo(@"OU=Research\, Development,DC=testdomain,DC=local"));
        Assert.That(result[1], Is.EqualTo(@"OU=Team A,OU=Research\, Development,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_CaseInsensitiveRdnTypes_Works()
    {
        // Test that OU, ou, Ou all work
        var result = LdapConnectorExport.BuildContainerChain("ou=Users,DC=testdomain,DC=local");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo("ou=Users,DC=testdomain,DC=local"));
    }

    [Test]
    public void BuildContainerChain_ComplexRealWorldDn_ReturnsCorrectChain()
    {
        // Simulates a typical user DN where we want to ensure the OU hierarchy exists
        // For a user at: CN=John Smith,OU=Engineering,OU=Departments,DC=corp,DC=local
        // The parent container would be: OU=Engineering,OU=Departments,DC=corp,DC=local
        var result = LdapConnectorExport.BuildContainerChain("OU=Engineering,OU=Departments,DC=corp,DC=local");

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo("OU=Departments,DC=corp,DC=local"));
        Assert.That(result[1], Is.EqualTo("OU=Engineering,OU=Departments,DC=corp,DC=local"));
    }

    #endregion
}
