// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's Connection Timeout bounds the network connection itself (#2003).
/// <para>
/// The platform LDAP client on Linux applies the timeout to operations but not to its TCP connect, so against a
/// directory server behind a firewall that silently drops packets, a bind waited out the operating system's SYN
/// retries: 134 seconds, measured, with Connection Timeout set to 5. Every connection the connector opens is built in
/// one place, which now checks first that the server accepts a TCP connection within the Connection Timeout.
/// </para>
/// <para>
/// 10.255.255.1 stands in for a black-holed directory server. On a network that drops traffic to it the connect hangs;
/// on one that refuses it as unreachable the connect fails at once. Either way it must fail, and inside the time the
/// administrator allowed.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorConnectionTimeoutTests
{
    private const string BlackHoledAddress = "10.255.255.1";

    private CapturingSink _log = null!;

    private ILogger Logger() => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();

    [SetUp]
    public void SetUp() => _log = new CapturingSink();

    // ---- Through the connector ----

    [Test]
    public void OpenImportConnection_ServerDropsTheConnection_FailsWithinTheConnectionTimeout()
    {
        using var connector = new LdapConnector();
        var settingValues = SettingValues(BlackHoledAddress, port: 389, connectionTimeoutSeconds: 2);
        var stopwatch = Stopwatch.StartNew();

        var thrown = Assert.Throws<LdapException>(() => connector.OpenImportConnection(settingValues, null, Logger()));

        stopwatch.Stop();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)),
                "a dropped connection must give up after the Connection Timeout, not the operating system's TCP retries");
            Assert.That(thrown!.ErrorCode, Is.EqualTo(81), "server down, so the connector's retry policy treats it as transient, as before");
            Assert.That(thrown!.Message, Does.Contain(BlackHoledAddress), "the failure must name the server that did not answer");
        }
    }

    [Test]
    public void OpenImportConnection_PortRefused_FailsWithoutWaitingOutTheConnectionTimeout()
    {
        using var connector = new LdapConnector();
        var settingValues = SettingValues("127.0.0.1", UnusedLoopbackPort(), connectionTimeoutSeconds: 30);
        var stopwatch = Stopwatch.StartNew();

        Assert.That(() => connector.OpenImportConnection(settingValues, null, Logger()), Throws.InstanceOf<LdapException>());

        stopwatch.Stop();
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "a refusal is an answer; there is nothing to wait for");
    }

    // ---- The check itself ----

    [Test]
    public void EnsureAcceptsConnections_Listening_Returns()
    {
        using var listener = Listen(out var port);

        Assert.That(() => LdapConnectorUtilities.EnsureAcceptsConnections("127.0.0.1", port, TimeSpan.FromSeconds(5), Logger()), Throws.Nothing);
    }

    [Test]
    public void EnsureAcceptsConnections_PortRefused_ThrowsServerDown()
    {
        var port = UnusedLoopbackPort();

        var thrown = Assert.Throws<LdapException>(() =>
            LdapConnectorUtilities.EnsureAcceptsConnections("127.0.0.1", port, TimeSpan.FromSeconds(5), Logger()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.ErrorCode, Is.EqualTo(81));
            Assert.That(thrown!.Message, Does.Contain("127.0.0.1"));
        }
    }

    [Test]
    public void EnsureAcceptsConnections_ServerDoesNotAnswer_ThrowsServerDownWithinTheTimeout()
    {
        var stopwatch = Stopwatch.StartNew();

        var thrown = Assert.Throws<LdapException>(() =>
            LdapConnectorUtilities.EnsureAcceptsConnections(BlackHoledAddress, 389, TimeSpan.FromSeconds(1), Logger()));

        stopwatch.Stop();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.ErrorCode, Is.EqualTo(81));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        }
    }

    /// <summary>
    /// A domain name commonly resolves to every domain controller in the domain, and a firewall between JIM and a
    /// remote site can drop traffic to some of them. The LDAP client tries each address in turn and still connects
    /// once it reaches one that answers, so the check must too: the timeout bounds each address, not the name, or a
    /// name that works today would start failing.
    /// </summary>
    [Test]
    public void EnsureAcceptsConnections_AnEarlierAddressDoesNotAnswerAndALaterOneAccepts_ReturnsAndWarnsAboutTheOneThatDidNotAnswer()
    {
        using var listener = Listen(out var port);
        var addresses = new[] { IPAddress.Parse(BlackHoledAddress), IPAddress.Loopback };

        Assert.That(() => LdapConnectorUtilities.EnsureAcceptsConnections("dc.corp.local", addresses, port, TimeSpan.FromSeconds(1), Logger()),
            Throws.Nothing);

        // Where the black-holed address is refused as unreachable rather than dropped, nothing waited, so there is
        // nothing to warn about; only a silent drop costs the LDAP client its long wait.
        if (_log.Events.Count(e => e.Level == LogEventLevel.Warning) == 0)
            Assert.Ignore("This network refuses the black-holed address outright, so the dropped-address warning cannot be exercised here.");

        var warning = _log.Events.Single(e => e.Level == LogEventLevel.Warning);
        var rendered = warning.RenderMessage();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rendered, Does.Contain("dc.corp.local"));
            Assert.That(rendered, Does.Contain(BlackHoledAddress), "the warning must name the address that did not answer");
        }
    }

    [Test]
    public void EnsureAcceptsConnections_NoAddressAccepts_ThrowsServerDownNamingEachAddress()
    {
        var port = UnusedLoopbackPort();
        var addresses = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") };

        var thrown = Assert.Throws<LdapException>(() =>
            LdapConnectorUtilities.EnsureAcceptsConnections("dc.corp.local", addresses, port, TimeSpan.FromSeconds(5), Logger()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.ErrorCode, Is.EqualTo(81));
            Assert.That(thrown!.Message, Does.Contain("dc.corp.local"));
            Assert.That(thrown!.Message, Does.Contain("127.0.0.1"));
            Assert.That(thrown!.Message, Does.Contain("127.0.0.2"));
        }
    }

    [Test]
    public void EnsureAcceptsConnections_NoAddresses_ThrowsServerDown()
    {
        var thrown = Assert.Throws<LdapException>(() =>
            LdapConnectorUtilities.EnsureAcceptsConnections("dc.corp.local", [], 389, TimeSpan.FromSeconds(5), Logger()));

        Assert.That(thrown!.ErrorCode, Is.EqualTo(81));
    }

    // ---- Helpers ----

    private static List<ConnectedSystemSettingValue> SettingValues(string host, int port, int connectionTimeoutSeconds) =>
    [
        NewSetting("Host", stringValue: host),
        NewSetting("Port", intValue: port),
        NewSetting("Connection Timeout", intValue: connectionTimeoutSeconds),
        NewSetting("Username", stringValue: "cn=admin,dc=example,dc=org"),
        NewSetting("Password", encryptedValue: "adminpassword"),
        NewSetting("Authentication Type", stringValue: "Simple"),
        NewSetting("Maximum Retries", intValue: 0)
    ];

    private static ConnectedSystemSettingValue NewSetting(string name, string? stringValue = null, string? encryptedValue = null, int? intValue = null) => new()
    {
        Setting = new ConnectorDefinitionSetting { Name = name },
        StringValue = stringValue,
        StringEncryptedValue = encryptedValue,
        IntValue = intValue
    };

    /// <summary>
    /// A loopback port with nothing listening on it: bound, read, and released.
    /// </summary>
    private static int UnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
