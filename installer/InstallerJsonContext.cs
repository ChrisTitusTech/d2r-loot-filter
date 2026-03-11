using System.Text.Json.Serialization;

namespace D2RInstaller;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Installer.GitHubReleaseResponse))]
[JsonSerializable(typeof(Installer.InstalledReleaseMetadata))]
internal sealed partial class InstallerJsonContext : JsonSerializerContext
{
}