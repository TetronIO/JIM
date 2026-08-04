// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// A Pending Export turned into the one request that applies it, before anything decides how that
/// request travels.
/// <para>
/// Composing the request and sending it are kept apart so a change is built identically whether it goes
/// out on its own or inside a bulk batch. A bulk export that shaped its payloads even slightly
/// differently would apply different data to the provider than the per-object export it stands in for,
/// and only the confirming import would ever say so.
/// </para>
/// </summary>
/// <param name="Method">The HTTP method the change is expressed as.</param>
/// <param name="Path">Where the change is sent, relative to the service provider's base URL and never rooted.</param>
/// <param name="Body">The payload, or null for a delete.</param>
/// <param name="EntityTag">The tag guarding the write, where the provider maintains entity tags and JIM holds one.</param>
/// <param name="ResourceId">The provider's id for the resource, or null for a create, where the provider assigns it.</param>
internal sealed record ScimExportOperation(
    HttpMethod Method,
    string Path,
    object? Body,
    string? EntityTag,
    string? ResourceId);
