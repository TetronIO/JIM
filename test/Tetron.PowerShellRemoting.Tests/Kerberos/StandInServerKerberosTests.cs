// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Tetron.PowerShellRemoting.Encryption;
using Tetron.PowerShellRemoting.Kerberos;

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

/// <summary>
/// Kerberos authentication and WinRM message encryption against the harness's stand-in WinRM server, whose side is
/// pyspnego: an implementation independent of this library. Skipped without the harness.
/// </summary>
[Category("RequiresRemotingHarness")]
public class StandInServerKerberosTests
{
    [SetUp]
    public void SetUp()
    {
        KerberosHarness.RequireHarness();
        if (string.IsNullOrWhiteSpace(KerberosHarness.WinRM))
            Assert.Ignore("Needs JIM_REMOTING_HARNESS_WINRM.");
    }

    [Test]
    public async Task EncryptedRequest_AfterKerberosAuthentication_GetsAnEncryptedReplyWeCanReadAsync()
    {
        using var context = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, KerberosHarness.ServiceHost);
        using var http = OneConnectionClient();
        await AuthenticateAsync(http, context);

        var request = Encoding.UTF8.GetBytes(
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\">" +
            "<s:Header><a:Action>urn:tetron:not-an-action</a:Action><a:MessageID>uuid:00000000-0000-0000-0000-000000000001</a:MessageID></s:Header>" +
            "<s:Body/></s:Envelope>");
        var encoded = WinRMEncryptedMessage.Encode(request, context);
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(encoded.Body) };
        message.Content.Headers.TryAddWithoutValidation("Content-Type", encoded.ContentType);

        using var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsByteArrayAsync();
        var reply = Encoding.UTF8.GetString(WinRMEncryptedMessage.Decode(body, response.Content.Headers.ContentType!.ToString(), context));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply, Does.Contain("ActionNotSupported"));
            Assert.That(reply, Does.Contain("uuid:00000000-0000-0000-0000-000000000001"), "The reply relates to our request.");
        }
    }

    [Test]
    public async Task UnencryptedRequest_AfterKerberosAuthentication_IsRefusedAsync()
    {
        using var context = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, KerberosHarness.ServiceHost);
        using var http = OneConnectionClient();
        await AuthenticateAsync(http, context);

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent("<s:Envelope/>", Encoding.UTF8) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "UTF-8" };
        using var response = await http.SendAsync(message);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static Uri Endpoint => new($"http://{KerberosHarness.WinRM}:5985/wsman");

    // Kerberos authentication on WinRM is per connection, so these tests must keep to one.
    private static HttpClient OneConnectionClient() =>
        new(new SocketsHttpHandler { MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

    private static async Task AuthenticateAsync(HttpClient http, KerberosClientContext context)
    {
        var token = context.Step(ReadOnlySpan<byte>.Empty);
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent([]) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Kerberos", Convert.ToBase64String(token!));
        using var response = await http.SendAsync(message);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "The server did not accept the Kerberos token.");

        var reply = response.Headers.WwwAuthenticate.Single(h => h.Scheme == "Kerberos").Parameter;
        context.Step(Convert.FromBase64String(reply!));
        Assert.That(context.IsEstablished, Is.True, "Mutual authentication did not complete.");
    }
}
