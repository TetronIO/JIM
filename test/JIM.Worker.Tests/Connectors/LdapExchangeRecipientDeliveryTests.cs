// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exchange;
using JIM.Models.Transactional;
using NUnit.Framework;
using Tetron.PowerShellRemoting;
using Tetron.PowerShellRemoting.Kerberos;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's Exchange session for the delivery lane (#2021, requirements 8, 9, 15 to 17 and 26): it signs in
/// once, then for each change reads the recipient, runs only what converges it, and reads again after an enable. A
/// failure comes back as a classified result, never an exception, so one bad recipient never stops the lane; only
/// cancellation and a session that cannot be opened throw.
/// </summary>
[TestFixture]
public class LdapExchangeRecipientDeliveryTests
{
    private static readonly LdapExchangeKerberosSignIn SignIn = new("svc-jim@PANOPLY.LOCAL", "PANOPLY.LOCAL", "dc1.panoply.local");
    private static readonly Uri Endpoint = new("http://ex1.panoply.local/PowerShell/");

    private static readonly ExchangeRecipientTarget Alice = new()
    {
        ConnectedSystemObjectId = Guid.NewGuid(),
        ObjectGuid = Guid.NewGuid(),
        DistinguishedName = "CN=Alice,OU=People,DC=panoply,DC=local",
        ObjectType = "user",
        DomainController = "dc1.panoply.local"
    };

    private FakeSession _session = null!;
    private List<KerberosRealm> _realms = null!;
    private RemoteSessionOptions? _opened;

    [SetUp]
    public void SetUp()
    {
        _session = new FakeSession();
        _realms = [];
        _opened = null;
    }

    [TearDown]
    public void TearDown() => _session.Dispose();

    // ---- Opening -----------------------------------------------------------------------------------------------

    [Test]
    public async Task OpenAsync_SignsInToExchangesEndpointAsTheDirectoryAccountAfterTeachingKerberosTheRealmAsync()
    {
        await Delivery().OpenAsync(SignIn, Endpoint, "secret", [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_realms.Single().Name, Is.EqualTo("PANOPLY.LOCAL"));
            Assert.That(_realms.Single().Kdcs, Is.EqualTo(new[] { "dc1.panoply.local" }));
            Assert.That(_opened!.ConnectionUri, Is.EqualTo(Endpoint));
            Assert.That(_opened.ConfigurationName, Is.EqualTo("Microsoft.Exchange"));
            Assert.That(_opened.UserPrincipalName, Is.EqualTo("svc-jim@PANOPLY.LOCAL"));
        }
    }

