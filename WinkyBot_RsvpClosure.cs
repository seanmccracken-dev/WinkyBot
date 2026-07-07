using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace WinkyBot.Functions;

public class WinkyBot_RsvpClosure
{
    private readonly ILogger _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly IHttpClientFactory _httpClientFactory;

    public WinkyBot_RsvpClosure(ILoggerFactory loggerFactory, CosmosClient cosmosClient, IHttpClientFactory httpClientFactory)
    {
        _logger = loggerFactory.CreateLogger<WinkyBot_RsvpClosure>();
        _cosmosClient = cosmosClient;
        _httpClientFactory = httpClientFactory;
    }

    [Function("WinkyBot_RsvpClosure")]
    public async Task Run([TimerTrigger("0 0 20 * * 5")] TimerInfo timerInfo)
    {
        _logger.LogInformation("RSVP closure timer executed at: {executionTime}", DateTime.UtcNow);

        var container = _cosmosClient.GetContainer("WinkyBot_DB", "Events");
        var nowUtc = DateTime.UtcNow;

        var query = new QueryDefinition(@"
            SELECT TOP 1 *
            FROM c
            WHERE IS_DEFINED(c.discordChannelId)
              AND IS_DEFINED(c.discordMessageId)
              AND c.eventDateTimeUtc <= @nowUtc
            ORDER BY c.eventDateTimeUtc DESC")
            .WithParameter("@nowUtc", nowUtc);

        WinkyEvent? winkyEvent = null;
        using (var iterator = container.GetItemQueryIterator<WinkyEvent>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 }))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                winkyEvent = response.Resource.FirstOrDefault();
                if (winkyEvent is not null)
                {
                    break;
                }
            }
        }

        if (winkyEvent is null)
        {
            _logger.LogInformation("No event found to close RSVP buttons for at this time.");
            return;
        }

        if (string.IsNullOrWhiteSpace(winkyEvent.DiscordChannelId) || string.IsNullOrWhiteSpace(winkyEvent.DiscordMessageId))
        {
            _logger.LogWarning("Event {eventId} has missing Discord message identifiers.", winkyEvent.id);
            return;
        }

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("Missing DISCORD_TOKEN configuration.");
            return;
        }

        var payload = WinkyBot_EventCreation.BuildDiscordPayloadWithoutButtons(winkyEvent, WinkyBot_EventCreation.DefaultEventDescription);
        var json = JsonSerializer.Serialize(payload);

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"https://discord.com/api/v10/channels/{winkyEvent.DiscordChannelId}/messages/{winkyEvent.DiscordMessageId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

        var responseMessage = await client.SendAsync(request);
        if (responseMessage.IsSuccessStatusCode)
        {
            _logger.LogInformation("Successfully removed RSVP buttons from message {messageId}.", winkyEvent.DiscordMessageId);
            return;
        }

        var responseBody = await responseMessage.Content.ReadAsStringAsync();
        _logger.LogError(
            "Failed to remove RSVP buttons for event {eventId}. Status: {status}. Body: {body}",
            winkyEvent.id,
            responseMessage.StatusCode,
            responseBody);
    }
}
