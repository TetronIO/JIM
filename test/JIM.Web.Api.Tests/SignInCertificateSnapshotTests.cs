// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using JIM.TestSupport;
using JIM.Web.Authentication;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The certificates trusted for sign-in, held in memory for the TLS validation callback, which cannot query the
/// database. It must follow the store closely (that is what makes trusting a certificate take effect without a
/// restart) without querying it on every handshake, and a database outage must not empty it.
/// </summary>
[TestFixture]
public class SignInCertificateSnapshotTests
{
    private ManualTimeProvider _time = null!;
    private X509Certificate2 _root = null!;
    private List<X509Certificate2> _store = null!;
    private int _loads;
    private Exception? _loadFailure;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _root = TestPki.PublicOnly(TestPki.CreateRoot("Contoso Root CA"));
        _store = [];
        _loads = 0;
        _loadFailure = null;
    }

    [TearDown]
    public void TearDown()
    {
        _root.Dispose();
    }

    [Test]
    public void Certificates_NeverRefreshed_Empty()
    {
        var snapshot = NewSnapshot();

        Assert.That(snapshot.Certificates, Is.Empty);
    }

    [Test]
    public async Task RefreshAsync_FirstCall_LoadsTheStoreAsync()
    {
        _store.Add(_root);
        var snapshot = NewSnapshot();

        await snapshot.RefreshAsync(CancellationToken.None);

        Assert.That(snapshot.Certificates, Is.EquivalentTo(new[] { _root }));
    }

    [Test]
    public async Task RefreshAsync_CalledAgainWhileFresh_DoesNotQueryTheStoreAgainAsync()
    {
        var snapshot = NewSnapshot();

        await snapshot.RefreshAsync(CancellationToken.None);
        _time.Advance(SignInCertificateSnapshot.FreshFor - TimeSpan.FromMilliseconds(1));
        await snapshot.RefreshAsync(CancellationToken.None);

        Assert.That(_loads, Is.EqualTo(1));
    }

    [Test]
    public async Task RefreshAsync_AfterItGoesStale_PicksUpACertificateAddedSinceAsync()
    {
        var snapshot = NewSnapshot();
        await snapshot.RefreshAsync(CancellationToken.None);

        _store.Add(_root);
        _time.Advance(SignInCertificateSnapshot.FreshFor);
        await snapshot.RefreshAsync(CancellationToken.None);

        Assert.That(snapshot.Certificates, Is.EquivalentTo(new[] { _root }));
    }

    [Test]
    public async Task Version_ChangesOnlyWhenTheSetOfCertificatesChangesAsync()
    {
        var snapshot = NewSnapshot();
        await snapshot.RefreshAsync(CancellationToken.None);
        var empty = snapshot.Version;

        _time.Advance(SignInCertificateSnapshot.FreshFor);
        await snapshot.RefreshAsync(CancellationToken.None);
        var stillEmpty = snapshot.Version;

        _store.Add(_root);
        _time.Advance(SignInCertificateSnapshot.FreshFor);
        await snapshot.RefreshAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillEmpty, Is.EqualTo(empty));
            Assert.That(snapshot.Version, Is.Not.EqualTo(empty));
        }
    }

    [Test]
    public async Task RefreshAsync_StoreUnavailable_KeepsWhatItHadAsync()
    {
        _store.Add(_root);
        var snapshot = NewSnapshot();
        await snapshot.RefreshAsync(CancellationToken.None);

        _loadFailure = new InvalidOperationException("database unavailable");
        _time.Advance(SignInCertificateSnapshot.FreshFor);

        Assert.That(async () => await snapshot.RefreshAsync(CancellationToken.None), Throws.Nothing);
        Assert.That(snapshot.Certificates, Is.EquivalentTo(new[] { _root }));
    }

    private SignInCertificateSnapshot NewSnapshot() => new(_ =>
    {
        _loads++;
        if (_loadFailure != null)
            throw _loadFailure;
        return Task.FromResult<IReadOnlyCollection<X509Certificate2>>(_store.ToArray());
    }, _time);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
