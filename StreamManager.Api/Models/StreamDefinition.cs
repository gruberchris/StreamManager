namespace StreamManager.Api.Models;

public class StreamDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string SqlScript { get; set; }
    public string? JobId { get; set; }
    public string? StreamName { get; set; }
    public string? OutputTopic { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}