// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using JIM.Web.Extensions.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Guards JIM's API versioning configuration and the precondition that justifies it.
/// <para>
/// JIM versions by URL path segment and deliberately does not set
/// <see cref="ApiVersioningOptions.AssumeDefaultVersionWhenUnspecified"/>, because every routable API controller
/// declares its own <c>[ApiVersion]</c>. That makes the setting dead configuration; it also makes the absence of
/// an <c>[ApiVersion]</c> on a newly added controller a runtime failure rather than a build failure, which is
/// what these tests exist to prevent.
/// </para>
/// </summary>
[TestFixture]
public class ApiVersioningConfigurationTests
{
    [Test]
    public void EveryRoutableApiController_DeclaresAnApiVersion()
    {
        var undeclared = RoutableApiControllers()
            .Where(controller => !controller.GetCustomAttributes<ApiVersionAttribute>(inherit: true).Any())
            .Select(controller => controller.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.That(undeclared, Is.Empty,
            "Every routable API controller must declare [ApiVersion], because JIM does not set " +
            "AssumeDefaultVersionWhenUnspecified: there is no default to fall back on, so an undeclared " +
            "controller fails to match its route at runtime while the build stays clean.");
    }

    [Test]
    public void AddJimApiVersioning_DoesNotAssumeADefaultVersion()
    {
        var options = BuildVersioningOptions();

        Assert.That(options.AssumeDefaultVersionWhenUnspecified, Is.False,
            "Assuming a default version is only meaningful for endpoints that carry no version metadata. " +
            "EveryRoutableApiController_DeclaresAnApiVersion asserts that no such endpoint exists, so enabling " +
            "this would be dead configuration.");
    }

    [Test]
    public void AddJimApiVersioning_VersionsByUrlSegment_AndReportsVersions()
    {
        var options = BuildVersioningOptions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.ApiVersionReader, Is.TypeOf<UrlSegmentApiVersionReader>(),
                "Routes are templated 'api/v{version:apiVersion}/[controller]', so the version is read from the " +
                "URL segment rather than a header or query string.");
            Assert.That(options.DefaultApiVersion, Is.EqualTo(new ApiVersion(1, 0)));
            Assert.That(options.ReportApiVersions, Is.True);
        }
    }

    /// <summary>
    /// Pins that the controller pipeline is opted into versioning at all, whichever call provides it.
    /// <para>
    /// Note what this does <b>not</b> catch: dropping <c>AddMvc()</c> alone leaves these registrations in place,
    /// because <c>AddApiExplorer()</c> registers a superset of them. The guard against that is the AV0013
    /// analyser, which fails the build. This test covers the wider regression of the versioning block being
    /// rewired or removed, which no analyser would notice.
    /// </para>
    /// </summary>
    [Test]
    public void AddJimApiVersioning_OptsTheControllerPipelineIntoVersioning()
    {
        var services = BuildServices();

        var mvcVersioningServices = services
            .Where(descriptor => (descriptor.ImplementationType ?? descriptor.ServiceType)
                .Assembly.GetName().Name == "Asp.Versioning.Mvc")
            .ToList();

        Assert.That(mvcVersioningServices, Is.Not.Empty,
            "Controllers are discovered and routed by MVC, which has to be opted into API versioning for the " +
            "[ApiVersion] metadata on a controller to reach the actions it declares.");
    }

    /// <summary>
    /// The API explorer is what groups the versioned controllers into the OpenAPI document, and
    /// <c>SubstituteApiVersionInUrl</c> is what turns the <c>{version}</c> route token into a literal <c>v1</c>
    /// in that document. Without it every documented path reads <c>/api/v{version}/...</c>.
    /// </summary>
    [Test]
    public void AddJimApiVersioning_SubstitutesTheVersionIntoDocumentedUrls()
    {
        var provider = BuildServices().BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ApiExplorerOptions>>().Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.SubstituteApiVersionInUrl, Is.True);
            Assert.That(options.GroupNameFormat, Is.EqualTo("'v'VVV"));
        }
    }

    /// <summary>
    /// Builds the real API description collection and asserts every endpoint resolves under <c>api/v1/</c>.
    /// <para>
    /// This is the closest thing to booting the application that a unit test can reach, and it is here because
    /// the versioning wiring's failure modes are runtime ones. Enumerating the descriptions forces every
    /// controller's route template to be parsed, so a duplicate route parameter (the trap
    /// <see cref="ApiRouteTemplateTests"/> guards statically) fails here too, and it forces the API explorer to
    /// substitute the <c>{version}</c> token. A regression in the versioning registration shows up as templates
    /// that keep the literal token, which is what the published OpenAPI document would then advertise.
    /// </para>
    /// </summary>
    [Test]
    public void EveryApiEndpoint_ResolvesUnderTheVersionedUrl()
    {
        var services = BuildServices();
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());
        services.AddControllers()
            .AddApplicationPart(typeof(JIM.Web.Controllers.Api.SynchronisationController).Assembly);

        var descriptions = services.BuildServiceProvider()
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>();

        var paths = descriptions.ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Select(description => description.RelativePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct()
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths, Is.Not.Empty,
                "No endpoints were discovered at all, which means this test is asserting nothing.");
            Assert.That(paths.Where(path => path.Contains("{version}", StringComparison.Ordinal)), Is.Empty,
                "SubstituteApiVersionInUrl should replace the {version} route token with the literal version. " +
                "A path that keeps the token is what the OpenAPI document would advertise to clients.");
            Assert.That(paths.Where(path => !path.StartsWith("api/v1/", StringComparison.Ordinal)), Is.Empty,
                "Every API endpoint is expected to resolve under the versioned URL prefix.");
        }
    }

    private static ApiVersioningOptions BuildVersioningOptions() =>
        BuildServices().BuildServiceProvider().GetRequiredService<IOptions<ApiVersioningOptions>>().Value;

    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        services.AddJimApiVersioning();
        return services;
    }

    /// <summary>
    /// Every concrete controller that ASP.NET will route. Abstract base classes are excluded: they declare no
    /// routes of their own, and their derived types carry the attributes.
    /// </summary>
    private static IEnumerable<Type> RoutableApiControllers() =>
        typeof(JIM.Web.Controllers.Api.SynchronisationController).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(type));

    /// <summary>
    /// The API explorer's endpoint metadata provider requires an <see cref="IHostEnvironment"/>. Nothing here
    /// reads from it; it only has to exist for the description collection to be built outside a host.
    /// </summary>
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "JIM.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
