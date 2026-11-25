using System.Text.Json;
using Lavari.Contracts.Common;
using Lavari.Contracts.Security;
using Microsoft.JSInterop;

namespace Lavari.Web.Services;

public sealed record ApiCallResult<T>(bool IsSuccess, int StatusCode, T? Data, ApiErrorResponse? Error);

public sealed class LavariBrowserApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IJSRuntime _js;

    public LavariBrowserApiClient(IJSRuntime js) => _js = js;

    public ValueTask SetCsrfTokenAsync(string? token) => _js.InvokeVoidAsync("lavariInterop.setCsrfToken", token);
    public ValueTask SetEditingSessionAsync(string? sessionId) => _js.InvokeVoidAsync("lavariInterop.setEditingSession", sessionId);
    public ValueTask<string?> GetBrowserTimeZoneAsync() => _js.InvokeAsync<string?>("lavariInterop.getBrowserTimeZone");
    public ValueTask DownloadUrlAsync(string url) => _js.InvokeVoidAsync("lavariInterop.downloadUrl", url);
    public ValueTask SetDirtyGuardAsync(bool isDirty) => _js.InvokeVoidAsync("lavariInterop.setDirtyGuard", isDirty);

    public async Task RefreshCsrfTokenAsync()
    {
        var resp = await SendAsync<JsonElement>("GET", "/api/auth/antiforgery", null);
        if (resp.IsSuccess && resp.Data.ValueKind == JsonValueKind.Object && resp.Data.TryGetProperty("token", out var tok))
        {
            await SetCsrfTokenAsync(tok.GetString());
        }
    }

    public async Task<ApiCallResult<UserSessionDto>> LoginAsync(string email, string password)
    {
        var raw = await SendAsync<JsonElement>("POST", "/api/auth/login", new LoginRequest { Email = email, Password = password });
        if (!raw.IsSuccess)
        {
            return new ApiCallResult<UserSessionDto>(false, raw.StatusCode, null, raw.Error);
        }

        if (raw.Data.TryGetProperty("csrfToken", out var tok))
        {
            await SetCsrfTokenAsync(tok.GetString());
        }

        var session = raw.Data.GetProperty("session").Deserialize<UserSessionDto>(JsonOpts);
        if (session is not null)
        {
            await SetEditingSessionAsync(session.EditingSessionId);
        }
        return new ApiCallResult<UserSessionDto>(true, raw.StatusCode, session, null);
    }

    public Task<ApiCallResult<T>> GetAsync<T>(string url) => SendAsync<T>("GET", url, null);
    public Task<ApiCallResult<T>> PostAsync<T>(string url, object? body = null) => SendAsync<T>("POST", url, body);
    public Task<ApiCallResult<T>> PutAsync<T>(string url, object? body = null) => SendAsync<T>("PUT", url, body);
    public Task<ApiCallResult<object?>> DeleteAsync(string url) => SendAsync<object?>("DELETE", url, null);

    public async Task<ApiCallResult<T>> UploadFileAsync<T>(
        string url,
        Dictionary<string, string?> fields,
        string fileName,
        string contentType,
        byte[] bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        var jsResp = await _js.InvokeAsync<JsFetchResponse>("lavariInterop.uploadFile", url, fields, fileName, contentType, b64);
        return ParseResponse<T>(jsResp);
    }

    private async Task<ApiCallResult<T>> SendAsync<T>(string method, string url, object? body)
    {
        var bodyJson = body is null ? null : JsonSerializer.Serialize(body, JsonOpts);
        var jsResp = await _js.InvokeAsync<JsFetchResponse>("lavariInterop.fetchJson", method, url, bodyJson);
        return ParseResponse<T>(jsResp);
    }

    private static ApiCallResult<T> ParseResponse<T>(JsFetchResponse jsResp)
    {
        if (jsResp.Ok)
        {
            if (string.IsNullOrWhiteSpace(jsResp.Body) || typeof(T) == typeof(object))
            {
                return new ApiCallResult<T>(true, jsResp.Status, default, null);
            }

            var data = JsonSerializer.Deserialize<T>(jsResp.Body, JsonOpts);
            return new ApiCallResult<T>(true, jsResp.Status, data, null);
        }

        ApiErrorResponse? err = null;
        if (!string.IsNullOrWhiteSpace(jsResp.Body))
        {
            try { err = JsonSerializer.Deserialize<ApiErrorResponse>(jsResp.Body, JsonOpts); } catch { }
        }

        err ??= new ApiErrorResponse
        {
            Code = $"HTTP-{jsResp.Status}",
            Message = jsResp.Status == 401 ? "Please sign in to continue." : "The request could not be completed.",
            PossibleCause = "Verify your permissions or reload the page and retry.",
            CorrelationId = Guid.NewGuid().ToString("N")[..8]
        };
        return new ApiCallResult<T>(false, jsResp.Status, default, err);
    }

    private sealed record JsFetchResponse(int Status, bool Ok, string Body);
}
