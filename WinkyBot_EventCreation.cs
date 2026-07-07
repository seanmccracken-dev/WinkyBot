using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;


namespace WinkyBot.Functions;

public class WinkyBot_EventCreation
{
    internal const string DefaultEventDescription = "Join us for a night of fun and games!";

    private readonly ILogger _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly IHttpClientFactory _httpClientFactory;

    public WinkyBot_EventCreation(ILoggerFactory loggerFactory, CosmosClient cosmosClient, IHttpClientFactory httpClientFactory)
    {
        _logger = loggerFactory.CreateLogger<WinkyBot_EventCreation>();
        _cosmosClient = cosmosClient;
        _httpClientFactory = httpClientFactory;
    }

    [Function("WinkyBot_EventCreation")]
    public async Task Run([TimerTrigger("0 10 * * 2")] TimerInfo myTimer)
    {
        _logger.LogInformation("Timer trigger function executed at: {executionTime}", DateTime.Now);

        var eventName = "Friday Night Games";
        var eventDescription = DefaultEventDescription;
        
        TimeZoneInfo cstZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        DateTime currentTime = TimeZoneInfo.ConvertTime(DateTime.UtcNow, cstZone);
        int daysUntilFriday = ((int)DayOfWeek.Friday - (int)currentTime.DayOfWeek + 7) % 7;
        if (daysUntilFriday == 0 && currentTime.Hour >= 20)
        {
            daysUntilFriday = 7;
        }
        DateTime eventDateTime = currentTime.AddDays(daysUntilFriday).Date.AddHours(20);
        DateTime eventDateTimeUtc = TimeZoneInfo.ConvertTimeToUtc(eventDateTime, cstZone);

        var winkyEvent = new WinkyEvent
        {
            EventName = eventName,
            EventDateTimeUtc = eventDateTimeUtc,
            Attending = Array.Empty<string>(),
            Tentative = Array.Empty<string>(),
            Absent = Array.Empty<string>()
        };

        var discordPayload = BuildDiscordPayload(winkyEvent, eventDescription);
        
        await SendToDiscord(winkyEvent, discordPayload);

        await WriteToCosmosDb(winkyEvent);
    }

    private async Task SendToDiscord(WinkyEvent winkyEvent, object payload)
    {
        var client = _httpClientFactory.CreateClient();
        var json = JsonSerializer.Serialize(payload);
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        var configuredChannelId = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_ID");

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(configuredChannelId))
        {
            _logger.LogError("Missing Discord configuration. Ensure DISCORD_TOKEN and DISCORD_CHANNEL_ID are set.");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{configuredChannelId}/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

        var response = await client.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var messageId))
                winkyEvent.DiscordMessageId = messageId.GetString();

            if (root.TryGetProperty("channel_id", out var channelId))
                winkyEvent.DiscordChannelId = channelId.GetString();

            _logger.LogInformation("Successfully sent event to Discord. MessageId: {messageId}, ChannelId: {channelId}",
                winkyEvent.DiscordMessageId, winkyEvent.DiscordChannelId);
        }
        else
        {
            _logger.LogError("Failed to send event to Discord. Status Code: {statusCode}", response.StatusCode);
        }
    }
    
    private async Task WriteToCosmosDb(WinkyEvent winkyEvent)
    {
        _logger.LogInformation("Writing event to Cosmos DB.");
        try
        {
            var container = _cosmosClient.GetContainer("WinkyBot_DB", "Events");

            var response = await container.CreateItemAsync(winkyEvent, new PartitionKey(winkyEvent.id));   
            _logger.LogInformation("Successfully wrote event to Cosmos DB. Status: {statusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write event to Cosmos DB: {message}", ex.Message);
        }     
    }

    internal static object BuildDiscordPayload(WinkyEvent winkyEvent, string eventDescription)
    {
        var unixTime = new DateTimeOffset(winkyEvent.EventDateTimeUtc).ToUnixTimeSeconds();

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = winkyEvent.EventName,
                    description = eventDescription,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    color = 5814783,
                    fields = new[]
                    {
                        new { name = "Date", value = $"<t:{unixTime}:D>", inline = true },
                        new { name = "Time", value = $"<t:{unixTime}:t>", inline = true },
                        new { name = "\u200B", value = "\u200B", inline = false },
                        new { name = "✅ Attending", value = BuildMentionList(winkyEvent.Attending), inline = false },
                        new { name = "🤔 Tentative", value = BuildMentionList(winkyEvent.Tentative), inline = false },
                        new { name = "🕒 Late", value = BuildMentionList(winkyEvent.Late), inline = false },
                        new { name = "❌ Absent", value = BuildMentionList(winkyEvent.Absent), inline = false }
                    },
                    footer = new
                    {
                        text = "WinkyBot Scheduler"
                    }
                }
            },
            components = new[]
            {
                new
                {
                    type = 1,
                    components = new[]
                    {
                        new
                        {
                            type = 2,
                            style = 3,
                            label = "Attending",
                            custom_id = $"event:{winkyEvent.id}:attending"
                        },
                        new
                        {
                            type = 2,
                            style = 2,
                            label = "Tentative",
                            custom_id = $"event:{winkyEvent.id}:tentative"
                        },
                        new
                        {
                            type = 2,
                            style = 1,
                            label = "Late",
                            custom_id = $"event:{winkyEvent.id}:late"
                        },
                        new
                        {
                            type = 2,
                            style = 4,
                            label = "Absent",
                            custom_id = $"event:{winkyEvent.id}:absent"
                        }
                    }
                }
            }
        };
    }

    internal static object BuildDiscordPayloadWithoutButtons(WinkyEvent winkyEvent, string eventDescription)
    {
        var unixTime = new DateTimeOffset(winkyEvent.EventDateTimeUtc).ToUnixTimeSeconds();

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = winkyEvent.EventName,
                    description = eventDescription,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    color = 5814783,
                    fields = new[]
                    {
                        new { name = "Date", value = $"<t:{unixTime}:D>", inline = true },
                        new { name = "Time", value = $"<t:{unixTime}:t>", inline = true },
                        new { name = "\u200B", value = "\u200B", inline = false },
                        new { name = "✅ Attending", value = BuildMentionList(winkyEvent.Attending), inline = false },
                        new { name = "🤔 Tentative", value = BuildMentionList(winkyEvent.Tentative), inline = false },
                        new { name = "🕒 Late", value = BuildMentionList(winkyEvent.Late), inline = false },
                        new { name = "❌ Absent", value = BuildMentionList(winkyEvent.Absent), inline = false }
                    },
                    footer = new
                    {
                        text = "WinkyBot Scheduler"
                    }
                }
            },
            components = Array.Empty<object>()
        };
    }

    private static string BuildMentionList(IEnumerable<string> userIds)
    {
        var mentions = userIds
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Select(userId => $"<@{userId}>")
            .ToArray();

        return mentions.Length == 0 ? "\u200B" : string.Join("\n", mentions);
    }
}