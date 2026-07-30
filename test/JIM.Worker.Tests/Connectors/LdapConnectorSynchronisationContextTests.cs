// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The connector API is synchronous, but reading the JIM certificate store is not, so opening an LDAPS connection has
/// to block on an asynchronous call. These tests pin down that it blocks safely: the portal validates Connected System
/// settings on Blazor's renderer synchronisation context, which runs one callback at a time, and an asynchronous call
/// whose continuation is posted back to that context can never finish while the call that is waiting for it holds it.
/// </summary>
/// <remarks>
/// Nothing here needs a directory server. The connection is aimed at a port with nothing listening, because the
/// failure being guarded against happens before the connection is attempted; what matters is that the call returns
/// at all.
/// </remarks>
[TestFixture]
public class LdapConnectorSynchronisationContextTests
{
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        _logger.Dispose();
    }

    [Test]
    public void OpenImportConnection_OnASingleThreadedSynchronisationContext_DoesNotDeadlock()
    {
        var completed = new ManualResetEventSlim(false);

        RunOnSingleThreadedContext(() =>
        {
            using var connector = new LdapConnector();
            connector.SetCertificateProvider(new YieldingCertificateProvider());

            try
            {
                // Expected to fail: nothing is listening. The assertion is about returning, not about succeeding.
                connector.OpenImportConnection(BuildSettingValues(), _logger);
            }
            catch (Exception)
            {
                // Any connection failure is fine here.
            }
            finally
            {
                completed.Set();
            }
        });

        Assert.That(completed.Wait(TimeSpan.FromSeconds(30)), Is.True,
            "Opening an LDAPS connection never returned on a single-threaded synchronisation context. Reading the " +
            "JIM certificate store must not be awaited on the caller's context, or the portal deadlocks when an " +
            "administrator saves Connected System settings.");
    }

    /// <summary>
    /// Runs the supplied work on a dedicated thread whose synchronisation context posts continuations back to that
    /// same thread, one at a time, the way Blazor's renderer does.
    /// </summary>
    private static void RunOnSingleThreadedContext(Action work)
    {
        var context = new SingleThreadedSynchronisationContext();

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            context.Post(_ =>
            {
                try
                {
                    work();
                }
                finally
                {
                    context.Complete();
                }
            }, null);
            context.Run();
        })
        {
            IsBackground = true,
            Name = "single-threaded-synchronisation-context"
        };

        thread.Start();
    }

    private static List<ConnectedSystemSettingValue> BuildSettingValues()
    {
        return
        [
            // Port 1 on the loopback interface: refused immediately rather than waiting out a timeout.
            NewSetting("Host", stringValue: "127.0.0.1"),
            NewSetting("Port", intValue: 1),
            NewSetting("Use Secure Connection (LDAPS)?", checkboxValue: true),
            NewSetting("Connection Timeout", intValue: 2),
            NewSetting("Username", stringValue: "cn=admin,dc=example,dc=org"),
            NewSetting("Password", encryptedValue: "adminpassword"),
            NewSetting("Authentication Type", stringValue: "Simple"),
            NewSetting("Maximum Retries", intValue: 0)
        ];
    }

    private static ConnectedSystemSettingValue NewSetting(string name, string? stringValue = null, string? encryptedValue = null, int? intValue = null, bool checkboxValue = false)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            StringEncryptedValue = encryptedValue,
            IntValue = intValue,
            CheckboxValue = checkboxValue
        };
    }

    /// <summary>
    /// Stands in for the JIM certificate store, completing asynchronously the way a database query does. A provider
    /// that completes synchronously cannot reproduce the deadlock, because there is no continuation to post.
    /// </summary>
    private sealed class YieldingCertificateProvider : ICertificateProvider
    {
        public async Task<List<X509Certificate2>> GetTrustedCertificatesAsync()
        {
            await Task.Yield();
            return [];
        }
    }

    /// <summary>
    /// A synchronisation context that runs every posted callback on one thread, in order, like Blazor's renderer.
    /// </summary>
    private sealed class SingleThreadedSynchronisationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        internal void Run()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }

        internal void Complete() => _queue.CompleteAdding();
    }
}
