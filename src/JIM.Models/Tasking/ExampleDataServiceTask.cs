using JIM.Models.Activities;
namespace JIM.Models.Tasking;

public class ExampleDataTemplateWorkerTask : WorkerTask
{
	/// <summary>
	/// Then id of the ExampleDataTemplate to execute via this task.
	/// </summary>
	public int TemplateId { get; set; }

	public ExampleDataTemplateWorkerTask()
	{
	}

	/// <summary>
	/// Factory method for creating a task triggered by a user.
	/// </summary>
	public static ExampleDataTemplateWorkerTask ForUser(int templateId, Guid userId, string userName)
	{
		return new ExampleDataTemplateWorkerTask
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
	public static ExampleDataTemplateWorkerTask ForApiKey(int templateId, Guid apiKeyId, string apiKeyName)
	{
		return new ExampleDataTemplateWorkerTask
		{
			TemplateId = templateId,
			InitiatedByType = ActivityInitiatorType.ApiKey,
			InitiatedById = apiKeyId,
			InitiatedByName = apiKeyName
		};
	}
}