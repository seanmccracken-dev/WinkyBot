using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;


namespace WinkyBot.Functions;

public class WinkyBot_EventCreation
{
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
        var eventDescription = "Join us for a night of fun and games!";
        
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

        var discordPayload = new
        {
            embeds = new[]
            {
                new
                {
                    title = eventName,
                    description = eventDescription,
                    timestamp = DateTime.Now.ToString("o"),    
                    color = 5814783,
                    fields = new[]
                    {
                        new { name = "Date", value = $"<t:{new DateTimeOffset(eventDateTimeUtc).ToUnixTimeSeconds()}:D>", inline = true },
                        new { name = "Time", value = $"<t:{new DateTimeOffset(eventDateTimeUtc).ToUnixTimeSeconds()}:t>", inline = true },
                        new { name = "\u200B", value = "\u200B", inline = false },
                        new { name = "✅ Attending: ", value = "0", inline = false },
                        new { name = "🤔 Tentative: ", value = "0", inline = false },
                        new { name = "❌ Absent: ", value = "0", inline = false }
                    },
                    footer = new
                    {
                        text = "WinkyBot Scheduler"
                    }
                }
            }
        };
        
        await SendToDiscord(winkyEvent, discordPayload);

        await WriteToCosmosDb(winkyEvent);
    }

    private async Task SendToDiscord(WinkyEvent winkyEvent, object payload)
    {
        var client = _httpClientFactory.CreateClient();
        var json = JsonSerializer.Serialize(payload);
        var content  = new StringContent(json, Encoding.UTF8, "application/json");

        string? webhookUrl = Environment.GetEnvironmentVariable("DISCORD_CHANNEL_WEBHOOK");

        var response = await client.PostAsync(webhookUrl, content);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Successfully sent event to Discord.");
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
}