using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace WinkyBot.Function;

public class WinkyBot_CommandManager
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    public WinkyBot_CommandManager(ILoggerFactory loggerFactory, HttpClient httpClient)
    {
        _logger = loggerFactory.CreateLogger<WinkyBot_CommandManager>();
        _httpClient = httpClient;
    }

    [Function("UpdateCommands")]
    public async Task Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "UpdateCommands")] HttpRequest req)
    {
        _logger.LogInformation("UpdateCommands function executed at: {executionTime}", DateTime.Now);

        var appId = Environment.GetEnvironmentVariable("DISCORD_APPLICATION_ID");
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        var url = $"https://discord.com/api/v10/applications/{appId}/commands";

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Add("Authorization", $"Bot {token}");

        var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Successfully updated commands: {content}", content);
        }
        else
        {
            _logger.LogError("Failed to update commands: {content}", content);
        };
    }
}