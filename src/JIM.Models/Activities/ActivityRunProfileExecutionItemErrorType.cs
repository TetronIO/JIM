// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

public enum ActivityRunProfileExecutionItemErrorType
{
    /// <summary>
    /// Default value indicating no error has been set.
    /// Excluded from error counts and statistics.
    /// </summary>
    NotSet,
    /// <summary>
    /// Object matching found multiple Metaverse Objects matching a single Connected System Object
    /// during a join operation. An MVO can only be joined to a single CSO per Connected System.
    /// The administrator should review Object Matching Rules to ensure unique matches.
    /// </summary>
    AmbiguousMatch,
    /// <summary>
    /// Could not find a valid object type in the Connected System schema for the imported object type,
    /// or the object type exists but has no external ID attribute defined.
    /// </summary>
    CouldNotMatchObjectType,
    /// <summary>
    /// A Connected System Object cannot be joined to a Metaverse Object because that MVO already
    /// has a CSO from this Connected System. This typically indicates duplicate data in the source
    /// system or Object Matching Rules that are not sufficiently unique.
    /// </summary>
    CouldNotJoinDueToExistingJoin,
    /// <summary>
    /// The imported object contains duplicate attribute names (case-insensitive).
    /// The source data must be de-duplicated before it can be imported.
    /// </summary>
    DuplicateImportedAttributes,
    /// <summary>
    /// The imported object is missing a value for the external ID attribute.
    /// Every imported object must have a non-empty external ID to be uniquely identified.
    /// </summary>
    MissingExternalIdAttributeValue,
    /// <summary>
    /// The imported object contains an attribute that is not defined in the Connected System schema.
    /// The schema should be updated to include the attribute, or the import data corrected.
    /// </summary>
    UnexpectedAttribute,
    /// <summary>
    /// An attribute contains a reference to another Connected System Object that could not be found.
    /// This may occur when the referenced object is outside the configured container scope. On import, one error
    /// covers every reference the object was left with; the item's <c>UnresolvedReferenceCount</c> records how many,
    /// and is set even when another error holds the item's error slot (#959).
    /// </summary>
    UnresolvedReference,
    /// <summary>
    /// The external ID attribute has a data type that is not supported for use as an external identifier.
    /// </summary>
    UnsupportedExternalIdAttributeType,
    /// <summary>
    /// During confirming import, one or more exported attribute values were not found on the CSO.
    /// The export will be retried.
    /// </summary>
    ExportNotConfirmed,
    /// <summary>
    /// During confirming import, one or more exported attribute values exceeded the maximum retry count.
    /// Manual intervention may be required.
    /// </summary>
    ExportConfirmationFailed,
    /// <summary>
    /// CSO creation failed during import but no specific error was recorded.
    /// This indicates a bug in the import processing logic.
    /// </summary>
    CsoCreationFailed,
    /// <summary>
    /// Multiple objects in the same import batch have the same external ID.
    /// All duplicates are marked with this error - none are processed.
    /// The data owner must fix the source data to ensure unique external IDs.
    /// </summary>
    DuplicateObject,
    /// <summary>
    /// The export generated an external identifier (e.g., LDAP Distinguished Name) that is
    /// structurally invalid. This typically occurs when expression-based ID attributes evaluate
    /// with null or empty input values, producing malformed identifiers.
    /// </summary>
    InvalidGeneratedExternalId,

    /// <summary>
    /// An unexpected exception occurred during sync processing. The full exception message and
    /// stack trace are captured. This indicates a bug in the processing logic rather than a data issue.
    /// </summary>
    UnhandledError,

    /// <summary>
    /// An expression-based Attribute Flow mapping threw while evaluating (for example a registered
    /// function received an unexpected value type, or the expression failed to parse). This is a
    /// configuration or data issue with the Synchronisation Rule's expression, not a JIM bug. The object is
    /// errored and no value is flowed for the failing mapping; the target attribute is left untouched
    /// (a thrown expression is never treated as a deliberate null/clear).
    /// </summary>
    ExpressionEvaluationError,

