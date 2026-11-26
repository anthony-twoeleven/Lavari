using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDotnetProject("lavari-web", "../Lavari.Web/Lavari.Web.csproj")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.Build().Run();
