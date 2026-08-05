// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The well-known keys and values used to classify a Connected System Object Type.
/// </summary>
/// <remarks>
/// Classification is stored as open key/value tags rather than an enum so that a connector can express concepts
/// JIM does not model without a schema change per connector. These constants cover the classifications JIM itself
/// consumes; a connector may add its own keys alongside them. A type carrying no tags is unclassified, which every
/// consumer must treat as "show it, do not group it".
/// </remarks>
public static class ObjectTypeTags
{
    public static class Keys
    {
        /// <summary>
        /// What kind of class this object type is in the Connected System's own schema model. Maps RFC 4512's
        /// structural / auxiliary / abstract kinds for directory connectors; other connectors set what maps for
        /// them, or leave it unset.
        /// </summary>
        public const string ClassKind = "class-kind";

        /// <summary>
        /// Whether this object type is one an administrator would manage, or one the Connected System uses
        /// internally (configuration or operational classes that only add noise to the schema screen).
        /// </summary>
        public const string Visibility = "visibility";
    }

    public static class Values
    {
        /// <summary>An object type that can be instantiated, and defines an object's primary identity.</summary>
        public const string ClassKindStructural = "structural";

        /// <summary>An object type that augments another with additional attributes, rather than standing alone.</summary>
        public const string ClassKindAuxiliary = "auxiliary";

        /// <summary>An object type that exists only to be inherited from, and cannot be instantiated.</summary>
        public const string ClassKindAbstract = "abstract";

        /// <summary>An object type the Connected System uses for its own configuration or operation.</summary>
        public const string VisibilityInternal = "internal";

        /// <summary>An object type an administrator would expect to manage.</summary>
        public const string VisibilityStandard = "standard";
    }
}
