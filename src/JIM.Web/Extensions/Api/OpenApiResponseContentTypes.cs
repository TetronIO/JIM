// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.OpenApi;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// Keeps the published OpenAPI document's response content types to what each action declares. Lives outside
/// <c>Program.cs</c> so it can be unit tested.
/// </summary>
public static class OpenApiResponseContentTypes
{
    /// <summary>
    /// Publishes a response whose <see cref="ProducesResponseTypeAttribute"/> names its own content type with that
    /// type alone. The generator merges a controller-wide <see cref="ProducesAttribute"/> into every response, so a
    /// certificate download would otherwise be published as JSON as well. Responses that declare no content type keep
    /// the ones the generator gave them.
    /// </summary>
    /// <param name="operation">The operation being generated.</param>
    /// <param name="endpointMetadata">The endpoint's metadata, including its MVC attributes.</param>
    public static void ApplyToOperation(OpenApiOperation operation, IList<object> endpointMetadata)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(endpointMetadata);

        if (operation.Responses == null)
            return;

        foreach (var declared in endpointMetadata.OfType<ProducesResponseTypeAttribute>())
        {
            var contentTypes = new MediaTypeCollection();
            ((IApiResponseMetadataProvider)declared).SetContentTypes(contentTypes);
            if (contentTypes.Count == 0)
                continue;

            if (!operation.Responses.TryGetValue(declared.StatusCode.ToString(), out var response) || response.Content == null)
                continue;

            foreach (var undeclared in response.Content.Keys.Where(k => !contentTypes.Contains(k)).ToList())
                response.Content.Remove(undeclared);
        }
    }
}
