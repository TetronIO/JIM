// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Tetron.PowerShellRemoting.Kerberos;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The one Kerberos configuration file a JIM process signs in to Exchange with (#2021, D8). Kerberos reads a single file
/// per process, so every Connected System's realm has to be in it, and the web process needs one as much as the Worker:
/// saving Exchange settings signs in from there.
/// </summary>
[TestFixture]
public class ExchangeKerberosConfigurationTests
{
    private readonly List<string> _directories = [];
    private readonly List<string> _applied = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
        _directories.Clear();
        _applied.Clear();
    }

    [Test]
    public async Task EnsureRealmAsync_FirstRealm_WritesTheFileAndPointsKerberosAtItAsync()
    {
        var configuration = NewConfiguration();

        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));

        var text = await File.ReadAllTextAsync(configuration.ConfigurationPath!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("PANOPLY.LOCAL = {"));
            Assert.That(text, Does.Contain("kdc = dc1.panoply.local"));
            Assert.That(_applied, Is.EqualTo(new[] { configuration.ConfigurationPath }), "KRB5_CONFIG names the file, once");
        }
    }

    [Test]
    public async Task EnsureRealmAsync_AnyRealm_AllowsOnlyAesAndNeverResolvesNamesThroughReverseDnsAsync()
    {
        var configuration = NewConfiguration();

        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));

        var text = await File.ReadAllTextAsync(configuration.ConfigurationPath!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("permitted_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96"));
            Assert.That(text, Does.Not.Contain("rc4").IgnoreCase);
            Assert.That(text, Does.Contain("rdns = false"));
        }
    }

    [Test]
    public async Task EnsureRealmAsync_FileAndItsDirectory_AreReadableByTheProcessOwnerAloneAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Unix permissions.");
            return;
        }

        var configuration = NewConfiguration();

        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));

        var path = configuration.ConfigurationPath!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(path)!),
                Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        }
    }

    [Test]
    public async Task EnsureRealmAsync_RealmItAlreadyHolds_LeavesTheFileAloneAsync()
    {
        var configuration = NewConfiguration();
        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));
        await File.AppendAllTextAsync(configuration.ConfigurationPath!, "# untouched\n");

        // The same realm and KDC, as another Connected System on the same domain would name them.
        await configuration.EnsureRealmAsync(new KerberosRealm("panoply.local", ["DC1.panoply.local"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllTextAsync(configuration.ConfigurationPath!), Does.EndWith("# untouched\n"),
                "nothing changed, so the file is not rewritten under a session reading it");
            Assert.That(_applied, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task EnsureRealmAsync_SecondRealm_KeepsBothAsync()
    {
        var configuration = NewConfiguration();

        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));
        await configuration.EnsureRealmAsync(new KerberosRealm("RESURGAM.LOCAL", ["dc1.resurgam.local"]));

        var text = await File.ReadAllTextAsync(configuration.ConfigurationPath!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain("PANOPLY.LOCAL = {"));
            Assert.That(text, Does.Contain("RESURGAM.LOCAL = {"));
            Assert.That(text, Does.Contain("default_realm = PANOPLY.LOCAL"), "the first realm stays the default");
            Assert.That(_applied, Has.Count.EqualTo(1), "a rewrite needs no new KRB5_CONFIG: Kerberos rereads the file");
        }
    }

    /// <summary>
    /// Two Connected Systems on one domain can pin different domain controllers. Either is a KDC for the realm, so the
    /// file keeps both, and the one just asked for goes first: it is the one that system's administrator chose.
    /// </summary>
    [Test]
    public async Task EnsureRealmAsync_SameRealmWithAnotherKdc_ListsTheNewKdcFirstAndKeepsTheOtherAsync()
    {
        var configuration = NewConfiguration();
        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));

        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc2.panoply.local"]));

        var kdcs = (await File.ReadAllLinesAsync(configuration.ConfigurationPath!))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("kdc = ", StringComparison.Ordinal))
            .ToList();
        Assert.That(kdcs, Is.EqualTo(new[] { "kdc = dc2.panoply.local", "kdc = dc1.panoply.local" }));
    }

    [Test]
    public async Task EnsureRealmAsync_ManyCallersAtOnce_WriteOneFileHoldingEveryRealmAsync()
    {
        var configuration = NewConfiguration();
        var realms = Enumerable.Range(1, 20).Select(i => new KerberosRealm($"DOMAIN{i}.LOCAL", [$"dc1.domain{i}.local"])).ToList();

        await Task.WhenAll(realms.Select(r => Task.Run(() => configuration.EnsureRealmAsync(r))));

        var text = await File.ReadAllTextAsync(configuration.ConfigurationPath!);
        using (Assert.EnterMultipleScope())
        {
            foreach (var realm in realms)
                Assert.That(text, Does.Contain($"{realm.Name} = {{"));
            Assert.That(_applied, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task EnsureRealmAsync_RealmThatIsNotAPlainName_IsRefusedAndLeavesTheFileAsItWasAsync()
    {
        var configuration = NewConfiguration();
        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));
        var before = await File.ReadAllTextAsync(configuration.ConfigurationPath!);

        Assert.That(() => configuration.EnsureRealmAsync(new KerberosRealm("EVIL.LOCAL }", ["dc1.evil.local"])),
            Throws.InstanceOf<ArgumentException>());
        await configuration.EnsureRealmAsync(new KerberosRealm("RESURGAM.LOCAL", ["dc1.resurgam.local"]));

        var after = await File.ReadAllTextAsync(configuration.ConfigurationPath!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Does.Not.Contain("EVIL"));
            Assert.That(after, Does.Not.Contain("EVIL"), "a refused realm is not kept to be written by the next caller");
            Assert.That(after, Does.Contain("RESURGAM.LOCAL = {"));
        }
    }

    [Test]
    public void NothingEnsured_WritesNothingAndLeavesKerberosAlone()
    {
        var created = 0;
        var configuration = new ExchangeKerberosConfiguration(_applied.Add, () =>
        {
            created++;
            return NewDirectory();
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.ConfigurationPath, Is.Null);
            Assert.That(created, Is.Zero, "a process that never reaches Exchange creates nothing");
            Assert.That(_applied, Is.Empty);
        }
    }

    [Test]
    public async Task EnsureRealmAsync_PointingKerberosAtTheFileFails_TriesAgainNextTimeAsync()
    {
        var attempts = 0;
        var configuration = new ExchangeKerberosConfiguration(path =>
        {
            if (++attempts == 1)
                throw new InvalidOperationException("Could not set KRB5_CONFIG.");
            _applied.Add(path);
        }, NewDirectory);

        Assert.That(() => configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"])),
            Throws.InvalidOperationException);
        await configuration.EnsureRealmAsync(new KerberosRealm("PANOPLY.LOCAL", ["dc1.panoply.local"]));

        Assert.That(_applied, Is.EqualTo(new[] { configuration.ConfigurationPath }),
            "a session must never open against a file Kerberos was not pointed at");
    }

    private ExchangeKerberosConfiguration NewConfiguration() => new(_applied.Add, NewDirectory);

    private string NewDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("jim-kerberos-tests-").FullName;
        _directories.Add(directory);
        return directory;
    }
}
