﻿namespace JIM.Models.Extensibility;

/// <summary>
/// Represents a collection of functions, provided in the form of a .NET dll file.
/// </summary>
public class FunctionLibrary
{
    public int Id { get; set; }

    public string Filename { get; set; } = null!;

    public string Version { get; set; } = null!;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime LastUpdated { get; set; }

    // todo: signing info?
}