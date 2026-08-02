// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Connectors.MockScim;

/// <summary>
/// One resource held by <see cref="MockScimProvider"/>. Attributes are kept loosely typed so a test can
/// hand the provider whatever shape it needs to exercise, including the multi-valued and complex forms
/// the schema flattening turns into JIM attributes.
/// </summary>
internal sealed class MockScimResource
{
    public required string Id { get; init; }

    /// <summary>The resource type name, matching what <c>/ResourceTypes</c> publishes.</summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// The provider's <c>meta.lastModified</c>. Settable so a test can move a resource across a delta
    /// watermark boundary deliberately, which is the whole point of having a provider under control.
    /// </summary>
    public DateTimeOffset LastModified { get; set; }

    /// <summary>The provider's <c>meta.version</c> entity tag.</summary>
    public string Version { get; set; } = "W/\"1\"";

    /// <summary>Everything other than <c>id</c> and <c>meta</c>, which the provider renders itself.</summary>
    public Dictionary<string, object?> Attributes { get; } = new(StringComparer.Ordinal);
}