    /// <summary>
    /// An Expression-based Attribute Flow read an attribute the object has no value for, and the mapping's
    /// Missing Input Behaviour says that is a fault rather than something the Expression handles. The message
    /// names the Expression, the target attribute, the missing input, and whether the mapping alone was skipped
    /// (Fail this mapping: the object's other attributes still flowed) or the whole object was left untouched
    /// (Fail the object). Distinct from <see cref="ExpressionEvaluationError"/>, which is an Expression that
    /// threw: this one evaluated fine, or would have, and the administrator asked to be told instead.
    /// </summary>
    ExpressionMissingInput,

    /// <summary>
    /// During Attribute Flow, a multi-valued source attribute held more than one value but the target
    /// attribute is single-valued. A single-valued target can hold only one value, and JIM will not
    /// select one arbitrarily, so no value was flowed for that attribute (import) or no Pending Export
    /// was generated for it (export); the object's other attributes are unaffected. The administrator
    /// should map the source to a multi-valued target, reduce the source to a single value, or use an
    /// Expression to select one value deterministically.
    /// </summary>
    MultiValuedToSingleValued,

    /// <summary>
    /// A delta import could not be performed because the directory's change tracking watermark
    /// (e.g., OpenLDAP accesslog timestamp) was not available. The connector automatically
    /// fell back to performing a full import instead, which correctly imported all objects and
    /// established the watermark for future delta imports. This is informational — the import
    /// succeeded, but was slower than expected. If this recurs, verify that the directory's
    /// change tracking mechanism (e.g., accesslog overlay) is accessible to the bind account.
    /// </summary>
    DeltaImportFallbackToFullImport,

    /// <summary>
    /// SPEC-1082 D10, Run Profile Verification Mode only: the stored import content hash matched
    /// the freshly computed incoming hash, but the honest diff (run in full because Verification
    /// Mode never skips) still found attribute changes. This is the dangerous disagreement a
    /// content-hash skip would have silently missed; it indicates either a bug in the content hash
    /// calculator or an unexpected divergence between the hash's canonical form and the diff. The
    /// object's changes were still applied normally; this error is purely diagnostic.
    /// </summary>
    ImportHashVerificationFailed,

    /// <summary>
    /// The Connector could not parse one of the imported object's attribute values into the type the
    /// Connected System schema declares for it (for example a date column holding "not applicable", or a
    /// number column holding text). The object itself was sound and was imported with the values that did
    /// parse; only the failing attribute was omitted, so its Connected System Object retains whatever
    /// value it previously held for that attribute. The administrator should correct the source data, or
    /// change the attribute's type in the schema if the source is right and the schema is wrong.
    /// </summary>
    ImportAttributeValueError,

    /// <summary>
    /// The Connector reported that an imported object cannot be processed because of how the Connected
    /// System is configured in JIM (for example no attributes are selected for the object type). The
    /// object was not imported. Unlike most import errors this is not a source-data problem: the fix is
    /// in the Connected System's configuration, not in the Connected System itself.
    /// </summary>
    ConnectorConfigurationError,

    /// <summary>
    /// An outbound Synchronisation Rule could not export a Metaverse Object because that Object's one
    /// Connected System Object in the target Connected System is of a different Connected System Object
    /// Type than the Rule targets. A Metaverse Object holds at most one Connected System Object per
    /// Connected System, so the slot the Rule needs is already occupied and no Pending Export is staged.
    /// The export-side counterpart of <see cref="CouldNotJoinDueToExistingJoin"/>.
    ///
    /// Two configurations reach this. Two outbound Synchronisation Rules into one Connected System whose
    /// scopes overlap: narrow them so no Metaverse Object satisfies both. Or a Connected System that both
    /// imports objects of one Object Type and exports another for the same Metaverse Objects: give the
    /// exported Object Types their own Connected System over the same target.
    /// </summary>
    CouldNotExportDueToExistingConnectedSystemObject,

    /// <summary>
    /// JIM refused an export before sending it, because a class it would add to the object has required
    /// attributes that neither the export writes nor the object already holds (#492). The message names
    /// exactly what is missing. This is configuration to act on, not a JIM defect: add an Attribute Flow
    /// for the missing attribute(s), or withdraw the auxiliary class selection that brought the class in.
    /// </summary>
    ClassMembershipRequirementsNotMet,

    /// <summary>
    /// Unique Value Generation (#242): no free candidate value was found within the attempt limit. The
    /// message names the attribute, the last candidate tried and the scope (gate) that rejected it. No
    /// value is written for the object.
    /// </summary>
    GeneratedValueExhausted,

