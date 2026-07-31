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
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo)
    {
        _logger.LogInformation("RSVP closure timer executed at: {executionTime}", DateTime.UtcNow);

        var container = _cosmosClient.GetContainer("WinkyBot_DB", "Events");
        var nowUtc = DateTime.UtcNow;

        var query = new QueryDefinition(@"
            SELECT *
            FROM c
                        WHERE (IS_DEFINED(c.discordChannelId) OR IS_DEFINED(c.DiscordChannelId))
                            AND (IS_DEFINED(c.discordMessageId) OR IS_DEFINED(c.DiscordMessageId))
                            AND (
                                        (NOT IS_DEFINED(c.rsvpClosed) AND NOT IS_DEFINED(c.RsvpClosed))
                                        OR c.rsvpClosed = false
                                        OR c.RsvpClosed = false
                                    )
            ORDER BY c._ts ASC");

        var candidates = new List<WinkyEvent>();
        using (var iterator = container.GetItemQueryIterator<WinkyEvent>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 100 }))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                candidates.AddRange(response.Resource);
            }
        }

        var dueEvents = candidates
            .Where(candidate =>
            {
                var eventTimeUtc = candidate.GetEffectiveEventDateTimeUtc();
                return eventTimeUtc.HasValue && eventTimeUtc.Value <= nowUtc;
            })
            .OrderBy(candidate => candidate.GetEffectiveEventDateTimeUtc())
            .ToList();

        _logger.LogInformation(
            "RSVP closure candidate scan complete. Candidates: {candidateCount}, Due: {dueCount}, NowUtc: {nowUtc}",
            candidates.Count,
            dueEvents.Count,
            nowUtc);

        if (dueEvents.Count == 0)
        {
            _logger.LogInformation("No event found to close RSVP buttons for at this time.");
            return;
        }

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("Missing DISCORD_TOKEN configuration.");
            return;
        }

        var client = _httpClientFactory.CreateClient();
        var closedCount = 0;
        foreach (var winkyEvent in dueEvents)
        {
            if (string.IsNullOrWhiteSpace(winkyEvent.DiscordChannelId) || string.IsNullOrWhiteSpace(winkyEvent.DiscordMessageId))
            {
                _logger.LogWarning("Event {eventId} has missing Discord message identifiers.", winkyEvent.id);
                continue;
            }

            var payload = WinkyBot_EventCreation.BuildDiscordPayloadWithoutButtons(winkyEvent, WinkyBot_EventCreation.DefaultEventDescription);
            var json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"https://discord.com/api/v10/channels/{winkyEvent.DiscordChannelId}/messages/{winkyEvent.DiscordMessageId}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

            var responseMessage = await client.SendAsync(request);
            if (!responseMessage.IsSuccessStatusCode)
            {
                var responseBody = await responseMessage.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to remove RSVP buttons for event {eventId}. Status: {status}. Body: {body}",
                    winkyEvent.id,
                    responseMessage.StatusCode,
                    responseBody);
                continue;
            }

            winkyEvent.RsvpClosed = true;
            await container.UpsertItemAsync(winkyEvent, new PartitionKey(winkyEvent.id));
            closedCount++;
            _logger.LogInformation("Successfully removed RSVP buttons from message {messageId}.", winkyEvent.DiscordMessageId);
        }

        _logger.LogInformation("RSVP closure completed. Closed events this run: {closedCount}", closedCount);
    }
}
