// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Scim.Serialisation;
using NUnit.Framework;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Covers the RFC 7644 section 3.12 error response, which both sides of JIM's SCIM support use:
/// the client connector parses it from service providers, and JIM's own service provider (#124)
/// emits it. Provider deviations are tolerated on read; what JIM writes stays strictly conformant.
/// </summary>
[TestFixture]
public class ScimErrorTests
{
    [Test]
    public void Deserialise_ConformantError_ParsesStatusScimTypeAndDetail()
    {
        const string json = """
        {
          "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
          "status": "400",
          "scimType": "invalidValue",
          "detail": "Attribute 'userName' is required."
        }
        """;

        var error = JsonSerializer.Deserialize<ScimError>(json, ScimJson.Options);

        Assert.That(error, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(error!.Status, Is.EqualTo("400"));
            Assert.That(error.StatusCode, Is.EqualTo(400));
            Assert.That(error.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
            Assert.That(error.Detail, Is.EqualTo("Attribute 'userName' is required."));
            Assert.That(error.Schemas, Does.Contain(ScimUrns.Error));
        });
    }

    [Test]
    public void Deserialise_StatusAsJsonNumber_StillParses()
    {
        // RFC 7644 requires status as a JSON string, but real providers emit a bare number.
        // A client that throws here would turn a readable provider error into an opaque parse failure.
        const string json = """
        {
          "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
          "status": 429,
          "detail": "Too many requests."
        }
        """;

        var error = JsonSerializer.Deserialize<ScimError>(json, ScimJson.Options);

        Assert.That(error, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(error!.Status, Is.EqualTo("429"));
            Assert.That(error.StatusCode, Is.EqualTo(429));
            Assert.That(error.ScimType, Is.Null);
        });
    }

    [Test]
    public void Deserialise_MixedCaseAttributeNames_StillParses()
    {
        // RFC 7643 section 2.1: SCIM attribute names are case insensitive.
        const string json = """
        {
          "Schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
          "Status": "409",
          "ScimType": "uniqueness",
          "Detail": "userName already exists."
        }
        """;

        var error = JsonSerializer.Deserialize<ScimError>(json, ScimJson.Options);

        Assert.That(error, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(error!.StatusCode, Is.EqualTo(409));
            Assert.That(error.ScimType, Is.EqualTo(ScimErrorTypes.Uniqueness));
        });
    }

    [Test]
    public void Deserialise_NonNumericStatus_YieldsNullStatusCodeWithoutThrowing()
    {
        const string json = """
        { "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"], "status": "unavailable" }
        """;

        var error = JsonSerializer.Deserialize<ScimError>(json, ScimJson.Options);

        Assert.That(error, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(error!.Status, Is.EqualTo("unavailable"));
            Assert.That(error.StatusCode, Is.Null);
        });
    }

    [Test]
    public void Serialise_Error_EmitsSchemaUrnAndStatusAsStringOmittingNulls()
    {
        var error = new ScimError { Status = "404", Detail = "Resource not found." };

        var json = JsonSerializer.Serialize(error, ScimJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"schemas\":[\"urn:ietf:params:scim:api:messages:2.0:Error\"]"));
            Assert.That(json, Does.Contain("\"status\":\"404\""), "status must be emitted as a JSON string per RFC 7644.");
            Assert.That(json, Does.Not.Contain("scimType"), "null optional members must be omitted, not emitted as null.");
        });
    }

    [Test]
    public void ForStatus_BuildsErrorCarryingTheSchemaUrn()
    {
        var error = ScimError.ForStatus(500, "Internal failure.");

        Assert.Multiple(() =>
        {
            Assert.That(error.Status, Is.EqualTo("500"));
            Assert.That(error.StatusCode, Is.EqualTo(500));
            Assert.That(error.Detail, Is.EqualTo("Internal failure."));
            Assert.That(error.Schemas, Is.EqualTo(new[] { ScimUrns.Error }));
        });
    }
}
