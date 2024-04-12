using JIM.Models.Core;
namespace JIM.Models.Tasking;

public class DataGenerationTemplateWorkerTask : WorkerTask
{
	/// <summary>
	/// Then id of the DataGenerationTemplate to execute via this task.
	/// </summary>
	public int TemplateId { get; set; }

	public DataGenerationTemplateWorkerTask()
	{
	}

	public DataGenerationTemplateWorkerTask(int templateId, MetaverseObject user)
	{
		TemplateId = templateId;
		InitiatedBy = user;
		InitiatedByName = user.DisplayName;
	}
}