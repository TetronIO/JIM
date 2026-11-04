// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation;
using Tetron.PowerShellRemoting.Tests.Kerberos;

namespace Tetron.PowerShellRemoting.Tests;

/// <summary>
/// Payloads of several sizes, there and back through one bound parameter: under and over 16 KB, where WinRM's
/// encryption is known to split messages for some protocols, and up to several megabytes, which PowerShell remoting
/// has to fragment across many WS-Management messages in each direction, each within the size Windows accepts. An
/// Exchange recipient at full serialisation is tens of kilobytes, so this is everyday traffic, not an edge case.
/// Skipped without the harness.
/// </summary>
/// <remarks>
/// The harness proves our framing against pyspnego; whether Windows itself ever splits a Kerberos-sealed message is for
/// the lab's run of <see cref="ExchangeLabTests"/>.
/// </remarks>
[Category("RequiresRemotingHarness")]
public class LargePayloadHarnessTests
{
    private const int SixteenKilobytes = 16 * 1024;

    // Windows' MaxEnvelopeSizekb, 500 by default: the largest request it accepts. The stand-in server enforces it too.
    private const int WindowsRequestLimit = 500 * 1024;

    [SetUp]
    public void SetUp()
    {
        KerberosHarness.RequireHarness();
        if (string.IsNullOrWhiteSpace(KerberosHarness.WinRM))
            KerberosHarness.Unavailable("Needs JIM_REMOTING_HARNESS_WINRM.");
    }

    [TestCase(1024)]
    [TestCase(SixteenKilobytes - 1)]
    [TestCase(SixteenKilobytes)]
    [TestCase(SixteenKilobytes + 1)]
    [TestCase(64 * 1024)]
    [TestCase(1024 * 1024)]
    [TestCase(5 * 1024 * 1024)]
    public async Task InvokeAsync_PayloadOfThisSize_ArrivesBackExactlyAsync(int characters)
    {
        var payload = Payload(characters);
        using var runspace = await RemoteSessionFactory.OpenAsync(KerberosHarness.SessionOptions());
        using var ps = PowerShell.Create(runspace);

        ps.AddCommand("Write-Output").AddParameter("InputObject", payload);
        var output = await ps.InvokeAsync();

        var statistics = RemoteSessionFactory.GetStatistics(runspace);
        TestContext.Out.WriteLine($"{characters} characters: largest request {statistics.LargestRequestBytes} bytes, largest response {statistics.LargestResponseBytes} bytes.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ps.HadErrors, Is.False);
            Assert.That(output.Single().BaseObject as string, Is.EqualTo(payload), "byte for byte, in order");
            Assert.That(statistics.LargestRequestBytes, Is.LessThanOrEqualTo(WindowsRequestLimit),
                "no request is larger than Windows accepts, however large the parameter");
            if (characters > SixteenKilobytes)
            {
                Assert.That(statistics.LargestRequestBytes, Is.GreaterThan(SixteenKilobytes), "the request really crossed 16 KB");
                Assert.That(statistics.LargestResponseBytes, Is.GreaterThan(SixteenKilobytes), "the response really crossed 16 KB");
            }
        }
    }

    /// <summary>
    /// Text that changes all the way along, so a fragment dropped, repeated or put back in the wrong place changes it.
    /// </summary>
    internal static string Payload(int characters) =>
        string.Create(characters, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = (char)('a' + ((i * 7) + (i / 26)) % 26);
        });
}
