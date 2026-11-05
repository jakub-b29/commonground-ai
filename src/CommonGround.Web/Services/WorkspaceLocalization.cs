using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CommonGround.Web.Services;

public sealed class WorkspaceLocalization
{
    private static readonly string[] Supported = ["en", "pl", "es", "fr", "de"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWebHostEnvironment _env;
    private readonly IJSRuntime? _js;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger<WorkspaceLocalization>? _logger;
    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private string _language = "en";
    private bool _initializedFromBrowser;

    public WorkspaceLocalization(IWebHostEnvironment env, IJSRuntime? js = null, IHttpContextAccessor? http = null, ILogger<WorkspaceLocalization>? logger = null)
    {
        _env = env;
        _js = js;
        _http = http;
        _logger = logger;
    }

    public string CurrentLanguage => _language;

    public string ActiveLocale => _language;

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
        if (_strings.Count > 0)
        {
            OnChange?.Invoke();
        }
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
        var file = $"{language}.json";
        var webRoot = _env.WebRootPath ?? string.Empty;
        var contentRoot = _env.ContentRootPath ?? string.Empty;
        var baseDir = AppContext.BaseDirectory ?? string.Empty;
        var candidates = new[]
        {
            Path.Combine(webRoot, "i18n", file),
            Path.Combine(contentRoot, "wwwroot", "i18n", file),
            Path.Combine(baseDir, "wwwroot", "i18n", file),
            Path.Combine(baseDir, "i18n", file),
        };
        var attempted = new List<string>(candidates.Length);
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            var seen = false;
            foreach (var prior in attempted)
            {
                if (string.Equals(prior, path, StringComparison.OrdinalIgnoreCase))
                {
                    seen = true;
                    break;
                }
            }
            if (seen)
            {
                continue;
            }
            attempted.Add(path);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                await using var stream = File.OpenRead(path);
                var parsed = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, ct);
                if (parsed is null)
                {
                    _logger?.LogWarning("Localization file deserialized to null: {Path}", path);
                    continue;
                }
                return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load localization file: {Path}", path);
            }
        }
        _logger?.LogWarning("Localization file {File} not found. Attempted paths: {Paths}", file, string.Join("; ", attempted));
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

