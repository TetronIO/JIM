// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// Registers JIM's API versioning configuration.
/// </summary>
/// <remarks>
/// This lives in its own extension method rather than inline in <c>Program.cs</c> so that the wiring can be
/// asserted by tests; <c>Program.cs</c> builds a whole application and cannot be exercised from a unit test.
/// </remarks>
public static class ApiVersioningExtensions
{
    /// <summary>
    /// Configures API versioning by URL path segment (for example <c>/api/v1/...</c>) for JIM's controllers.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddJimApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();

            // Deliberately no AssumeDefaultVersionWhenUnspecified. Every routable API controller declares
            // [ApiVersion] and every route embeds the version segment, so no request can reach a controller
            // without version metadata for a default to apply to; setting it would be dead configuration.
            // ApiVersioningConfigurationTests pins that precondition, because a controller added without an
            // [ApiVersion] would otherwise start failing at runtime rather than at build time.
        })
        // AddMvc() opts the controller pipeline into versioning explicitly. API versioning covers minimal APIs
        // on its own; controllers are routed by MVC, which the library asks you to opt in separately.
        //
        // It is a no-op today, and is here on purpose. AddApiExplorer() (from Asp.Versioning.Mvc.ApiExplorer)
        // already registers a strict superset of what AddMvc() registers, and the resulting MvcOptions filter
        // pipeline is identical with and without this call; both were verified against 10.2.1. That equivalence
        // is an undocumented implementation detail of AddApiExplorer() rather than a contract, so relying on it
        // would mean controller versioning breaking silently at runtime if the library ever stopped calling
        // AddMvc() internally. Stating it costs nothing and removes that dependency. It also satisfies the
        // AV0013 analyser rule that Asp.Versioning 10.2.0 began shipping, which pattern-matches the call chain
        // and cannot see the effective registrations.
        .AddMvc()
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        return services;
    }
}
