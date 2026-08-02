// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Guards the shape of every API route template.
/// <para>
/// A duplicate route parameter name is rejected by ASP.NET when the route table is built, which is at
/// <b>startup</b>: the app throws before it serves anything, taking the OpenAPI generation stage of the Docker
/// build with it. Nothing before that catches it. <c>dotnet build</c> is clean, and unit tests that call action
/// methods directly bypass routing entirely, so they pass too.
/// </para>
/// <para>
/// The trap is specific and easy to walk into: every controller is routed
/// <c>api/v{version:apiVersion}/[controller]</c>, so <c>version</c> is already a parameter on every single
/// action. An action template of <c>change-history/{version:int}</c> reads perfectly well on its own and brings
/// the app down. This asserts the property directly rather than relying on somebody remembering to boot the app
/// after adding a route.
/// </para>
/// </summary>
[TestFixture]
public class ApiRouteTemplateTests
{
    /// <summary>
    /// Matches a route parameter and captures its name, stopping at the constraint separator so that
    /// <c>{id:int}</c> and <c>{id}</c> are recognised as the same parameter.
    /// </summary>
    private static readonly Regex RouteParameter = new(@"\{\*?(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    [Test]
    public void EveryApiRoute_HasUniquelyNamedParameters()
    {
        var offences = new List<string>();

        foreach (var controller in ApiControllers())
        {
            var controllerTemplates = TemplatesOf(controller.GetCustomAttributes<RouteAttribute>());
            if (controllerTemplates.Count == 0)
                controllerTemplates.Add(string.Empty);

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var actionTemplates = TemplatesOf(action.GetCustomAttributes<HttpMethodAttribute>());
                if (actionTemplates.Count == 0)
                    continue;

                // An action template starting with "/" or "~/" replaces the controller's rather than combining
                // with it, which is exactly how ASP.NET resolves them.
                offences.AddRange(
                    from controllerTemplate in controllerTemplates
                    from actionTemplate in actionTemplates
                    let combined = actionTemplate.StartsWith('/') || actionTemplate.StartsWith("~/")
                        ? actionTemplate
                        : $"{controllerTemplate}/{actionTemplate}"
                    let duplicates = DuplicateParameterNames(combined)
                    where duplicates.Count > 0
                    select $"{controller.Name}.{action.Name}: '{combined}' repeats {string.Join(", ", duplicates)}");
            }
        }

        Assert.That(offences, Is.Empty,
            "A repeated route parameter name is rejected when the route table is built, so the application " +
            "fails to start rather than failing to compile. Rename the action's parameter (note that " +
            "'version' is already taken by every controller's own route).");
    }

    /// <summary>
    /// A canary for the guard itself: the shape it looks for really is detected. Without this, a regex that
    /// silently stopped matching would leave the test above passing for ever.
    /// </summary>
    [Test]
    public void TheGuard_DetectsARepeatedParameterName()
    {
        Assert.That(DuplicateParameterNames("api/v{version:apiVersion}/sync/change-history/{version:int}"),
            Does.Contain("version"));
        Assert.That(DuplicateParameterNames("api/v{version:apiVersion}/sync/sync-rules/{id:int}/initial-password"),
            Is.Empty);
    }

    private static IEnumerable<Type> ApiControllers() =>
        typeof(JIM.Web.Controllers.Api.SynchronisationController).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(t));

    private static List<string> TemplatesOf(IEnumerable<IRouteTemplateProvider> providers) =>
        providers.Select(p => p.Template).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();

    private static List<string> DuplicateParameterNames(string template) =>
        RouteParameter.Matches(template)
            .Select(m => m.Groups["name"].Value)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
}
