// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Localisation;

/// <summary>
/// An exception whose text a person reads in the portal, held as a <see cref="JimMessage"/> beside the British English
/// in <see cref="Exception.Message"/> (#3030). The portal shows <see cref="Detail"/> in the viewer's language
/// (<c>ex.ToDisplayText()</c>); the REST API, PowerShell and logs keep reading <see cref="Exception.Message"/>, so a
/// catch on the underlying type (<see cref="ArgumentException"/>, <see cref="NotSupportedException"/> and so on) keeps
/// working unchanged.
/// </summary>
public interface IJimMessageCarrier
{
    /// <summary>The message, for rendering in the reader's language; null where the exception carries only English.</summary>
    JimMessage? Detail { get; }
}
