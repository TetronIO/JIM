using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP
{
    internal class LdapConnectorSchema
    {
        private readonly LdapConnection _connection;
        private readonly ConnectorSchema _schema;
        private readonly string _root;

        internal LdapConnectorSchema(LdapConnection ldapConnection, string rootDistinguishedName) 
        {
            _connection = ldapConnection;
            _schema = new ConnectorSchema();
            _root = rootDistinguishedName;
        }

        internal async Task<ConnectorSchema> GetSchemaAsync()
        {
            // future improvement: work out how to get the default naming context programatically, so we don't have to ask the
            // user for the root via a setting value.

            return await Task.Run(() => 
            { 
                // query: classes, structural, don't return hidden by default classes
                var filter = "(&(objectClass=classSchema)(objectClassCategory=1)(defaultHidingValue=FALSE))";
                var dn = $"CN=Schema,CN=Configuration,{_root}";
                var request = new SearchRequest(dn, filter, SearchScope.Subtree);
                var response = (SearchResponse)_connection.SendRequest(request); // object doesn't exist when querying ADLDS!                

                if (response.ResultCode != ResultCode.Success)
                    throw new Exception($"No success getting object types. Result code: {response.ResultCode}");

                if (response.Entries.Count == 0)
                    throw new Exception($"Couldn't get object types. Non returned from connected system. Result code: {response.ResultCode}");

                // enumerate each object class entry
                foreach (SearchResultEntry entry in response.Entries)
                {
                    var name = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "name") ?? 
                        throw new Exception($"No name on object class entry: {entry.DistinguishedName}");
                    var objectType = new ConnectorSchemaObjectType(name);

                    // now go and work out which attributes the object type has and add them to the object type
                    if (AddObjectTypeAttributes(objectType))
                    {
                        // make a recommendation on what unique identifier attribute(s) to use
                        // for AD/ADLDS:
                        var objectGuidSchemaAttribute = objectType.Attributes.Single(a => a.Name.Equals("objectguid", StringComparison.CurrentCultureIgnoreCase));
                        objectType.RecommendedExternalIdAttribute = objectGuidSchemaAttribute;

                        // say what the secondary external identifier needs to be for LDAP systems
                        var dnSchemaAttribute = objectType.Attributes.Single(a => a.Name.Equals("distinguishedname", StringComparison.CurrentCultureIgnoreCase));
                        objectType.RecommendedSecondaryExternalIdAttribute = dnSchemaAttribute;
                        
                        // override the object type for distinguishedName, we want to handle it as a string, not a reference type
                        // we do this as a DN attribute on an object cannot be a reference to itself. that would make no sense.
                        dnSchemaAttribute.Type = AttributeDataType.Text;

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
            string objectClassName = objectType.Name;

            while (continueGettingClasses)
            {
                var objectClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={objectClassName})");
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
            
            if (objectClassEntry.Attributes.Contains("maycontain"))
                foreach (string attributeName in objectClassEntry.Attributes["maycontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, false));

            if (objectClassEntry.Attributes.Contains("mustcontain"))
                foreach (string attributeName in objectClassEntry.Attributes["mustcontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, true));

            if (objectClassEntry.Attributes.Contains("systemmaycontain"))
                foreach (string attributeName in objectClassEntry.Attributes["systemmaycontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, false));

            if (objectClassEntry.Attributes.Contains("systemmustcontain"))
                foreach (string attributeName in objectClassEntry.Attributes["systemmustcontain"].GetValues(typeof(string)))
                    if (!objectType.Attributes.Any(q => q.Name == attributeName))
                        objectType.Attributes.Add(GetSchemaAttribute(attributeName, objectClassName, true));

            // now recurse into any auxliary and system auxiliary classes
            var auxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "auxiliaryclass");
            var systemAuxiliaryClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(objectClassEntry, "systemauxiliaryclass");

            if (auxiliaryClasses != null)
            {
                foreach (var auxiliaryClass in auxiliaryClasses)
                {
                    var auxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={auxiliaryClass})") ?? 
                        throw new Exception($"Couldn't find auxiliary class entry: {auxiliaryClass}");

                    GetObjectClassAttributesRecursively(auxiliaryClassEntry, objectType);
                }
            }

            if (systemAuxiliaryClasses != null)
            {
                foreach (var systemAuxiliaryClass in systemAuxiliaryClasses)
                {
                    var systemAuxiliaryClassEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={systemAuxiliaryClass})") ?? 
                        throw new Exception($"Couldn't find auxiliary class entry: {systemAuxiliaryClass}");

                    GetObjectClassAttributesRecursively(systemAuxiliaryClassEntry, objectType);
                }
            }
        }

        private ConnectorSchemaAttribute GetSchemaAttribute(string attributeName, string objectClass, bool required)
        {
            var attributeEntry = LdapConnectorUtilities.GetSchemaEntry(_connection, _root, $"(ldapdisplayname={attributeName})") ?? 
                throw new Exception($"Couldn't retrieve schema attribute: {attributeName}");

            var description = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "description");
            var admindescription = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "admindescription");
            
            // isSingleValued comes back as TRUE/FALSE string
            // if we can't convert to a bool, assume it's single-valued
            var isSingleValuedRawValue = LdapConnectorUtilities.GetEntryAttributeStringValue(attributeEntry, "issinglevalued");
            if (!bool.TryParse(isSingleValuedRawValue, out bool isSingleValued))
            {
                isSingleValued = true;
                Log.Verbose($"GetSchemaAttribute: Could not establish if SVA/MVA for attribute {attributeName}. Assuming SVA. Raw value: '{isSingleValuedRawValue}'");
            }

            var attributePlurality = isSingleValued ? AttributePlurality.SingleValued : AttributePlurality.MultiValued;

            // work out what data-type the attribute is
            var omSyntax = LdapConnectorUtilities.GetEntryAttributeIntValue(attributeEntry, "omsyntax");
            var attributeDataType = AttributeDataType.Text;

            if (omSyntax.HasValue)
            {
                // https://social.technet.microsoft.com/wiki/contents/articles/52570.active-directory-syntaxes-of-attributes.aspx
                switch (omSyntax)
                {
                    case 1:
                    case 10:
                        attributeDataType = AttributeDataType.Boolean;
                        break;
                    case 2:
                    case 65:
                        attributeDataType = AttributeDataType.Number;
                        break;
                    case 3:
                        attributeDataType = AttributeDataType.Binary;
                        break;
                    case 4:
                    case 6:
                    case 18:
                    case 19:
                    case 20:
                    case 22:
                    case 27:
                    case 64:
                        attributeDataType = AttributeDataType.Text;
                        break;
                    case 23:
                    case 24:
                        attributeDataType = AttributeDataType.DateTime;
                        break;
                    case 127:
                        attributeDataType = AttributeDataType.Reference;
                        break;
                }
            }

            // handle exceptions:
            // the objectGUID is typed as a string in the schema, but the byte-array returned does not decode to a string, but does to a Guid. go figure.
            if (attributeName.Equals("objectguid", StringComparison.OrdinalIgnoreCase))
                attributeDataType = AttributeDataType.Guid;

            var attribute = new ConnectorSchemaAttribute(attributeName, attributeDataType, attributePlurality, required, objectClass);

            if (!string.IsNullOrEmpty(description))
                attribute.Description = description;
            else if (!string.IsNullOrEmpty(admindescription))
                attribute.Description = admindescription;            

            return attribute;
        }
    }
}
