using System.Text.Json.Serialization;

namespace C_toolsSetup;

internal sealed class UpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("cad")]
    public string Cad { get; set; } = "";

    [JsonPropertyName("bundleName")]
    public string BundleName { get; set; } = "";

    [JsonPropertyName("bundleZipUrl")]
    public string BundleZipUrl { get; set; } = "";

    [JsonPropertyName("bundleZipSha256")]
    public string BundleZipSha256 { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("setupUrl")]
    public string SetupUrl { get; set; } = "";

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; } = "";

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset? PublishedAtUtc { get; set; }
}
