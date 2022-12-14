namespace JIM.Models.Interfaces
{
    public interface IConnector
    {
        /// <summary>
        /// The name of the Connector. This will be shown when an administrator wants to configure a new instance of the Connector.
        /// The administrator will be able to provide their own name for an instance of the Connector.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The default description for the Connector. This will be shown when an administrator wants to configure a new instance of the Connector.
        /// The administrator will be able to provide their own description for an instance of the Connector to make it relevant to their environment.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// The URL where more information on the Connector can be found. This will be shown when an administrator wants to configure a new instance of this Connector.
        /// </summary>
        public string Url { get; set; }
    }
}
