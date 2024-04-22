var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.API>("api");

var legacy = builder.AddProject<Projects.LegacyAPI>("legacy");

builder.Build().Run();
