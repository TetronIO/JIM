using JIM.Scheduler;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<Scheduler>();
    })
    .Build();

await host.RunAsync();
