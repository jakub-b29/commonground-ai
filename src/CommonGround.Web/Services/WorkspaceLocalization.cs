using System.Text.Json;

namespace CommonGround.Web.Services;

public sealed class WorkspaceLocalization
{
    private static readonly string[] Supported = ["en", "pl", "es", "fr", "de"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWebHostEnvironment _env;
    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private string _language = "en";

    public WorkspaceLocalization(IWebHostEnvironment env)
    {
        _env = env;
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
        OnChange?.Invoke();
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_strings.Count > 0)
        {
            return;
        }
        _strings = await LoadAsync(_language, ct);
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
