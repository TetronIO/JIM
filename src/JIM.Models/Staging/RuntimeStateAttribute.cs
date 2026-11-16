// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Marks a <see cref="ConnectedSystem"/> property as runtime state (#2950): a value the Worker maintains as it runs, written
/// only by its own narrow statement. A save of the whole Connected System leaves every such column out, so a copy loaded
/// earlier (the portal's page, or the Worker's own run) cannot put back the value it loaded.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RuntimeStateAttribute : Attribute;
