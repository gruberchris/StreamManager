using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Models;

namespace StreamManager.Api.Data;

public class StreamManagerDbContext(DbContextOptions<StreamManagerDbContext> options) : DbContext(options)
{
    public DbSet<StreamDefinition> StreamDefinitions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StreamDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.SqlScript).IsRequired();
            entity.Property(e => e.JobId).HasMaxLength(255);
            entity.Property(e => e.StreamName).HasMaxLength(255);
            entity.Property(e => e.OutputTopic).HasMaxLength(255);
            entity.Property(e => e.CreatedAt).IsRequired();
        });
    }
}