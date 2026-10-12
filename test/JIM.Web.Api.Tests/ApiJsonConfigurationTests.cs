// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using JIM.Web;
using JIM.Web.Models.Api;
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

    [Test]
    public void ConfiguredOptions_RejectOutOfRangeIntegerEnumValues_WhenDeserialising()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        // An integer that maps to no defined enum member. With the default JsonStringEnumConverter
        // this would bind silently to an undefined enum value; the API must reject it at the boundary.
        const string jsonWithOutOfRangeEnum = "{\"Mode\":99}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnumProbe>(jsonWithOutOfRangeEnum, options));
    }

    [Test]
    public void ConfiguredOptions_RejectIntegerEnumValues_WhenDeserialising()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        // Even an in-range integer is rejected: the API contract is string enum values only, so that
        // a client can never depend on the numeric ordinal (which is free to change between releases).
        const string jsonWithIntegerEnum = "{\"Mode\":1}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnumProbe>(jsonWithIntegerEnum, options));
    }

    [Test]
    public void ConfiguredOptions_AcceptStringEnumValues_WhenDeserialising()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        // String enum values remain the supported wire format and must still bind.
        const string jsonWithStringEnum = "{\"Mode\":\"Second\"}";

        var result = JsonSerializer.Deserialize<EnumProbe>(jsonWithStringEnum, options);

        Assert.That(result!.Mode, Is.EqualTo(ProbeEnum.Second));
    }

    [Test]
    public void ConfiguredOptions_AcceptExponentNotationForDecimal_WhenDeserialising()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        // System.Text.Json accepts exponent notation for decimal-typed properties and
        // canonicalises it; a client sending 1.5E3 binds as 1500.
        const string jsonWithExponent = "{\"DecimalValue\":1.5E3}";

        var result = JsonSerializer.Deserialize<PredefinedSearchCriterionRequest>(jsonWithExponent, options);

        Assert.That(result!.DecimalValue, Is.EqualTo(1500m));
    }

    [Test]
    public void ConfiguredOptions_SerialiseDecimalAsPlainJsonNumber_WhenSerialising()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        var request = new PredefinedSearchCriterionRequest
        {
            MetaverseAttributeId = 1,
            ComparisonType = "GreaterThan",
            DecimalValue = 0.1m
        };

        var json = JsonSerializer.Serialize(request, options);

        // Decimal must serialise as a plain, unquoted JSON number with no exponent notation
        // (System.Decimal.ToString never emits exponent form, unlike double).
        using var document = JsonDocument.Parse(json);
        var decimalProperty = document.RootElement.GetProperty("DecimalValue");
        Assert.That(decimalProperty.ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(decimalProperty.GetRawText(), Is.EqualTo("0.1"));
    }

    [Test]
    public void ConfiguredOptions_RejectDecimalRangeOverflow_WhenDeserialising()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        // 8E28 exceeds decimal.MaxValue (~7.92E28); deserialisation must fail rather than
        // silently truncate or round the value.
        const string jsonWithOverflow = "{\"DecimalValue\":8E28}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PredefinedSearchCriterionRequest>(jsonWithOverflow, options));
    }

    private sealed record DuplicateProbe(string Name);

    [Test]
    public void ConfigureHttp_DescribesEnumsByTheirNames()
    {
        // The OpenAPI document generator describes the API's types from the HTTP JSON options, not MVC's. Without
        // the policy there, the document declared every enum an integer while the API sends and accepts only names.
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions();

        ApiJsonConfiguration.ConfigureHttp(options);

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(options.SerializerOptions, typeof(ProbeEnum));
        Assert.That(schema["type"]?.GetValue<string>(), Is.Not.EqualTo("integer"));
        Assert.That(schema["enum"]?.AsArray().Select(value => value!.GetValue<string>()), Is.EqualTo(new[] { "First", "Second" }));
    }

    [Test]
    public void ConfigureHttp_DisallowsDuplicateJsonProperties()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions();

        ApiJsonConfiguration.ConfigureHttp(options);

        Assert.That(options.SerializerOptions.AllowDuplicateProperties, Is.False);
    }

    [Test]
    public void ConfigureHttp_RaisesMaxDepthForTheSchemaGenerator()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions();

        ApiJsonConfiguration.ConfigureHttp(options);

        Assert.That(options.SerializerOptions.MaxDepth, Is.EqualTo(256));
    }

    private enum ProbeEnum
    {
        First = 0,
        Second = 1
    }

    private sealed record EnumProbe(ProbeEnum Mode);
}
