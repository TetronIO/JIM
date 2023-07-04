using JIM.Models.Core;

namespace JIM.Models.Tasking
{
	public class SynchronisationServiceTask : ServiceTask
	{
        /// <summary>
        /// The id for the connected system the run profile relates to.
        /// </summary>
        public int ConnectedSystemId { get; set; }

        /// <summary>
        /// The id for the connected system run profile to execute via this task.
        /// </summary>
        public int ConnectedSystemRunProfileId { get; set; }

        public SynchronisationServiceTask()
        {
            // for use by EntityFramework to construct db-sourced objects.
        }

		public SynchronisationServiceTask(int connectedSystemId, int connectedSystemRunProfileId)
        {
            ConnectedSystemId = connectedSystemId;
            ConnectedSystemRunProfileId = connectedSystemRunProfileId;
        }

        /// <summary>
        /// When a synchronisation service task is triggered by a user, this overload should be used to attribute the action to the user.
        /// </summary>
        public SynchronisationServiceTask(int connectedSystemId, int connectedSystemRunProfileId, MetaverseObject initiatingBy)
        {
            ConnectedSystemId = connectedSystemId;
            ConnectedSystemRunProfileId = connectedSystemRunProfileId;
            InitiatedBy = initiatingBy;
            InitiatedByName = initiatingBy.DisplayName;
        }
    }
}
