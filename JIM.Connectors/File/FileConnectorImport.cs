using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;
using System.Diagnostics;
namespace JIM.Connectors.File;

internal class FileConnectorImport
{
    private readonly CancellationToken _cancellationToken;
    private readonly ConnectedSystem _connectedSystem;
    private readonly FileConnectorReader _reader;
    private readonly FileConnectorObjectTypeInfo _objectTypeInfo;
    private readonly ILogger _logger;

    internal FileConnectorImport(
        ConnectedSystem connectedSystem,
        FileConnectorReader reader,
        FileConnectorObjectTypeInfo objectTypeInfo,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _connectedSystem = connectedSystem;
        _reader = reader;
        _objectTypeInfo = objectTypeInfo;
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    internal async Task<ConnectedSystemImportResult> GetFullImportObjectsAsync()
    {
        _logger.Verbose("GetFullImportObjects() called");
        var stopwatch = Stopwatch.StartNew();

        var result = new ConnectedSystemImportResult();
        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Information("GetFullImportObjects: O1 Cancellation requested. Stopping.");
            return result;
        }

        if (_connectedSystem.ObjectTypes == null)
            throw new InvalidDataException("_connectedSystem.ObjectTypes is null!");

        await _reader.CsvReader.ReadAsync();
        _reader.CsvReader.ReadHeader();

        while (await _reader.CsvReader.ReadAsync())
        {
            // always check to see if this task has been cancelled by the user.
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Information("GetFullImportObjects: O2 Cancellation requested. Stopping.");
                return result;
            }

            // start building the object that we pass back to JIM, representing the connected system object.
            // TODO: expand this to support the UPDATE scenario
            var importObject = new ConnectedSystemImportObject { ChangeType = Models.Enums.ObjectChangeType.Create };

            // work out what object type this row is meant to be
            if (!string.IsNullOrEmpty(_objectTypeInfo.PredefinedObjectType))
                importObject.ObjectType = _objectTypeInfo.PredefinedObjectType;
            else if (!string.IsNullOrEmpty(_objectTypeInfo.ObjectTypeColumnName))
                importObject.ObjectType = _reader.CsvReader.GetField(_objectTypeInfo.ObjectTypeColumnName);

            // get the schema object type, so we know what attributes we want to pull from the file.
            var objectType = _connectedSystem.ObjectTypes.SingleOrDefault(ot => ot.Name.Equals(importObject.ObjectType, StringComparison.InvariantCultureIgnoreCase));
            if (objectType == null)
            {
                // TODO: add a connected system setting that allows for the run to be stopped at the first error received, to avoid generating huge numbers of activity items.
                importObject.ErrorType = ConnectedSystemImportObjectError.CouldNotDetermineObjectType;
                importObject.ErrorMessage = $"GetFullImportObjects: Couldn't match object type '{importObject.ObjectType}' to the one(s) selected in the schema.";
                result.ImportObjects.Add(importObject);
                continue;
            }

            if (!objectType.Selected)
            {
                _logger.Debug($"Object type '{objectType.Name}' specified in the file is not selected. Skipping.");
                continue;
            }

            // enumerate the schema and extract the field from the csv row
            foreach (var attribute in objectType.Attributes.Where(q => q.Selected))
            {
                if (attribute.AttributePlurality != AttributePlurality.SingleValued)
                {
                    // do not know how to read multi-value fields using CsvReader at the moment, despite mentions 
                    // of it in the documentation, so cannot support MVAs for now.
                    throw new NotSupportedException("Multi-valued attributes are not currently supported with this Connector.");
                }

                var importObjectAttribute = new ConnectedSystemImportObjectAttribute
                {
                    Name = attribute.Name,
                    Type = attribute.Type
                };

                if (attribute.Type == AttributeDataType.Text)
                {
                    importObjectAttribute.StringValues.Add(_reader.CsvReader.GetField(attribute.Name));
                }
                else if (attribute.Type == AttributeDataType.Number)
                {
                    importObjectAttribute.IntValues.Add(_reader.CsvReader.GetField<int>(attribute.Name));
                }
                else if (attribute.Type == AttributeDataType.DateTime)
                {
                    importObjectAttribute.DateTimeValues.Add(_reader.CsvReader.GetField<DateTime>(attribute.Name));
                }
                else if (attribute.Type == AttributeDataType.Boolean)
                {
                    importObjectAttribute.BoolValue = _reader.CsvReader.GetField<bool>(attribute.Name);
                }
                else if (attribute.Type == AttributeDataType.Guid)
                {
                    importObjectAttribute.GuidValues.Add(_reader.CsvReader.GetField<Guid>(attribute.Name));
                }
                else if (attribute.Type == AttributeDataType.Reference)
                {
                    importObjectAttribute.ReferenceValues.Add(_reader.CsvReader.GetField(attribute.Name));
                }
                else
                {
                    throw new NotSupportedException($"FileConnector does not support attribute data type '{attribute.Type}'.");
                }

                importObject.Attributes.Add(importObjectAttribute);
            }

            result.ImportObjects.Add(importObject);
        }

        stopwatch.Stop();
        _reader.Dispose();
        _logger.Debug($"GetFullImportObjects: Executed in {stopwatch.Elapsed}");
        return result;
    }
}