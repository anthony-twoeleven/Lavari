using Lavari.Api.Endpoints;
using Lavari.Api.Hubs;
using Lavari.Api.Middleware;
using Lavari.Api.Security;
using Lavari.Application.Abstractions;
using Lavari.Infrastructure;
using Lavari.Infrastructure.Persistence;
using Lavari.ServiceDefaults;
using Lavari.Web.Components;
using Lavari.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TickerQ.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<LavariBrowserApiClient>();
builder.Services.AddScoped<AppSessionState>();

var keysDir = ResolveDataProtectionKeysPath(builder.Configuration["Lavari:DataProtectionKeysPath"]);
builder.Services.AddDataProtection()
    .SetApplicationName("Lavari")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lavari.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect("/login");
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect("/login");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "lavari.csrf";
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.AddSignalR();
builder.Services.AddScoped<HttpCurrentActorContext>();
builder.Services.AddScoped<ICurrentActorContext>(sp => sp.GetRequiredService<HttpCurrentActorContext>());
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

var enableTickerQ = !string.Equals(builder.Configuration["Lavari:DisableTickerQ"], "true", StringComparison.OrdinalIgnoreCase);
builder.Services.AddLavariInfrastructure(builder.Configuration, enableTickerQ);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var actorCtx = scope.ServiceProvider.GetRequiredService<HttpCurrentActorContext>();
    actorCtx.SetSystemPrincipal("System Seeder");

    var db = scope.ServiceProvider.GetRequiredService<LavariDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");

    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    var seedSuperAdmin = app.Environment.IsDevelopment() ||
                         string.Equals(builder.Configuration["Lavari:SeedDevelopmentSuperAdmin"], "true", StringComparison.OrdinalIgnoreCase);
    var templatesDir = Path.Combine(AppContext.BaseDirectory, "Templates");
    await seeder.SeedAsync(
        seedSuperAdmin,
        builder.Configuration["Lavari:SuperAdminEmail"],
        builder.Configuration["Lavari:SuperAdminPassword"],
        templatesDir);
}

app.MapDefaultEndpoints();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseMiddleware<LavariActorHydrationMiddleware>();
app.UseMiddleware<LavariExceptionMiddleware>();
app.UseMiddleware<LavariAntiforgeryMiddleware>();

if (enableTickerQ)
{
    app.UseTickerQ();
}

app.MapLavariApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ResolveDataProtectionKeysPath(string? configured)
{
    if (!string.IsNullOrWhiteSpace(configured))
    {
        var full = Path.GetFullPath(configured.Trim());
        Directory.CreateDirectory(full);
        return full;
    }

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var baseDir = string.IsNullOrWhiteSpace(localAppData)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lavari")
        : Path.Combine(localAppData, "Lavari");
    var dir = Path.Combine(baseDir, "Keys");
    Directory.CreateDirectory(dir);
    return dir;
}

public partial class Program;
