namespace CommonGround.Infrastructure.MetaModels;

public sealed class MetaModelOptions
{
    public const string SectionName = "MetaModels";
    public string BaseUrl { get; set; } = "https://api.meta.ai/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string SparkModel { get; set; } = "muse-spark-1.3-contributor";
    public string WonderModel { get; set; } = "muse-spark-1.3-contributor";
    public string VoiceModel { get; set; } = "muse-voice-transcribe-1.0";
    public string SamModel { get; set; } = "sam-3.1";
    public string ImageModel { get; set; } = "muse-image-1.0";
    public bool UseFallbackWhenNoKey { get; set; } = false;
    public bool UseSearchGrounding { get; set; } = true;
    public string ReasoningEffort { get; set; } = "medium";
    public int MaxOutputTokens { get; set; } = 32000;
    public double Temperature { get; set; } = 0.1;
    public double TopP { get; set; } = 0.95;
    public int TotalRequestTimeoutMinutes { get; set; } = 10;
    public int AttemptTimeoutMinutes { get; set; } = 5;
    public int DiscoveryMaxRetryAttempts { get; set; } = 2;
}