    [Test]
    public void OpenAsync_SignInRefused_ThrowsASessionExceptionTheLaneRecordsAgainstEveryChange()
    {
        var delivery = Delivery(open: (_, _) => throw new KerberosException("gss_init_sec_context", 0xD0000, 0x96C73A00 + 24, "Preauthentication failed"));

        var ex = Assert.ThrowsAsync<ExchangeSessionException>(() => delivery.OpenAsync(SignIn, Endpoint, "wrong", [], CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.AuthenticationFailed));
            Assert.That(ex.Message, Does.Contain("svc-jim@PANOPLY.LOCAL"), "The administrator is told whose sign-in failed, in the save check's words.");
        }
    }

    [Test]
    public void OpenAsync_ExchangeUnreachable_ThrowsUnreachable()
    {
        var delivery = Delivery(open: (_, _) => throw new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));

        var ex = Assert.ThrowsAsync<ExchangeSessionException>(() => delivery.OpenAsync(SignIn, Endpoint, "secret", [], CancellationToken.None));

        Assert.That(ex!.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
    }

    [Test]
    public void OpenAsync_Cancelled_ThrowsCancellationNotAFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var delivery = Delivery(open: (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<ILdapExchangeRecipientSession>(_session); });

        Assert.That(() => delivery.OpenAsync(SignIn, Endpoint, "secret", [], cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    // ---- Converging --------------------------------------------------------------------------------------------

    [Test]
    public async Task ConvergeAsync_UserNotYetARecipient_EnablesTheMailboxReadsItAgainAndSetsWhatTheEnableCouldNotAsync()
    {
        var delivery = await OpenedAsync();
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.None, null, null, null));
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.Mailbox, "alice", "alice@panoply.example", null));

        var result = await delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox) with { PrimarySmtpAddress = "alice.smith@panoply.example" }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Delivered, Is.True);
            Assert.That(result.ChangedAnything, Is.True);
            Assert.That(_session.Ran.Select(c => c.Name), Is.EqualTo(new[] { "Enable-Mailbox", "Set-Mailbox" }));
            Assert.That(_session.ReadCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task ConvergeAsync_RecipientAlreadyInItsDeclaredState_RunsNothingAndSaysSoAsync()
    {
        var delivery = await OpenedAsync();
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.Mailbox, "alice", "alice@panoply.example", null));

        var result = await delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox) with { Alias = "alice" }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Delivered, Is.True);
            Assert.That(result.ChangedAnything, Is.False);
            Assert.That(_session.Ran, Is.Empty);
        }
    }

    [Test]
    public async Task ConvergeAsync_TypeChange_IsRefusedWithTheReasonAndRunsNothingAsync()
    {
        var delivery = await OpenedAsync();
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.Mailbox, "alice", null, null));

        var result = await delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.SharedMailbox), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Delivered, Is.False);
            Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(result.Message, Does.Contain("SharedMailbox"));
            Assert.That(_session.Ran, Is.Empty);
        }
    }

    [Test]
    public async Task ConvergeAsync_ObjectExchangeCannotFind_IsObjectNotFoundSoTheLaneRetriesAsync()
    {
        var delivery = await OpenedAsync();
        _session.ReadFailure = new RemoteFailure
        {
            Kind = RemoteFailureKind.Command,
            Message = "The operation couldn't be performed because object couldn't be found.",
            FailureCategory = "Cmdlet-ManagementObjectNotFoundException"
        };

        var result = await delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox), CancellationToken.None);

        Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.ObjectNotFound));
    }

    [Test]
    public async Task ConvergeAsync_CommandTheAccountCannotSee_IsInsufficientPermissionsAsync()
    {
        var delivery = await OpenedAsync();
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.None, null, null, null));
        _session.RunFailures["Enable-Mailbox"] = new RemoteFailure
        {
            Kind = RemoteFailureKind.Command,
            Message = "The term 'Enable-Mailbox' is not recognized as the name of a cmdlet, function, script file, or operable program."
        };

        var result = await delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.InsufficientPermissions));
            Assert.That(result.Message, Does.StartWith("Enable-Mailbox"));
        }
    }

    [Test]
    public async Task ConvergeAsync_EnableRacedBySomeoneElse_ReadsAgainAndConvergesWhatIsThereAsync()
    {
        // Someone enabled the mailbox between JIM's read and its enable: Exchange refuses the enable, and the recipient
        // that now exists is converged instead of the change being refused.
        var delivery = await OpenedAsync();
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.None, null, null, null));
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.Mailbox, "asmith", null, null));
        _session.RunFailures["Enable-Mailbox"] = new RemoteFailure { Kind = RemoteFailureKind.Command, Message = "The user is already mailbox-enabled." };

        var result = await delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox) with { Alias = "alice" }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Delivered, Is.True);
            Assert.That(result.ChangedAnything, Is.True);
            Assert.That(_session.Ran.Select(c => c.Name), Is.EqualTo(new[] { "Set-Mailbox" }));
        }
    }

    [Test]
    public async Task ConvergeAsync_EnableRefusedAndStillNoRecipient_IsTheEnablesOwnFailureAsync()
    {
        var delivery = await OpenedAsync();
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.None, null, null, null));
        _session.Reads.Enqueue(new LdapExchangeRecipient(ExchangeRecipientTypes.None, null, null, null));
        _session.RunFailures["Enable-Mailbox"] = new RemoteFailure { Kind = RemoteFailureKind.Command, Message = "Couldn't find database \"DB99\"." };

        var result = await delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox) with { Database = "DB99" }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(result.Message, Does.Contain("DB99"));
        }
    }

    [Test]
    public async Task ConvergeAsync_Cancelled_ThrowsRatherThanReportingAFailureAsync()
    {
        var delivery = await OpenedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(() => delivery.ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox), cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConvergeAsync_NoSessionOpen_IsUnreachableNotAnExceptionAsync()
    {
        var result = await Delivery().ConvergeAsync(Alice, Declared(ExchangeRecipientTypes.Mailbox), CancellationToken.None);

        Assert.That(result.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
    }

    // ---- Statistics and closing --------------------------------------------------------------------------------

    [Test]
    public async Task Statistics_AreTheSessionsAsync()
    {
        var delivery = await OpenedAsync();
        _session.Statistics = new ExchangeSessionStatistics { KerberosLogins = 1, Connections = 2, Commands = 7 };

        Assert.That(delivery.Statistics, Is.EqualTo(_session.Statistics));
    }

    [Test]
    public void Statistics_NoSessionOpen_AreNone()
    {
        Assert.That(Delivery().Statistics, Is.EqualTo(ExchangeSessionStatistics.None));
    }

    [Test]
    public async Task Close_DisposesTheSessionAndIsSafeTwiceAsync()
    {
        var delivery = await OpenedAsync();

        delivery.Close();
        delivery.Close();

        Assert.That(_session.Disposed, Is.EqualTo(1));
    }

    private LdapExchangeRecipientDelivery Delivery(Func<RemoteSessionOptions, CancellationToken, Task<ILdapExchangeRecipientSession>>? open = null) =>
        new(open ?? ((options, _) => { _opened = options; return Task.FromResult<ILdapExchangeRecipientSession>(_session); }),
            (realm, _) => { _realms.Add(realm); return Task.CompletedTask; });

    private async Task<LdapExchangeRecipientDelivery> OpenedAsync()
    {
        var delivery = Delivery();
        await delivery.OpenAsync(SignIn, Endpoint, "secret", [], CancellationToken.None);
        return delivery;
    }

    private static ExchangeDeclaredState Declared(string type) => new() { RecipientType = type };

    private sealed class FakeSession : ILdapExchangeRecipientSession
    {
        public Queue<LdapExchangeRecipient> Reads { get; } = new();
        public RemoteFailure? ReadFailure { get; set; }
        public Dictionary<string, RemoteFailure> RunFailures { get; } = [];
        public List<LdapExchangeCommand> Ran { get; } = [];
        public int ReadCount { get; private set; }
        public int Disposed { get; private set; }
        public ExchangeSessionStatistics Statistics { get; set; } = ExchangeSessionStatistics.None;

        public Task<LdapExchangeRecipient> ReadAsync(ExchangeRecipientTarget target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (ReadFailure != null)
                throw new LdapExchangeCommandException("Get-User", ReadFailure);
            return Task.FromResult(Reads.Dequeue());
        }

        public Task RunAsync(LdapExchangeCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunFailures.TryGetValue(command.Name, out var failure))
                throw new LdapExchangeCommandException(command.Name, failure);
            Ran.Add(command);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed++;
    }
}
