// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a Connector implementing <see cref="Interfaces.IConnectorCatalogue"/> says about its catalogue.
/// </summary>
/// <param name="DocumentSettingName">The setting the catalogue helps the administrator write; the portal offers its catalogue-driven editor for this setting in place of the ordinary field.</param>
public sealed record ConnectorCatalogueDeclaration(string DocumentSettingName);
