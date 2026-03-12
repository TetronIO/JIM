using NUnit.Framework;
using Serilog;

namespace JIM.Workflow.Tests;

[SetUpFixture]
public class GlobalSetup
{
    [OneTimeSetUp]
    public void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        Log.CloseAndFlush();
    }
}
