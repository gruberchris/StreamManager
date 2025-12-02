using Microsoft.AspNetCore.Mvc;
using StreamManager.Web.Models;
using System.Text.Json;

namespace StreamManager.Web.Controllers;

public class StreamController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamController> _logger;

    public StreamController(IHttpClientFactory httpClientFactory, ILogger<StreamController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IActionResult> Preview(Guid id)
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        
        try
        {
            var response = await client.GetAsync($"api/stream/{id}");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var stream = JsonSerializer.Deserialize<StreamDefinitionViewModel>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                if (stream != null)
                {
                    return View(stream);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching stream details");
        }

        return NotFound();
    }
}