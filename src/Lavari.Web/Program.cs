using Lavari.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/api/ping", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program;