    /// <summary>
    /// Unique Value Generation (#242): a sequence's next number outgrew the mapping's fixed width, and
    /// the flow's overflow behaviour is set to stop the object and report rather than allow a longer
    /// number. No value is written for the object.
    /// </summary>
    GeneratedValueWidthExceeded,

    /// <summary>
    /// Unique Value Generation (#242): the target Connected System rejected a generated value and another
    /// Connected System already holds it (the value is anchored elsewhere), so the object needs an
    /// administrator's decision. The Pending Export is Parked until Allow the rename, Retry or Leave it
    /// resolves it.
    /// </summary>
    GeneratedValueCollisionUnresolved,

    /// <summary>
    /// The Connected System rejected the export because a value it carries is already held by another object
    /// there (an account name, an email address, an object already at that DN). Reported by Connectors that
    /// classify such rejections (<c>SupportsUniquenessRejectionClassification</c>); a data conflict for an
    /// administrator to resolve, not a JIM defect, so it does not count as an unhandled error.
    /// </summary>
    UniqueValueAlreadyInUse,

    /// <summary>
    /// An Object Matching Rule could not be worked out for the object (#2052): its expression threw, its result could
    /// not be converted to the Metaverse attribute's type, or the rule's shape cannot be compared. Before provisioning,
    /// also when the rule's expression could not be worked out for some of the target's objects and nothing else
    /// matched, since one of those may be the object's own account. The object was
    /// neither joined nor projected (on export, not provisioned), so a broken rule cannot flood the Metaverse with
    /// duplicate identities. The message names the rule and the fault; the object is retried on the next
    /// synchronisation.
    /// </summary>
    ObjectMatchingRuleFailed,

    /// <summary>
    /// An imported object's External ID already belongs to a Connected System Object of another Object Type in the
    /// same Connected System, so the object has changed Object Type (an overlapping Object Type was selected or
    /// deselected, the schema changed, or the entry was converted in the directory). JIM creates nothing and leaves
    /// the existing Connected System Object and its join untouched, rather than retire it and create a new one,
    /// which could delete its Metaverse Object. Raised only for Connectors whose External IDs identify one object
    /// across every Object Type (<c>ExternalIdsUniqueAcrossObjectTypes</c>), and only while the Run Profile does
    /// not allow Object Type changes (#2049). A configuration conflict for an administrator to resolve, not a JIM
    /// defect, so it does not count as an unhandled error.
    /// </summary>
    ObjectTypeChanged,

    /// <summary>
    /// Active Directory refused to create or enable a user account because the account has no password (#2039).
    /// The message names the remedies: give the provisioning Synchronisation Rule an Initial Password, so JIM creates
    /// the account disabled and enables it once the password is set, or create the account disabled. Configuration to
    /// act on, not a JIM defect, so it does not count as an unhandled error.
    /// </summary>
    PasswordRequiredToEnableAccount,

    /// <summary>
    /// Before provisioning, an Object Matching Rule found more than one existing Connected System Object in the target
    /// for the Metaverse Object (#2052). JIM cannot tell which account is the identity's, so it neither joined one nor
    /// provisioned another; the item lists the accounts it found. Previously the lowest-id account was joined.
    /// </summary>
    AmbiguousProvisioningMatch,

    /// <summary>
    /// A Synchronisation Rule's scoping criterion could not be evaluated for the object (#2053): its pattern ran past
    /// its time limit on one of the object's values. JIM cannot tell whether the object is in scope, so it takes no
    /// action for it this run: it is neither brought into scope nor taken out, and keeps any join it already has. The
    /// message names the attribute and the pattern; the object is retried on the next synchronisation. Configuration
    /// to act on, not a JIM defect, so it does not count as an unhandled error.
    /// </summary>
    ScopingCriterionFailed,

    /// <summary>
    /// Two or more of the dates exported to a multi-valued attribute are the same date at the step of time the
    /// Connected System holds that attribute to (#2153): for example two times within one second, for a directory that
    /// holds whole seconds. The Connected System can only hold them as one value, so the export sends them as one; the
    /// rest of the attribute, and the object's other attributes, still export. Reported so the merging is never silent.
    /// The message names the attribute, the resolution and how many values the Connected System will hold.
    /// </summary>
    DatesCollapseAtTargetResolution
}
