using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Servers
{
    public class ConnectedSystemServer
    {
        private JimApplication Application { get; }

        internal ConnectedSystemServer(JimApplication application)
        {
            Application = application;
        }

        public IList<ConnectedSystem> GetConnectedSystems()
        {
            return Application.Repository.ConnectedSystems.GetConnectedSystems();
        }

        public ConnectedSystem? GetConnectedSystem(Guid id)
        {
            return Application.Repository.ConnectedSystems.GetConnectedSystem(id);
        }

        public IList<SyncRun>? GetSynchronisationRuns(Guid id)
        {
            return Application.Repository.ConnectedSystems.GetSynchronisationRuns(id);
        }

        public IList<ConnectedSystemAttribute>? GetAttributes(Guid id)
        {
            return Application.Repository.ConnectedSystems.GetAttributes(id);
        }

        public IList<ConnectedSystemObjectType>? GetObjectTypes(Guid id)
        {
            return Application.Repository.ConnectedSystems.GetObjectTypes(id);
        }

        public ConnectedSystemObject? GetConnectedSystemObject(Guid connectedSystemId, Guid id)
        {
            return Application.Repository.ConnectedSystems.GetConnectedSystemObject(connectedSystemId, id);
        }

        public int GetConnectedSystemObjectCount()
        {
            return Application.Repository.ConnectedSystems.GetConnectedSystemObjectCount();
        }

        public int GetConnectedSystemObjectOfTypeCount(ConnectedSystemObjectType connectedSystemObjectType)
        {
            return Application.Repository.ConnectedSystems.GetConnectedSystemObjectOfTypeCount(connectedSystemObjectType.Id);
        }

        public IList<SyncRule>? GetSyncRules()
        {
            return Application.Repository.ConnectedSystems.GetSyncRules();
        }

        public SyncRule? GetSyncRule(Guid id)
        {
            return Application.Repository.ConnectedSystems.GetSyncRule(id);
        }
    }
}