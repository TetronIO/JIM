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
    }
}
