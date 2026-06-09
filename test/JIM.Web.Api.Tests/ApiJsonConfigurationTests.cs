// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the API JSON serialisation policy applied by <see cref="ApiJsonConfiguration"/>.
/// Duplicate JSON property names are ambiguous (the JSON spec does not define which value wins)
/// and are a known request-smuggling vector when different parsers disagree; the API must reject
/// them at the boundary rather than silently pick one.
/// </summary>
[TestFixture]
public class ApiJsonConfigurationTests
{
    [Test]
    public void Configure_DisallowsDuplicateJsonProperties()
    {
        var options = new JsonSerializerOptions();

        ApiJsonConfiguration.Configure(options);

        Assert.That(options.AllowDuplicateProperties, Is.False);
    }

    [Test]
    public void ConfiguredOptions_RejectDuplicatePropertyNames_WhenDeserialising()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        const string jsonWithDuplicateKey = "{\"Name\":\"first\",\"Name\":\"second\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DuplicateProbe>(jsonWithDuplicateKey, options));
    }

    private sealed record DuplicateProbe(string Name);
}
