using System.Text.Json.Serialization;

namespace Microsoft.DevProxy.Plugins.RequestLogs.MinimalPermissions;

internal class PermissionError
{
  [JsonPropertyName("requestUrl")]
  public string Url { get; set; } = string.Empty;
  [JsonPropertyName("message")]
  public string Message { get; set; } = string.Empty;
}