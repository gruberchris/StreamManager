using Microsoft.AspNetCore.Mvc;
using StreamManager.Web.Models;
using System.Text;
using System.Text.Json;

namespace StreamManager.Web.Controllers;

public class StreamsController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamsController> _logger;

    public StreamsController(IHttpClientFactory httpClientFactory, ILogger<StreamsController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        
        try
        {
            var response = await client.GetAsync("api/stream");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var streams = JsonSerializer.Deserialize<List<StreamDefinitionViewModel>>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                return View(streams ?? new List<StreamDefinitionViewModel>());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching streams");
        }

        return View(new List<StreamDefinitionViewModel>());
    }

    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateStreamViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var client = _httpClientFactory.CreateClient("ApiClient");
        
        try
        {
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await client.PostAsync("api/stream", content);
            if (response.IsSuccessStatusCode)
            {
                return RedirectToAction(nameof(Index));
            }
            
            var errorContent = await response.Content.ReadAsStringAsync();
            ModelState.AddModelError("", $"Error creating stream: {errorContent}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating stream");
            ModelState.AddModelError("", "An error occurred while creating the stream");
        }

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Deploy(Guid id)
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        
        try
        {
            var response = await client.PostAsync($"api/stream/{id}/deploy", null);
            if (response.IsSuccessStatusCode)
            {
                TempData["Message"] = "Stream deployed successfully";
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                TempData["Error"] = $"Error deploying stream: {errorContent}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deploying stream");
            TempData["Error"] = "An error occurred while deploying the stream";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Stop(Guid id)
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        
        try
        {
            var response = await client.PostAsync($"api/stream/{id}/stop", null);
            if (response.IsSuccessStatusCode)
            {
                TempData["Message"] = "Stream stopped successfully";
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                TempData["Error"] = $"Error stopping stream: {errorContent}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping stream");
            TempData["Error"] = "An error occurred while stopping the stream";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(Guid id)
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        
        try
        {
            var response = await client.DeleteAsync($"api/stream/{id}");
            if (response.IsSuccessStatusCode)
            {
                TempData["Message"] = "Stream deleted successfully";
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                TempData["Error"] = $"Error deleting stream: {errorContent}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting stream");
            TempData["Error"] = "An error occurred while deleting the stream";
        }

        return RedirectToAction(nameof(Index));
    }
}