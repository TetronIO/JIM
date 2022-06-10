namespace JIM.Models.Tasking
{
	public class DataGenerationTemplateServiceTask : ServiceTask
	{
		/// <summary>
        /// Then id of the DataGenerationTemplate to execute via this task.
        /// </summary>
		public int TemplateId { get; set; }

		public DataGenerationTemplateServiceTask()
        {

        }

		public DataGenerationTemplateServiceTask(int templateId)
        {
            TemplateId = templateId;
        }
    }
}
