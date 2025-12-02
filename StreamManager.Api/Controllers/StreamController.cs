using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Data;
using StreamManager.Api.Models;
using System.Text;
using System.Text.Json;

namespace StreamManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamController : ControllerBase
{
    private readonly StreamManagerDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly ILogger<StreamController> _logger;
    private readonly string _ksqlDbUrl;

    public StreamController(StreamManagerDbContext context, HttpClient httpClient, ILogger<StreamController> logger, IConfiguration configuration)
    {
        _context = context;
        _httpClient = httpClient;
        _logger = logger;
        _ksqlDbUrl = configuration.GetConnectionString("KsqlDb") ?? "http://localhost:8088";
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<StreamDefinition>>> GetStreams()
    {
        return await _context.StreamDefinitions.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<StreamDefinition>> GetStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        return stream;
    }

    [HttpPost]
    public async Task<ActionResult<StreamDefinition>> CreateStream(CreateStreamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.KsqlScript))
            return BadRequest("Name and KsqlScript are required");

        var stream = new StreamDefinition
        {
            Name = request.Name.Trim(),
            KsqlScript = request.KsqlScript.Trim(),
            IsActive = false
        };

        _context.StreamDefinitions.Add(stream);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetStream), new { id = stream.Id }, stream);
    }

    [HttpPost("{id}/deploy")]
    public async Task<ActionResult> DeployStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        try
        {
            var safeName = GenerateSafeName(stream.Name);
            var createStreamSql = $"CREATE STREAM {safeName} AS {stream.KsqlScript};";
            
            var payload = new { ksql = createStreamSql };
            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
            
            if (response.IsSuccessStatusCode)
            {
                var queryId = $"CSAS_{safeName}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                stream.KsqlQueryId = queryId;
                stream.OutputTopic = safeName.ToLowerInvariant();
                stream.IsActive = true;
                
                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Successfully deployed stream {StreamId} with query ID {QueryId}", id, queryId);
                return Ok(new { QueryId = queryId, OutputTopic = stream.OutputTopic });
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to deploy stream {StreamId}: {Error}", id, errorContent);
                return BadRequest($"Failed to deploy stream: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deploying stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("{id}/stop")]
    public async Task<ActionResult> StopStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        if (!stream.IsActive)
            return BadRequest("Stream is not active");

        try
        {
            if (string.IsNullOrWhiteSpace(stream.KsqlQueryId))
                return BadRequest("Stream has no query ID to terminate");
            
            var terminateSql = $"TERMINATE {stream.KsqlQueryId};";
            var payload = new { ksql = terminateSql };
            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
            
            if (response.IsSuccessStatusCode)
            {
                stream.IsActive = false;
                stream.KsqlQueryId = null; // Clear since it's terminated
                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Successfully stopped stream {StreamId} with query ID {QueryId}", id, stream.KsqlQueryId);
                return Ok();
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to stop stream {StreamId}: {Error}", id, errorContent);
                return BadRequest($"Failed to stop stream: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateStream(Guid id, UpdateStreamRequest request)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.KsqlScript))
            return BadRequest("Name and KsqlScript are required");

        try
        {
            // If stream is active, stop it first
            if (stream.IsActive && !string.IsNullOrWhiteSpace(stream.KsqlQueryId))
            {
                var terminateSql = $"TERMINATE {stream.KsqlQueryId};";
                var payload = new { ksql = terminateSql };
                var jsonContent = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
                
                // Drop the stream if it exists
                var safeName = GenerateSafeName(stream.Name);
                var dropSql = $"DROP STREAM IF EXISTS {safeName};";
                var dropPayload = new { ksql = dropSql };
                var dropJsonContent = JsonSerializer.Serialize(dropPayload);
                var dropHttpContent = new StringContent(dropJsonContent, Encoding.UTF8, "application/json");
                
                await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", dropHttpContent);
            }

            // Update the stream definition
            stream.Name = request.Name.Trim();
            stream.KsqlScript = request.KsqlScript.Trim();
            stream.IsActive = false;
            stream.KsqlQueryId = null;
            stream.OutputTopic = null;

            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Successfully updated stream {StreamId}", id);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteStream(Guid id)
    {
        var stream = await _context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        try
        {
            // Stop the stream if active
            if (stream.IsActive && !string.IsNullOrWhiteSpace(stream.KsqlQueryId))
            {
                var terminateSql = $"TERMINATE {stream.KsqlQueryId};";
                var payload = new { ksql = terminateSql };
                var jsonContent = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                await _httpClient.PostAsync($"{_ksqlDbUrl}/ksql", httpContent);
            }

            _context.StreamDefinitions.Remove(stream);
            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Successfully deleted stream {StreamId}", id);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    private static string GenerateSafeName(string name)
    {
        // Generate a safe name for ksqlDB (alphanumeric + underscore, starts with letter)
        var safeName = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(safeName) || !char.IsLetter(safeName[0]))
            safeName = "STREAM_" + safeName;
        
        return $"VIEW_{safeName.ToUpperInvariant()}_{Guid.NewGuid():N}";
    }
}

public record CreateStreamRequest(string Name, string KsqlScript);
public record UpdateStreamRequest(string Name, string KsqlScript);