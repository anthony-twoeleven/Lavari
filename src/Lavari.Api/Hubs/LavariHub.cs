using System.Security.Claims;
using Lavari.Application.Abstractions;
using Lavari.Application.Services;
using Lavari.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Lavari.Api.Hubs;

[Authorize]
public sealed class LavariHub : Hub
{
    private readonly IAuthService _authService;

    public LavariHub(IAuthService authService)
    {
        _authService = authService;
    }

    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId:N}");
        }
        await base.OnConnectedAsync();
    }

    public async Task SubscribeModule(string moduleKey)
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            throw new HubException("Unauthenticated.");
        }

        if (!PermissionRegistry.IsRegistered(moduleKey))
        {
            throw new HubException("Unknown module.");
        }

        var grants = await _authService.LoadEffectivePermissionsAsync(userId, Context.ConnectionAborted);
        if (!PermissionRegistry.Satisfies(grants, moduleKey, AccessLevel.Read))
        {
            throw new HubException("Forbidden.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"mod:{moduleKey}");
    }
}

public sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<LavariHub> _hub;

    public SignalRRealtimeNotifier(IHubContext<LavariHub> hub) => _hub = hub;

    public Task NotifyRecordChangedAsync(string moduleKey, string recordType, Guid recordId, string eventType, CancellationToken ct = default)
        => _hub.Clients.Group($"mod:{moduleKey}").SendAsync("RecordChanged", new
        {
            moduleKey,
            recordType,
            recordId,
            eventType
        }, ct);

    public Task NotifyUserAsync(Guid userId, string category, string title, string message, string? recordType, Guid? recordId, CancellationToken ct = default)
        => _hub.Clients.Group($"user:{userId:N}").SendAsync("InAppNotification", new
        {
            category,
            title,
            message,
            recordType,
            recordId
        }, ct);
}
