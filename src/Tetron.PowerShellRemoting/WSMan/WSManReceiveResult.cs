// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting.WSMan;

/// <summary>What one Receive returned: the stdout payloads in order, and whether the command has finished.</summary>
public sealed record WSManReceiveResult(IReadOnlyList<byte[]> Data, bool CommandDone);
