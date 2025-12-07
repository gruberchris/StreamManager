using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Data;
using StreamManager.Api.Models;
using StreamManager.Api.Services;

namespace StreamManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamController(
    StreamManagerDbContext context,
    IStreamQueryEngine engine,
    IQueryValidator validator,
    QueryRateLimiter rateLimiter,
    ILogger<StreamController> logger)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<StreamDefinition>>> GetStreams()
    {
        return await context.StreamDefinitions.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<StreamDefinition>> GetStream(Guid id)
    {
        var stream = await context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        return stream;
    }

    [HttpPost]
    public async Task<ActionResult<StreamDefinition>> CreateStream(CreateStreamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.SqlScript))
            return BadRequest("Name and SqlScript are required");

        // Validate the query
        var validation = validator.ValidatePersistentQuery(request.SqlScript, request.Name);
        if (!validation.IsValid)
        {
            logger.LogWarning("Query validation failed for stream {Name}: {Error}", request.Name, validation.ErrorMessage);
            return BadRequest(new { error = validation.ErrorMessage });
        }

        // Log warnings
        if (validation.Warnings.Any())
        {
            logger.LogInformation("Query warnings for stream {Name}: {Warnings}", 
                request.Name, string.Join("; ", validation.Warnings));
        }

        var stream = new StreamDefinition
        {
            Name = request.Name.Trim(),
            SqlScript = request.SqlScript.Trim(),
            IsActive = false
        };

        context.StreamDefinitions.Add(stream);
        await context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetStream), new { id = stream.Id }, stream);
    }

    [HttpPost("{id}/deploy")]
    public async Task<ActionResult> DeployStream(Guid id)
    {
        var stream = await context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        // Check rate limits (use stream owner ID in production)
        var userId = "system"; // Replace it with the actual user ID from authentication
        var rateLimitResult = rateLimiter.CanCreatePersistentQuery(userId);
        if (!rateLimitResult.IsAllowed)
        {
            logger.LogWarning("Rate limit exceeded for deployment by user {UserId}: {Message}", userId, rateLimitResult.ErrorMessage);
            return BadRequest(new { error = rateLimitResult.ErrorMessage });
        }

        try
        {
            var result = await engine.DeployPersistentQueryAsync(
                stream.Name,
                stream.SqlScript,
                properties: null,
                cancellationToken: default);
            
            if (result.Success)
            {
                stream.JobId = result.JobId;
                stream.StreamName = result.StreamName;
                stream.OutputTopic = result.OutputTopic;
                stream.IsActive = true;
                
                await context.SaveChangesAsync();
                
                logger.LogInformation("Successfully deployed stream {StreamId} with job ID {JobId} and topic {Topic}", 
                    id, result.JobId, result.OutputTopic);
                return Ok(new { JobId = result.JobId, OutputTopic = result.OutputTopic });
            }
            else
            {
                logger.LogError("Failed to deploy stream {StreamId}: {Error}", id, result.ErrorMessage);
                return BadRequest($"Failed to deploy stream: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deploying stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("{id}/stop")]
    public async Task<ActionResult> StopStream(Guid id)
    {
        var stream = await context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        if (!stream.IsActive)
            return BadRequest("Stream is not active");

        try
        {
            if (string.IsNullOrWhiteSpace(stream.JobId))
                return BadRequest("Stream has no job ID to terminate");
            
            await engine.StopPersistentQueryAsync(stream.JobId, cancellationToken: default);
            
            stream.IsActive = false;
            stream.JobId = null;
            await context.SaveChangesAsync();
            
            logger.LogInformation("Successfully stopped stream {StreamId}", id);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error stopping stream {StreamId}", id);
            return BadRequest($"Failed to stop stream: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateStream(Guid id, UpdateStreamRequest request)
    {
        var stream = await context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.SqlScript))
            return BadRequest("Name and SqlScript are required");

        try
        {
            // If stream is active, stop and delete it first
            if (stream.IsActive && !string.IsNullOrWhiteSpace(stream.JobId))
            {
                await engine.DeletePersistentQueryAsync(
                    stream.JobId,
                    stream.StreamName,
                    cancellationToken: default);
            }

            // Update the stream definition
            stream.Name = request.Name.Trim();
            stream.SqlScript = request.SqlScript.Trim();
            stream.IsActive = false;
            stream.JobId = null;
            stream.StreamName = null;
            stream.OutputTopic = null;

            await context.SaveChangesAsync();
            
            logger.LogInformation("Successfully updated stream {StreamId}", id);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteStream(Guid id)
    {
        var stream = await context.StreamDefinitions.FindAsync(id);
        if (stream == null)
            return NotFound();

        // Release the rate limit slot
        var userId = "system"; // Replace with actual user ID
        rateLimiter.ReleasePersistentQuery(userId);

        try
        {
            var warnings = new List<string>();
            
            // Step 1: Delete from engine (terminate and drop)
            if (stream.IsActive || !string.IsNullOrWhiteSpace(stream.JobId) || !string.IsNullOrWhiteSpace(stream.StreamName))
            {
                try
                {
                    await engine.DeletePersistentQueryAsync(
                        stream.JobId ?? "",
                        stream.StreamName,
                        cancellationToken: default);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fully delete stream resources for {StreamId}", id);
                    warnings.Add($"Engine cleanup: {ex.Message}");
                }
            }

            // Step 2: Remove from database (always do this)
            context.StreamDefinitions.Remove(stream);
            await context.SaveChangesAsync();
            
            logger.LogInformation("Successfully deleted stream {StreamId} from database", id);
            
            // Return success, but include warnings if there were issues
            if (warnings.Count > 0)
            {
                return Ok(new { 
                    message = "Stream deleted from database, but some cleanup operations failed", 
                    warnings = warnings 
                });
            }
            
            return Ok(new { message = "Stream fully deleted" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting stream {StreamId}", id);
            return StatusCode(500, "Internal server error");
        }
    }

}

public record CreateStreamRequest(string Name, string SqlScript);
public record UpdateStreamRequest(string Name, string SqlScript);