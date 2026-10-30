using System.Text.Json;
using Microsoft.JSInterop;

namespace CommonGround.Web.Services;

public sealed class WorkspaceLocalization
{
    private static readonly string[] Supported = ["en", "pl", "es", "fr", "de"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWebHostEnvironment _env;
    private readonly IJSRuntime? _js;
    private readonly IHttpContextAccessor? _http;
    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private string _language = "en";
    private bool _initializedFromBrowser;

    public WorkspaceLocalization(IWebHostEnvironment env, IJSRuntime? js = null, IHttpContextAccessor? http = null)
    {
        _env = env;
        _js = js;
        _http = http;
    }

    public string CurrentLanguage => _language;

    public IReadOnlyList<string> SupportedLanguages => Supported;

    public event Action? OnChange;

    public string T(string key)
    {
        if (_strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        return key;
    }

    public async Task SetLanguageAsync(string language, CancellationToken ct = default)
    {
        var normalized = (language ?? "en").Trim().ToLowerInvariant();
        if (Array.IndexOf(Supported, normalized) < 0)
        {
            normalized = "en";
        }
        if (string.Equals(_language, normalized, StringComparison.Ordinal) && _strings.Count > 0)
        {
            return;
        }
        _language = normalized;
        _strings = await LoadAsync(normalized, ct);
        await PersistToBrowserAsync(normalized);
        OnChange?.Invoke();
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_strings.Count > 0)
        {
            return;
        }
        if (!_initializedFromBrowser)
        {
            _initializedFromBrowser = true;
            var savedCookie = ReadLanguageCookie();
            if (!string.IsNullOrWhiteSpace(savedCookie) && Array.IndexOf(Supported, savedCookie) >= 0)
            {
                _language = savedCookie;
            }
            else
            {
                var saved = await ReadFromBrowserAsync();
                if (!string.IsNullOrWhiteSpace(saved) && Array.IndexOf(Supported, saved) >= 0)
                {
                    _language = saved;
                }
            }
        }
        _strings = await LoadAsync(_language, ct);
    }

    private string? ReadLanguageCookie()
    {
        try
        {
            var cookie = _http?.HttpContext?.Request.Cookies["cg-lang"];
            if (string.IsNullOrWhiteSpace(cookie))
            {
                return null;
            }
            var normalized = cookie.Trim().ToLowerInvariant();
            return Array.IndexOf(Supported, normalized) >= 0 ? normalized : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ReadFromBrowserAsync()
    {
        if (_js is null)
        {
            return null;
        }
        try
        {
            var saved = await _js.InvokeAsync<string>("localStorage.getItem", "cg-lang");
            if (!string.IsNullOrWhiteSpace(saved))
            {
                var normalized = saved.Trim().ToLowerInvariant();
                if (Array.IndexOf(Supported, normalized) >= 0)
                {
                    return normalized;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private async Task PersistToBrowserAsync(string language)
    {
        if (_js is null)
        {
            return;
        }
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", "cg-lang", language);
        }
        catch
        {
        }
        try
        {
            await _js.InvokeVoidAsync("eval", $"document.cookie = 'cg-lang={language}; path=/; max-age=31536000; SameSite=Lax'");
        }
        catch
        {
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(string language, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(_env.WebRootPath, "i18n", $"{language}.json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            await using var stream = File.OpenRead(path);
            var parsed = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, ct);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
