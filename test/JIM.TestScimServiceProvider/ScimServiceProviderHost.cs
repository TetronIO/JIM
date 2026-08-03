// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using JIM.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace JIM.TestScimServiceProvider;

/// <summary>
/// Serves a <see cref="MockScimProvider"/> over real HTTPS, so the SCIM Connector can be driven end to
/// end against something JIM reaches the way it reaches a customer's service provider: over a socket, by
/// hostname, with a certificate to validate.
/// <para>
/// Every request is adapted into the <see cref="HttpRequestMessage"/> the provider already answers rather
/// than re-implementing SCIM here. That is the point of this host: the integration scenario and the unit
/// suite exercise the same provider, so a scenario cannot pass against a more forgiving one.
/// </para>
/// </summary>
public static class ScimServiceProviderHost
{
    /// <summary>
    /// Adds the catch-all endpoint that hands every request to the provider.
    /// </summary>
    public static void MapScimServiceProvider(this WebApplication app, MockScimProvider provider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(provider);

        app.Run(async context =>
        {
            using var request = await ToRequestMessageAsync(context.Request);
            using var response = provider.Respond(request);
            await WriteAsync(context.Response, response);

            app.Logger.LogInformation("{Method} {Path} -> {StatusCode}",
                LogSanitiser.Sanitise(context.Request.Method),
                LogSanitiser.Sanitise(context.Request.Path + context.Request.QueryString),
                (int)response.StatusCode);
        });
    }

    private static async Task<HttpRequestMessage> ToRequestMessageAsync(HttpRequest source)
    {
        var uri = new Uri($"{source.Scheme}://{source.Host}{source.Path}{source.QueryString}");
        var request = new HttpRequestMessage(new HttpMethod(source.Method), uri);

        // Only the headers the provider actually reads are carried across: authentication, so a required
        // bearer token is enforced, and the entity tag that guards a write.
        if (source.Headers.TryGetValue("Authorization", out var authorisation))
            request.Headers.TryAddWithoutValidation("Authorization", authorisation.ToArray());

        if (source.Headers.TryGetValue("If-Match", out var ifMatch))
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch.ToArray());

        if (source.ContentLength is > 0 || source.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var reader = new StreamReader(source.Body);
            var body = await reader.ReadToEndAsync();
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/scim+json");
        }

        return request;
    }

    private static async Task WriteAsync(HttpResponse target, HttpResponseMessage source)
    {
        target.StatusCode = (int)source.StatusCode;

        // The Date header carries the provider's clock, which delta import watermarks against, and the
        // entity tag guards the next write; both would be lost if only the body were copied.
        foreach (var header in source.Headers)
            target.Headers[header.Key] = header.Value.ToArray();

        if (source.Headers.Date is { } date)
            target.Headers["Date"] = date.ToString("r", CultureInfo.InvariantCulture);

        var content = await source.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(content))
        {
            // A 204 carries no body, and Kestrel rejects a Content-Type on one.
            if (source.StatusCode != HttpStatusCode.NoContent)
                target.ContentType = "application/scim+json";
            return;
        }

        target.ContentType = "application/scim+json";
        await target.WriteAsync(content);
    }
}
