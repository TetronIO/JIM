using JIM.Models.Core;

namespace JIM.Models.Tasking
{
	public class ClearConnectedSystemObjectsTask : ServiceTask
	{
        /// <summary>
        /// The id for the connected system the run profile relates to.
        /// </summary>
        public int ConnectedSystemId { get; set; }

        public ClearConnectedSystemObjectsTask()
        {
            // for use by EntityFramework to construct db-sourced objects.
        }

		public ClearConnectedSystemObjectsTask(int connectedSystemId)
        {
            ConnectedSystemId = connectedSystemId;
        }

        /// <summary>
        /// When a clear connected system objects task is triggered by a user, this overload should be used to attribute the action to the user.
        /// </summary>
        public ClearConnectedSystemObjectsTask(int connectedSystemId, MetaverseObject initiatingBy)
        {
            ConnectedSystemId = connectedSystemId;
            InitiatedBy = initiatingBy;
            InitiatedByName = initiatingBy.DisplayName;
        }
    }
}
