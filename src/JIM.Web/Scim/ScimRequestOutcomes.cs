// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Scim;

/// <summary>
/// How a SCIM response counts towards the summary Activity (D17).
/// </summary>
public static class ScimRequestOutcomes
{
    /// <summary>
    /// The <c>HttpContext.Items</c> key a SCIM error response leaves its error type (<c>scimType</c>) under, so the
    /// rejection can be named without reading the response body.
    /// </summary>
    public const string ErrorTypeItem = "JIM.Scim.ErrorType";

    /// <summary>
    /// What a response with this method and status did: a create (<c>201</c>), an accepted replace or patch, a delete,
    /// or a rejection (any 4xx but <c>401</c> and <c>403</c>, which the security audit and the pipeline account for).
    /// Reads, searches and server errors are not counted.
    /// </summary>
    public static ScimRequestOutcome Classify(string method, int statusCode)
    {
        if (statusCode is >= 400 and < 500)
            return statusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden
                ? ScimRequestOutcome.NotCounted
                : ScimRequestOutcome.Rejected;

        if (statusCode is < 200 or >= 300)
            return ScimRequestOutcome.NotCounted;

        if (HttpMethods.IsPost(method))
            return statusCode == StatusCodes.Status201Created ? ScimRequestOutcome.Created : ScimRequestOutcome.NotCounted;
        if (HttpMethods.IsPut(method) || HttpMethods.IsPatch(method))
            return ScimRequestOutcome.Updated;
        return HttpMethods.IsDelete(method) ? ScimRequestOutcome.Deleted : ScimRequestOutcome.NotCounted;
    }
}
