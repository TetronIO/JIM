// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Transactional;

/// <summary>
/// What an import join's lookup compares (#2020): the Metaverse Object Type searched, the Metaverse attribute read, the
/// values' data type, and for Text whether case must match. Two Object Matching Rules with the same key look up a value
/// identically, so a page's batch looks every value of one key up in one query.
/// </summary>
public readonly record struct JoinMatchLookupKey(
    int MetaverseObjectTypeId,
    int MetaverseAttributeId,
    AttributeDataType ValueType,
    bool CaseSensitive);
