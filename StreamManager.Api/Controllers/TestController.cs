using Microsoft.AspNetCore.Mvc;
using StreamManager.Api.Services;

namespace StreamManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TestController : ControllerBase
{
    private readonly AdHocKsqlService _ksqlService;

    public TestController(AdHocKsqlService ksqlService)
    {
        _ksqlService = ksqlService;
    }

    [HttpPost("query")]
    public async Task<ActionResult> TestQuery([FromBody] TestQueryRequest request)
    {
        var results = new List<string>();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 10 second timeout
        
        try
        {
            await foreach (var result in _ksqlService.ExecuteQueryStreamAsync(request.Ksql, cts.Token))
            {
                results.Add(result);
                if (results.Count >= 10) break; // Limit results for testing
            }
        }
        catch (OperationCanceledException ex)
        {
            return Ok(new { count = results.Count, results = results, error = "Timeout", message = ex.Message });
        }
        catch (Exception ex)
        {
            return Ok(new { count = results.Count, results = results, error = ex.GetType().Name, message = ex.Message });
        }

        return Ok(new { count = results.Count, results = results, success = true });
    }

    [HttpPost("direct")]
    public async Task<ActionResult> TestDirect([FromBody] TestQueryRequest request)
    {
        // Test direct ksqlDB connection
        var httpClient = new HttpClient();
        var payload = new
        {
            ksql = request.Ksql,
            streamsProperties = new Dictionary<string, object>
            {
                { "ksql.streams.auto.offset.reset", "earliest" }
            }
        };

        var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload);
        var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await httpClient.PostAsync("http://localhost:8088/query", httpContent, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(10).ToList();
                return Ok(new { success = true, lines = lines, fullContent = content.Length > 1000 ? content[..1000] + "..." : content });
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                return Ok(new { success = false, status = response.StatusCode, error = error });
            }
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
}

public record TestQueryRequest(string Ksql);