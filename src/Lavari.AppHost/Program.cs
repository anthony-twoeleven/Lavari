var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Lavari_Web>("lavari-web")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.Build().Run();
