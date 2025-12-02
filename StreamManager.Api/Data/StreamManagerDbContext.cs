using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Models;

namespace StreamManager.Api.Data;

public class StreamManagerDbContext : DbContext
{
    public StreamManagerDbContext(DbContextOptions<StreamManagerDbContext> options) : base(options)
    {
    }

    public DbSet<StreamDefinition> StreamDefinitions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StreamDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.KsqlScript).IsRequired();
            entity.Property(e => e.KsqlQueryId).HasMaxLength(255);
            entity.Property(e => e.OutputTopic).HasMaxLength(255);
            entity.Property(e => e.CreatedAt).IsRequired();
        });
    }
}