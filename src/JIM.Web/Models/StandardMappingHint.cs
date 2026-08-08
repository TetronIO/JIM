// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One counterpart name to show against a Metaverse Attribute in the Attribute Flow editor, with the standard
/// (or standards, when a name is shared) whose vocabulary it belongs to. Advisory display data only.
/// </summary>
/// <param name="StandardLabel">The standard's display name, e.g. "LDAP/AD", or "SCIM 2.0 · LDAP/AD" when both share the name.</param>
/// <param name="CounterpartName">The attribute's name in that vocabulary, e.g. "givenName".</param>
/// <param name="Notes">Optional nuance about the correspondence, each note prefixed with its standard when more than one contributed.</param>
public sealed record StandardMappingHint(string StandardLabel, string CounterpartName, string? Notes);
