using CsvHelper;

namespace JIM.Connectors.File
{
    /// <summary>
    /// Enables us to encapsulate both reader-related objects that need disposing when no longer required.
    /// </summary>
    internal class FileConnectorReader : IDisposable
    {
        internal StreamReader Reader { get; }

        internal CsvReader CsvReader { get; }

        internal FileConnectorReader(StreamReader reader, CsvReader csvReader)
        {
            Reader = reader;
            CsvReader = csvReader;
        }

        public void Dispose()
        {
            Reader.Dispose();
            CsvReader.Dispose();
        }
    }
}
