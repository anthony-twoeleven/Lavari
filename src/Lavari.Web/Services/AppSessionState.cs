using Lavari.Contracts.Business;
using Lavari.Contracts.Locking;
using Lavari.Contracts.Security;
using Lavari.Domain.Security;

namespace Lavari.Web.Services;

public sealed class AppSessionState
{
    public bool IsInitialized { get; private set; }
    public UserSessionDto? Session { get; private set; }
    public string? BrowserTimeZone { get; private set; }
    public IReadOnlyList<InAppNotificationDto> Notifications { get; private set; } = [];
    public IReadOnlyList<TimeEntryDto> ActiveTimers { get; private set; } = [];

    public event Action? Changed;

    public bool IsAuthenticated => Session is not null;

    public bool Can(string moduleKey, AccessLevel requiredLevel)
    {
        if (Session is null) return false;
        if (Session.IsFullAccess) return true;
        return Session.EffectivePermissions.TryGetValue(moduleKey, out var lvl) && lvl >= (int)requiredLevel;
    }

    public void SetSession(UserSessionDto? session, string? browserTimeZone)
    {
        Session = session;
        BrowserTimeZone = browserTimeZone;
        IsInitialized = true;
        Changed?.Invoke();
    }

    public void UpdateNotifications(IReadOnlyList<InAppNotificationDto> items)
    {
        Notifications = items;
        Changed?.Invoke();
    }

    public void UpdateActiveTimers(IReadOnlyList<TimeEntryDto> timers)
    {
        ActiveTimers = timers;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Session = null;
        Notifications = [];
        ActiveTimers = [];
        IsInitialized = true;
        Changed?.Invoke();
    }
}
