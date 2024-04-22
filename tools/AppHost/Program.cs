using AppHost.Extensions;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.API>("api");

var legacy = builder.AddProject<Projects.LegacyAPI>("legacy");

builder.AddYarp("test")
    .Route("legacy", legacy, path: "/legacy", preservePath: false)
    .Route("api", api, path: YarpResource.CatchAll);

builder.Build().Run();
