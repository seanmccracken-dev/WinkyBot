using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace WinkyBot.Functions;

public class WinkyBot_InteractionHandler
{
    private const string DefaultEventDescription = "Join us for a night of fun and games!";

    private readonly ILogger _logger;
    private readonly CosmosClient _cosmosClient;
    private readonly IHttpClientFactory _httpClientFactory;

    public WinkyBot_InteractionHandler(ILoggerFactory loggerFactory, CosmosClient cosmosClient, IHttpClientFactory httpClientFactory)
    {
        _logger = loggerFactory.CreateLogger<WinkyBot_InteractionHandler>();
        _cosmosClient = cosmosClient;
        _httpClientFactory = httpClientFactory;
    }

    [Function("WinkyBot_InteractionHandler")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "discord/interactions")] HttpRequestData req)
    {
        var body = await ReadBodyAsync(req);

        if (!TryGetHeader(req, "X-Signature-Ed25519", out var signature) ||
            !TryGetHeader(req, "X-Signature-Timestamp", out var timestamp) ||
            !DiscordSecurity.VerifySignature(signature, timestamp, body))
        {
            _logger.LogWarning("Rejected Discord interaction due to failed signature validation.");
            return await CreateJsonResponse(req, HttpStatusCode.Unauthorized, new { error = "invalid request signature" });
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var interactionType = root.GetProperty("type").GetInt32();
        if (interactionType == 1)
        {
            return await CreateJsonResponse(req, HttpStatusCode.OK, new { type = 1 });
        }

        if (interactionType != 3)
        {
            _logger.LogInformation("Ignoring unsupported Discord interaction type {interactionType}", interactionType);
            return await CreateJsonResponse(req, HttpStatusCode.OK, new { type = 6 });
        }

        if (!TryExtractCustomId(root, out var customId) || !TryParseCustomId(customId, out var eventId, out var responseType))
        {
            _logger.LogWarning("Invalid custom_id in interaction payload.");
            return await CreateJsonResponse(req, HttpStatusCode.OK, CreateDeferredUpdateResponse());
        }

        if (!TryExtractUserId(root, out var userId))
        {
            _logger.LogWarning("Could not extract user id from interaction payload.");
            return await CreateJsonResponse(req, HttpStatusCode.OK, CreateDeferredUpdateResponse());
        }

        WinkyEvent? winkyEvent;
        var container = _cosmosClient.GetContainer("WinkyBot_DB", "Events");
        try
        {
            var readResponse = await container.ReadItemAsync<WinkyEvent>(eventId, new PartitionKey(eventId));
            winkyEvent = readResponse.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Event {eventId} was not found by id. Attempting lookup by Discord message id.", eventId);

            if (!TryExtractInteractionMessageId(root, out var messageId))
            {
                return await CreateJsonResponse(req, HttpStatusCode.OK, CreateDeferredUpdateResponse());
            }

            winkyEvent = await TryFindEventByMessageIdAsync(container, messageId);
        }

        if (winkyEvent is null)
        {
            return await CreateJsonResponse(req, HttpStatusCode.OK, CreateDeferredUpdateResponse());
        }

        ApplyRsvpSelection(winkyEvent, userId, responseType);

        try
        {
            var upsertResponse = await container.UpsertItemAsync(winkyEvent, new PartitionKey(winkyEvent.id));
            _logger.LogInformation(
                "RSVP updated in Cosmos DB for event {eventId}. User {userId} -> {responseType}. Status: {statusCode}",
                winkyEvent.id,
                userId,
                responseType,
                upsertResponse.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist RSVP changes for event {eventId}", eventId);
            return await CreateJsonResponse(req, HttpStatusCode.OK, CreateDeferredUpdateResponse());
        }

        if (!string.IsNullOrWhiteSpace(winkyEvent.DiscordChannelId) && !string.IsNullOrWhiteSpace(winkyEvent.DiscordMessageId))
        {
            await UpdateDiscordMessage(winkyEvent);
        }
        else
        {
            _logger.LogWarning("Event {eventId} is missing Discord message references.", eventId);
        }

        return await CreateJsonResponse(req, HttpStatusCode.OK, CreateDeferredUpdateResponse());
    }

    private async Task UpdateDiscordMessage(WinkyEvent winkyEvent)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(winkyEvent.DiscordChannelId) || string.IsNullOrWhiteSpace(winkyEvent.DiscordMessageId))
        {
            _logger.LogWarning("Cannot update Discord message because required data is missing.");
            return;
        }

        var payload = WinkyBot_EventCreation.BuildDiscordPayload(winkyEvent, DefaultEventDescription);
        var json = JsonSerializer.Serialize(payload);

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"https://discord.com/api/v10/channels/{winkyEvent.DiscordChannelId}/messages/{winkyEvent.DiscordMessageId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to update Discord message for event {eventId}. Status: {status}. Body: {body}",
                winkyEvent.id, response.StatusCode, responseBody);
        }
    }

    private static void ApplyRsvpSelection(WinkyEvent winkyEvent, string userId, string responseType)
    {
        var attending = new HashSet<string>(winkyEvent.Attending.Where(value => !string.IsNullOrWhiteSpace(value)));
        var tentative = new HashSet<string>(winkyEvent.Tentative.Where(value => !string.IsNullOrWhiteSpace(value)));
        var late = new HashSet<string>(winkyEvent.Late.Where(value => !string.IsNullOrWhiteSpace(value)));
        var absent = new HashSet<string>(winkyEvent.Absent.Where(value => !string.IsNullOrWhiteSpace(value)));

        attending.Remove(userId);
        tentative.Remove(userId);
        late.Remove(userId);
        absent.Remove(userId);

        switch (responseType)
        {
            case "attending":
                attending.Add(userId);
                break;
            case "tentative":
                tentative.Add(userId);
                break;
            case "late":
                late.Add(userId);
                break;
            case "absent":
                absent.Add(userId);
                break;
        }

        winkyEvent.Attending = attending.ToArray();
        winkyEvent.Tentative = tentative.ToArray();
        winkyEvent.Late = late.ToArray();
        winkyEvent.Absent = absent.ToArray();
    }

    private static bool TryExtractCustomId(JsonElement root, out string customId)
    {
        customId = string.Empty;
        if (!root.TryGetProperty("data", out var dataElement) || !dataElement.TryGetProperty("custom_id", out var customIdElement))
        {
            return false;
        }

        customId = customIdElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(customId);
    }

    private static bool TryExtractUserId(JsonElement root, out string userId)
    {
        userId = string.Empty;

        if (root.TryGetProperty("member", out var memberElement) &&
            memberElement.TryGetProperty("user", out var memberUserElement) &&
            memberUserElement.TryGetProperty("id", out var memberUserIdElement))
        {
            userId = memberUserIdElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(userId);
        }

        if (root.TryGetProperty("user", out var userElement) &&
            userElement.TryGetProperty("id", out var userIdElement))
        {
            userId = userIdElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(userId);
        }

        return false;
    }

    private static bool TryExtractInteractionMessageId(JsonElement root, out string messageId)
    {
        messageId = string.Empty;

        if (!root.TryGetProperty("message", out var messageElement) ||
            !messageElement.TryGetProperty("id", out var messageIdElement))
        {
            return false;
        }

        messageId = messageIdElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(messageId);
    }

    private static bool TryParseCustomId(string customId, out string eventId, out string responseType)
    {
        eventId = string.Empty;
        responseType = string.Empty;

        var parts = customId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], "event", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        eventId = parts[1];
        responseType = parts[2].ToLowerInvariant();

        return responseType is "attending" or "tentative" or "late" or "absent";
    }

    private static bool TryGetHeader(HttpRequestData req, string headerName, out string value)
    {
        value = string.Empty;
        if (!req.Headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        value = values.FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<string> ReadBodyAsync(HttpRequestData req)
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static object CreateDeferredUpdateResponse()
    {
        return new
        {
            type = 6
        };
    }

    private static async Task<HttpResponseData> CreateJsonResponse(HttpRequestData req, HttpStatusCode statusCode, object payload)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json");
        var json = JsonSerializer.Serialize(payload);
        await response.WriteStringAsync(json);
        return response;
    }

    private static async Task<WinkyEvent?> TryFindEventByMessageIdAsync(Container container, string messageId)
    {
        var query = new QueryDefinition("SELECT TOP 1 * FROM c WHERE c.discordMessageId = @messageId")
            .WithParameter("@messageId", messageId);

        using var iterator = container.GetItemQueryIterator<WinkyEvent>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var match = response.Resource.FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
