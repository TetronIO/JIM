// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// Thrown when something that needs every value of a Connected System Object's attribute is handed an object whose
/// values of that attribute were loaded in part (#2816): saving it back, reading the attribute's whole value set, or
/// replacing or clearing that set. A fault in JIM, never a configuration matter: export execution's load never hands
/// such an object to a path that needs the whole set.
/// </summary>
public class PartiallyLoadedObjectException : InvalidOperationException
{
    public PartiallyLoadedObjectException(string message) : base(message) { }
}
