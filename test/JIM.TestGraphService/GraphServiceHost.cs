// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JIM.TestGraphService;

/// <summary>
/// Serves a <see cref="MockGraphTenant"/> over HTTPS, adapting each request into the <see cref="HttpRequestMessage"/>
/// the tenant already answers for the unit suite, rather than implementing Microsoft Graph a second time.
/// </summary>
public static class GraphServiceHost
{
    /// <summary>How often a running count of requests is logged; each request is not, as a scale run makes millions.</summary>
    public const int ProgressInterval = 10_000;

    public static void MapGraphService(this WebApplication app, MockGraphTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tenant);

        long requests = 0;
        app.Run(async context =>
        {
            using var request = await ToRequestMessageAsync(context.Request);
            using var response = tenant.Respond(request);
            await WriteAsync(context.Response, response);

            var count = Interlocked.Increment(ref requests);
            if ((int)response.StatusCode >= 400)
                app.Logger.LogInformation("{Method} {Path} -> {StatusCode}", context.Request.Method, context.Request.Path, (int)response.StatusCode);
            if (count % ProgressInterval == 0)
                app.Logger.LogInformation("{Count} requests answered; the tenant holds {Users} user(s) and {Groups} group(s).", count, tenant.Users.Count, tenant.Groups.Count);
        });
    }

    private static async Task<HttpRequestMessage> ToRequestMessageAsync(HttpRequest source)
    {
        var request = new HttpRequestMessage(new HttpMethod(source.Method), new Uri($"{source.Scheme}://{source.Host}{source.Path}{source.QueryString}"));
        if (source.ContentLength is > 0 || source.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var reader = new StreamReader(source.Body);
            request.Content = new StringContent(await reader.ReadToEndAsync(), Encoding.UTF8, source.ContentType?.Split(';')[0] ?? "application/json");
        }

        return request;
    }

    private static async Task WriteAsync(HttpResponse target, HttpResponseMessage source)
    {
        target.StatusCode = (int)source.StatusCode;
        var content = await source.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(content))
            return;

        target.ContentType = "application/json";
        await target.WriteAsync(content);
    }
}
