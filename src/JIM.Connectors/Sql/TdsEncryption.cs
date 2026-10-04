// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql;

/// <summary>
/// What a server answers in the ENCRYPTION option of a TDS PRELOGIN, as MS-TDS 2.2.6.5 spells it.
/// </summary>
internal enum TdsEncryption : byte
{
    Off = 0x00,
    On = 0x01,
    NotSupported = 0x02,
    Required = 0x03
}
