// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Localisation;

/// <summary>
/// The message catalogues of the application servers other than the Connected System, preview and expression code
/// (#3030). Each is a resource file under <c>src/JIM.Application/Resources</c>; a message is built with
/// <c>JimMessage.Create(ApplicationMessageCatalogues.Servers, nameof(ServerMessages.Key), arguments)</c>.
/// </summary>
public static partial class ApplicationMessageCatalogues
{
    /// <summary>Refusals and results of the Metaverse, search, certificate, settings, scheduling and other servers.</summary>
    public const string Servers = "JIM.Application:ServerMessages";

    /// <summary>The reasons a Service Health row gives for its condition.</summary>
    public const string Health = "JIM.Application:HealthMessages";
}
