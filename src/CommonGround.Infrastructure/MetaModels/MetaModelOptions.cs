namespace CommonGround.Infrastructure.MetaModels;

public sealed class MetaModelOptions
{
    public const string SectionName = "MetaModels";
    public string BaseUrl { get; set; } = "https://api.meta.ai/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string SparkModel { get; set; } = "muse-spark-1.3";
    public string WonderModel { get; set; } = "muse-wonder";
    public string VoiceModel { get; set; } = "muse-voice-transcribe-1.0";
    public string SamModel { get; set; } = "sam-3.1";
    public string ImageModel { get; set; } = "muse-image-1.0";
    public bool UseFallbackWhenNoKey { get; set; } = true;
}
