namespace StreamManager.Web.Models;

public class StreamDefinitionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KsqlScript { get; set; } = string.Empty;
    public string? KsqlQueryId { get; set; }
    public string? OutputTopic { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateStreamViewModel
{
    public string Name { get; set; } = string.Empty;
    public string KsqlScript { get; set; } = string.Empty;
}