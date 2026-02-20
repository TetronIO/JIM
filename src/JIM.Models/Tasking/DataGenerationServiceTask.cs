using JIM.Models.Activities;
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
	/// Factory method for creating a task triggered by a user.
	/// </summary>
	public static DataGenerationTemplateWorkerTask ForUser(int templateId, Guid userId, string userName)
	{
		return new DataGenerationTemplateWorkerTask
		{
			TemplateId = templateId,
			InitiatedByType = ActivityInitiatorType.User,
			InitiatedById = userId,
			InitiatedByName = userName
		};
	}

	/// <summary>
	/// Factory method for creating a task triggered by an API key.
	/// </summary>
	public static DataGenerationTemplateWorkerTask ForApiKey(int templateId, Guid apiKeyId, string apiKeyName)
	{
		return new DataGenerationTemplateWorkerTask
		{
			TemplateId = templateId,
			InitiatedByType = ActivityInitiatorType.ApiKey,
			InitiatedById = apiKeyId,
			InitiatedByName = apiKeyName
		};
	}
}