// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Kerberos;

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

public class Krb5ConfigurationTests
{
    [Test]
    public void Render_OneRealmWithKdcs_ListsEachKdcAndMapsTheRealmsDomain()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local", "dc2.panoply.local"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("default_realm = PANOPLY.LOCAL"));
            Assert.That(text, Does.Contain("kdc = dc1.panoply.local"));
            Assert.That(text, Does.Contain("kdc = dc2.panoply.local"));
            Assert.That(text, Does.Contain(".panoply.local = PANOPLY.LOCAL"));
            Assert.That(text, Does.Contain("panoply.local = PANOPLY.LOCAL"));
        }
    }

    [Test]
    public void Render_RealmGivenInLowerCase_IsWrittenInUpperCase()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("panoply.local", [])]);

        Assert.That(text, Does.Contain("PANOPLY.LOCAL = {"));
    }

    [Test]
    public void Render_RealmWithoutKdcs_FindsItsKdcsThroughDns()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", [])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("dns_lookup_kdc = true"));
            Assert.That(text.Split('\n').Select(l => l.Trim()), Has.None.StartsWith("kdc ="));
        }
    }

    [Test]
    public void Render_AnyRealm_AllowsOnlyAesEncryptionTypes()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", [])]);
        var enctypeLines = text.Split('\n').Where(l => l.TrimStart().StartsWith("permitted_enctypes", StringComparison.Ordinal)
                                                      || l.TrimStart().StartsWith("default_tkt_enctypes", StringComparison.Ordinal)
                                                      || l.TrimStart().StartsWith("default_tgs_enctypes", StringComparison.Ordinal)).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(enctypeLines, Has.Count.EqualTo(3));
            Assert.That(enctypeLines, Has.All.Contains("aes256-cts-hmac-sha1-96"));
            Assert.That(enctypeLines, Has.None.Contains("rc4"));
            Assert.That(enctypeLines, Has.None.Contains("des"));
        }
    }

    [Test]
    public void Render_AnyRealm_NeverResolvesTheServiceNameThroughReverseDns()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", [])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("rdns = false"));
            Assert.That(text, Does.Contain("dns_canonicalize_hostname = false"));
            Assert.That(text, Does.Contain("forwardable = false"));
        }
    }

    [Test]
    public void Render_AnyRealm_KeepsTicketsInMemoryOnly()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", [])]);

        Assert.That(text, Does.Match(@"default_ccache_name = MEMORY:\S+"));
    }

    [Test]
    public void Render_TwoRealms_WritesBothAndMakesTheFirstTheDefault()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", []), new KerberosRealm("RESURGAM.LOCAL", ["dc1.resurgam.local"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("default_realm = PANOPLY.LOCAL"));
            Assert.That(text, Does.Contain("RESURGAM.LOCAL = {"));
            Assert.That(text, Does.Contain(".resurgam.local = RESURGAM.LOCAL"));
        }
    }

    [Test]
    public void Render_SameRealmTwiceInDifferentCase_IsRefused()
    {
        Assert.That(() => Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", []), new KerberosRealm("panoply.local", [])]),
            Throws.ArgumentException.With.Message.Contains("PANOPLY.LOCAL"));
    }

    [Test]
    public void Render_NoRealms_IsRefused()
    {
        Assert.That(() => Krb5Configuration.Render([]), Throws.ArgumentException);
    }

    [TestCase("PANOPLY.LOCAL\n[libdefaults]")]
    [TestCase("PANOPLY.LOCAL }")]
    [TestCase("PANOPLY = LOCAL")]
    [TestCase("PANOPLY.LOCAL # comment")]
    [TestCase("")]
    [TestCase(".PANOPLY.LOCAL")]
    public void Render_RealmThatIsNotAPlainDomainName_IsRefusedSoItCannotRewriteTheFile(string realm)
    {
        Assert.That(() => Krb5Configuration.Render([new KerberosRealm(realm, [])]), Throws.ArgumentException);
    }

    [TestCase("dc1.panoply.local\n  admin_server = evil")]
    [TestCase("dc1.panoply.local }")]
    [TestCase("dc1 panoply")]
    public void Render_KdcThatIsNotAPlainHostName_IsRefusedSoItCannotRewriteTheFile(string kdc)
    {
        Assert.That(() => Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", [kdc])]), Throws.ArgumentException);
    }

    [Test]
    public void Render_KdcWithAPort_IsAccepted()
    {
        var text = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local:88"])]);

        Assert.That(text, Does.Contain("kdc = dc1.panoply.local:88"));
    }

    [Test]
    public void Render_SameRealmsInAnyOrder_ProducesTheSameTextApartFromTheDefaultRealm()
    {
        var a = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", ["b.panoply.local", "a.panoply.local"])]);
        var b = Krb5Configuration.Render([new KerberosRealm("PANOPLY.LOCAL", ["b.panoply.local", "a.panoply.local"])]);

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public async Task WriteAsync_NewFile_IsReadableAndWritableByTheOwnerOnlyAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("File modes are a Unix concept.");
            return;
        }

        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "krb5.conf");

            await Krb5Configuration.WriteAsync(path, [new KerberosRealm("PANOPLY.LOCAL", [])]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
                Assert.That(await File.ReadAllTextAsync(path), Does.Contain("PANOPLY.LOCAL"));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task WriteAsync_ExistingFile_IsReplacedWholeAndLeavesNoTemporaryFileAsync()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "krb5.conf");
            await Krb5Configuration.WriteAsync(path, [new KerberosRealm("PANOPLY.LOCAL", [])]);

            await Krb5Configuration.WriteAsync(path, [new KerberosRealm("RESURGAM.LOCAL", [])]);

            using (Assert.EnterMultipleScope())
            {
                var text = await File.ReadAllTextAsync(path);
                Assert.That(text, Does.Contain("RESURGAM.LOCAL"));
                Assert.That(text, Does.Not.Contain("PANOPLY.LOCAL"));
                Assert.That(Directory.GetFiles(directory.FullName), Has.Length.EqualTo(1));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
