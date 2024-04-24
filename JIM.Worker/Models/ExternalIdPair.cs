using JIM.Models.Staging;

namespace JIM.Worker.Models;

/// <summary>
/// Used by the worker to keep track of cs import objects and their types.
/// </summary>
public struct ExternalIdPair
{
    public ConnectedSystemObjectType ConnectedSystemObjectType { get; init; }
    public ConnectedSystemImportObjectAttribute ConnectedSystemImportObjectAttribute { get; init; }
}