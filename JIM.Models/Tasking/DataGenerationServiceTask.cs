using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
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

	/// <summary>
	/// When data generation is triggered by a user, this overload should be used to attribute the action to the user.
	/// </summary>
	public DataGenerationTemplateWorkerTask(int templateId, MetaverseObject initiatedBy)
	{
		TemplateId = templateId;
		InitiatedByType = ActivityInitiatorType.User;
		InitiatedById = initiatedBy.Id;
		InitiatedByMetaverseObject = initiatedBy;
		InitiatedByName = initiatedBy.DisplayName;
	}

	/// <summary>
	/// When data generation is triggered by an API key, this overload should be used to attribute the action to the API key.
	/// </summary>
	public DataGenerationTemplateWorkerTask(int templateId, ApiKey apiKey)
	{
		TemplateId = templateId;
		InitiatedByType = ActivityInitiatorType.ApiKey;
		InitiatedById = apiKey.Id;
		InitiatedByApiKey = apiKey;
		InitiatedByName = apiKey.Name;
	}
}