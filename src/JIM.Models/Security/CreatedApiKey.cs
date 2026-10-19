// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security;

/// <summary>
/// An API key JIM has just minted, with its secret. The secret is never stored, so this is the only time it is
/// available.
/// </summary>
/// <param name="Key">The stored key.</param>
/// <param name="Secret">The full secret, to hand to whoever will use the key.</param>
public record CreatedApiKey(ApiKey Key, string Secret);
