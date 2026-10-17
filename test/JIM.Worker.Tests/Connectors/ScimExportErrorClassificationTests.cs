// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM;
using JIM.Models.Staging;
using JIM.Scim.Messages;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A service provider that answers a change with a client error (4xx) has refused it, which is not a fault in JIM
/// (#2118). The SCIM Connector classifies 401 and 403 as <see cref="ConnectedSystemExportErrorType.AccessDenied"/> and
/// every other client error it has no more specific classification for as
/// <see cref="ConnectedSystemExportErrorType.RejectedByConnectedSystem"/>. A server error (5xx), or no answer at all,
/// is not a refusal and stays unclassified.
/// </summary>
[TestFixture]
public class ScimExportErrorClassificationTests
{
    [TestCase(401)]
    [TestCase(403)]
    public void Classify_CredentialsRefused_IsAccessDenied(int statusCode)
    {
        Assert.That(ScimExportErrorClassifier.Classify(statusCode, null), Is.EqualTo(ConnectedSystemExportErrorType.AccessDenied));
    }

    [TestCase(400, "invalidSyntax")]
    [TestCase(400, "mutability")]
    [TestCase(400, null)]
    [TestCase(404, null)]
    [TestCase(409, "mutability")]
    [TestCase(413, "tooMany")]
    public void Classify_OtherClientError_IsRejectedByConnectedSystem(int statusCode, string? scimType)
    {
        Assert.That(ScimExportErrorClassifier.Classify(statusCode, scimType), Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
    }

    [TestCase(500)]
    [TestCase(503)]
    [TestCase(null)]
    public void Classify_ServerErrorOrNoAnswer_IsGeneral(int? statusCode)
    {
        Assert.That(ScimExportErrorClassifier.Classify(statusCode, null), Is.EqualTo(ConnectedSystemExportErrorType.General));
    }

    [Test]
    public void Classify_PreconditionFailed_KeepsItsMoreSpecificClassification()
    {
        Assert.That(ScimExportErrorClassifier.Classify(412, null), Is.EqualTo(ConnectedSystemExportErrorType.ConcurrencyConflict));
    }

    [Test]
    public void Classify_BadRequestInvalidValue_KeepsItsMoreSpecificClassification()
    {
        Assert.That(ScimExportErrorClassifier.Classify(400, "invalidValue"), Is.EqualTo(ConnectedSystemExportErrorType.MissingDependency));
    }

    [TestCase(401, "authentication")]
    [TestCase(403, "permission")]
    public void Failure_AccessDenied_NamesTheRemedyAndKeepsTheProvidersWords(int statusCode, string remedyWord)
    {
        var result = ScimExportErrorClassifier.Failure("Request failed: insufficient scope", statusCode, new ScimError { Detail = "insufficient scope" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.AccessDenied));
            Assert.That(result.ErrorMessage, Does.Contain(remedyWord).IgnoreCase);
            Assert.That(result.ErrorMessage, Does.Contain("insufficient scope"),
                "the service provider's own words are kept, so an administrator can see what it said");
        }
    }
}
