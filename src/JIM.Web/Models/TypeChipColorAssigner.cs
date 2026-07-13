// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using MudBlazor;
namespace JIM.Web.Models;

/// <summary>
/// Assigns a MudBlazor chip colour to each distinct free-form type name (e.g. a Connected System Object
/// Type) the first time it's seen, scoped to a single page/component instance. This guarantees every type
/// actually present in that page's table gets a distinct colour, rather than relying on a hash of the name
/// that could collide between two arbitrary type names.
/// </summary>
public class TypeChipColorAssigner
{
    private static readonly Color[] Palette =
    [
        Color.Primary,
        Color.Secondary,
        Color.Tertiary,
        Color.Info,
        Color.Success,
        Color.Warning,
        Color.Error,
        Color.Dark
    ];

    private readonly Dictionary<string, Color> _colors = new();

    public Color GetColor(string typeName)
    {
        if (!_colors.TryGetValue(typeName, out var color))
        {
            color = Palette[_colors.Count % Palette.Length];
            _colors[typeName] = color;
        }

        return color;
    }
}
