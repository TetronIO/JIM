using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

internal class LdapConnectorSchema
{
    private readonly LdapConnection _connection;
    private readonly ILogger _logger;
    private readonly LdapConnectorRootDse _rootDse;
    private readonly ConnectorSchema _schema;
    private string _schemaNamingContext = null!;

    internal LdapConnectorSchema(LdapConnection ldapConnection, ILogger logger, LdapConnectorRootDse rootDse)
    {
        _connection = ldapConnection;
        _logger = logger;
        _rootDse = rootDse;
        _schema = new ConnectorSchema();
    }

    internal async Task<ConnectorSchema> GetSchemaAsync()
    {
        return await Task.Run(() =>
        {
            // get the DN for the schema partition
            var schemaNamingContext = GetSchemaNamingContext();
            if (string.IsNullOrEmpty(schemaNamingContext))
                throw new Exception($"Couldn't get schema naming context from rootDSE.");
            _schemaNamingContext = schemaNamingContext;

            // query: classes, structural, don't return hidden by default classes, exclude defunct classes
            var filter = "(&(objectClass=classSchema)(objectClassCategory=1)(defaultHidingValue=FALSE)(!(isDefunct=TRUE)))";
            var request = new SearchRequest(_schemaNamingContext, filter, SearchScope.Subtree);
            var response = (SearchResponse)_connection.SendRequest(request);

            if (response.ResultCode != ResultCode.Success)
                throw new Exception($"No success getting object types. Result code: {response.ResultCode}");

            if (response.Entries.Count == 0)
                throw new Exception($"Couldn't get object types. Non returned from connected system. Result code: {response.ResultCode}");

            // enumerate each object class entry
            foreach (SearchResultEntry entry in response.Entries)
            {
                var name = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "name") ?? throw new Exception($"No name on object class entry: {entry.DistinguishedName}");
                var objectType = new ConnectorSchemaObjectType(name);

                // now go and work out which attributes the object type has and add them to the object type
                if (AddObjectTypeAttributes(objectType))
                {
                    // make a recommendation on what unique identifier attribute(s) to use
                    // for AD/AD LDS:
                    var objectGuidSchemaAttribute = objectType.Attributes.Single(a => a.Name.Equals("objectguid", StringComparison.OrdinalIgnoreCase));
                    objectType.RecommendedExternalIdAttribute = objectGuidSchemaAttribute;

                    // say what the secondary external identifier needs to be for LDAP systems
                    var dnSchemaAttribute = objectType.Attributes.Single(a => a.Name.Equals("distinguishedname", StringComparison.OrdinalIgnoreCase));
                    objectType.RecommendedSecondaryExternalIdAttribute = dnSchemaAttribute;

                    // override the data type for distinguishedName, we want to handle it as a string, not a reference type
                    // we do this as a DN attribute on an object cannot be a reference to itself. that would make no sense.
                    dnSchemaAttribute.Type = AttributeDataType.Text;

                    // override writability for distinguishedName: AD marks it as systemOnly but the LDAP connector
                    // needs it writable because the DN is provided by the client in Add requests to specify where
                    // the object is created. It's not written as an attribute — it's the target of the LDAP Add operation.
                    dnSchemaAttribute.Writability = AttributeWritability.Writable;

                    // object type looks good to go, add it to the schema
                    _schema.ObjectTypes.Add(objectType);
                }
            }

            return _schema;
        });
    }

    private bool AddObjectTypeAttributes(ConnectorSchemaObjectType objectType)
    {
        // walk up the parent object class tree
        var objectClassEntries = new List<SearchResultEntry>();
        var continueGettingClasses = true;
        string? ldapdisplayname;
        string? subclassof;
        var objectClassName = objectType.Name;

        while (continueGettingClasses)
        {
            var objectClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={objectClassName})");
            if (objectClassEntry == null)
            {
                // some object classes do not have a schema entry, i.e. some system objects.
                return false;
            }

            ldapdisplayname = LdapConnectorUtilities.GetEntryAttributeStringValue(objectClassEntry, "ldapdisplayname");
            subclassof = LdapConnectorUtilities.GetEntryAttributeStringValue(objectClassEntry, "subclassof");

            if (ldapdisplayname == subclassof)
                continueGettingClasses = false;

            objectClassEntries.Add(objectClassEntry);
            if (subclassof != null)
                objectClassName = subclassof;
        }

        foreach (var objectClassEntry in objectClassEntries)
            GetObjectClassAttributesRecursively(objectClassEntry, objectType);

        // todo: it's possible there's some duplication of attributes going on due to attributes being specified on structural and auxiliary classes, so de-dupe before we return
        objectType.Attributes = objectType.Attributes.OrderBy(q => q.Name).ToList();

        return true;
    }

    /// <summary>
    /// Adds all attributes on a supplied object class entry and then recurses into any auxiliary and system auxiliary references.
    /// </summary>
    private void GetObjectClassAttributesRecursively(SearchResultEntry objectClassEntry, ConnectorSchemaObjectType objectType)
    {
        var objectClassName = LdapConnectorUtilities.GetEntryAttributeStringValue(objectClassEntry, "ldapdisplayname") ??
                              throw new Exception($"No ldapdisplayname value on {objectClassEntry.DistinguishedName}");

        AddAttributesFromSchemaProperty(objectClassEntry, "maycontain", objectClassName, false, objectType);
        AddAttributesFromSchemaProperty(objectClassEntry, "mustcontain", objectClassName, true, objectType);
        AddAttributesFromSchemaProperty(objectClassEntry, "systemmaycontain", objectClassName, false, objectType);
        AddAttributesFromSchemaProperty(objectClassEntry, "systemmustcontain", objectClassName, true, objectType);

        // now recurse into any auxiliary and system auxiliary classes
        var auxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "auxiliaryclass");
        var systemAuxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "systemauxiliaryclass");

        if (auxiliaryClasses != null)
        {
            foreach (var auxiliaryClass in auxiliaryClasses)
            {
                var auxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={auxiliaryClass})") ??
                                          throw new Exception($"Couldn't find auxiliary class entry: {auxiliaryClass}");

                GetObjectClassAttributesRecursively(auxiliaryClassEntry, objectType);
            }
        }

        if (systemAuxiliaryClasses == null)
            return;

        foreach (var systemAuxiliaryClass in systemAuxiliaryClasses)
        {
            var systemAuxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={systemAuxiliaryClass})") ??
                                            throw new Exception($"Couldn't find auxiliary class entry: {systemAuxiliaryClass}");

            GetObjectClassAttributesRecursively(systemAuxiliaryClassEntry, objectType);
        }
    }

    private void AddAttributesFromSchemaProperty(SearchResultEntry objectClassEntry, string propertyName, string objectClassName, bool required, ConnectorSchemaObjectType objectType)
    {
        if (!objectClassEntry.Attributes.Contains(propertyName))
            return;

        foreach (string attributeName in objectClassEntry.Attributes[propertyName].GetValues(typeof(string)))
        {
            if (objectType.Attributes.All(q => q.Name != attributeName))
            {
                var attr = GetSchemaAttribute(attributeName, objectClassName, required, objectType.Name);
                if (attr != null)
                    objectType.Attributes.Add(attr);
            }
        }
    }

    /// <summary>
    /// Looks up the schema entry for an attribute and returns a ConnectorSchemaAttribute with full metadata.
    /// Returns null if the attribute is defunct (should be excluded from the schema entirely).
    /// </summary>
    private ConnectorSchemaAttribute? GetSchemaAttribute(string attributeName, string objectClass, bool required, string objectTypeName)
    {
        var attributeEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _schemaNamingContext, $"(ldapdisplayname={attributeName})") ??
                             throw new Exception($"Couldn't retrieve schema attribute: {attributeName}");

        // filter out defunct attributes — these are deprecated and should not be presented to users
        var isDefunctRawValue = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "isdefunct");
        if (bool.TryParse(isDefunctRawValue, out var isDefunct) && isDefunct)
        {
            _logger.Debug("GetSchemaAttribute: Skipping defunct attribute '{AttributeName}'.", attributeName);
            return null;
        }

        var description = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "description");
        var adminDescription = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "admindescription");

        // isSingleValued comes back as TRUE/FALSE string
        // if we can't convert to a bool, assume it's single-valued
        var isSingleValuedRawValue = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "issinglevalued");
        if (!bool.TryParse(isSingleValuedRawValue, out var isSingleValued))
        {
            isSingleValued = true;
            _logger.Verbose("GetSchemaAttribute: Could not establish if SVA/MVA for attribute {AttributeName}. Assuming SVA. Raw value: '{RawValue}'", attributeName, isSingleValuedRawValue);
        }

        var attributePlurality = isSingleValued ? AttributePlurality.SingleValued : AttributePlurality.MultiValued;

        // Active Directory SAM layer override: certain attributes are declared as multi-valued in the
        // LDAP schema but the SAM layer enforces single-valued semantics on security principals.
        // Override the plurality to match actual runtime behaviour so mapping validation works correctly.
        if (!isSingleValued && LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(attributeName, objectTypeName, _rootDse.IsActiveDirectory))
        {
            attributePlurality = AttributePlurality.SingleValued;
            _logger.Debug("GetSchemaAttribute: Overriding '{AttributeName}' from multi-valued to single-valued on object type '{ObjectType}' — " +
                "Active Directory SAM layer enforces single-valued semantics on this attribute for security principals.",
                attributeName, objectTypeName);
        }

        // work out what data-type the attribute is using the shared utility method
        var omSyntax = LdapConnectorUtilities.GetEntryAttributeIntValue(attributeEntry, "omsyntax");
        var attributeDataType = AttributeDataType.Text;

        if (omSyntax.HasValue)
        {
            try
            {
                attributeDataType = LdapConnectorUtilities.GetLdapAttributeDataType(omSyntax.Value);
            }
            catch (InvalidDataException)
            {
                // Unsupported omSyntax - default to Text
                _logger.Warning("GetSchemaAttribute: Unsupported omSyntax {OmSyntax} for attribute {AttributeName}. Defaulting to Text.", omSyntax.Value, attributeName);
            }
        }

        // handle exceptions:
        // the objectGUID is typed as a string in the schema, but the byte-array returned does not decode to a string, but does to a Guid. go figure.
        if (attributeName.Equals("objectguid", StringComparison.OrdinalIgnoreCase))
            attributeDataType = AttributeDataType.Guid;

        // determine writability from schema metadata: systemOnly, systemFlags (constructed bit), and linkID (back-links)
        var systemOnlyRawValue = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "systemonly");
        bool.TryParse(systemOnlyRawValue, out var systemOnly);
        var systemFlags = LdapConnectorUtilities.GetEntryAttributeIntValue(attributeEntry, "systemflags");
        var linkId = LdapConnectorUtilities.GetEntryAttributeIntValue(attributeEntry, "linkid");
        var writability = LdapConnectorUtilities.DetermineAttributeWritability(systemOnly, systemFlags, linkId);

        var attribute = new ConnectorSchemaAttribute(attributeName, attributeDataType, attributePlurality, required, objectClass, writability);

        if (!string.IsNullOrEmpty(description))
            attribute.Description = description;
        else if (!string.IsNullOrEmpty(adminDescription))
            attribute.Description = adminDescription;

        return attribute;
    }

    private string? GetSchemaNamingContext()
    {
        // get the schema naming context from an attribute on the rootDSE
        var request = new SearchRequest() { Scope = SearchScope.Base };
        request.Attributes.Add("schemaNamingContext");
        var response = (SearchResponse)_connection.SendRequest(request);

        if (response.ResultCode != ResultCode.Success)
        {
            _logger.Warning("GetSchemaNamingContext: No success. Result code: {ResultCode}", response.ResultCode);
            return null;
        }

        if (response.Entries.Count == 0)
        {
            _logger.Warning("GetSchemaNamingContext: Didn't get any results!");
            return null;
        }

        var entry = response.Entries[0];
        return LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "schemaNamingContext");
    }
}