using System.Text.Json.Serialization;

namespace WinkyBot.Functions;

public class WinkyEvent
{
    [JsonPropertyName("id")]
    public string id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("eventName")]
    public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("eventDateTimeUtc")]
    public DateTime EventDateTimeUtc { get; set; }
    
    [JsonPropertyName("attending")]    
    public string[] Attending { get; set; } = Array.Empty<string>();

    [JsonPropertyName("tentative")]
    public string[] Tentative { get; set; } = Array.Empty<string>();

    [JsonPropertyName("absent")]
    public string[] Absent { get; set; } = Array.Empty<string>();

    [JsonPropertyName("discordChannelId")]
    public string? DiscordChannelId { get; set; }

    [JsonPropertyName("discordMessageId")]
    public string? DiscordMessageId { get; set; }

}