using Microsoft.EntityFrameworkCore;
using StreamManager.Api.Configuration;
using StreamManager.Api.Data;
using StreamManager.Api.Services;
using StreamManager.Api.Services.Engines;
using StreamManager.Api.Services.Validators;
using StreamManager.Api.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration
builder.Services.Configure<ResourceLimitsOptions>(
    builder.Configuration.GetSection(ResourceLimitsOptions.SectionName));

// Bind and register stream engine configuration
var engineOptions = builder.Configuration
    .GetSection(StreamEngineOptions.SectionName)
    .Get<StreamEngineOptions>() ?? new StreamEngineOptions();

builder.Services.AddSingleton(engineOptions);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowWebApp", policy =>
    {
        policy.WithOrigins("https://localhost:7122", "http://localhost:5201") // Web app URLs
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Configure SignalR
builder.Services.AddSignalR();

// Configure Entity Framework
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Host=localhost;Database=stream_manager_db;Username=admin;Password=password";
builder.Services.AddDbContext<StreamManagerDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure HttpClient and register stream query engine and validator based on provider
if (engineOptions.Provider == StreamEngineProvider.KsqlDb)
{
    builder.Services.AddHttpClient<IStreamQueryEngine, KsqlDbEngine>(client =>
    {
        client.DefaultRequestVersion = new Version(2, 0);
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton<IQueryValidator, KsqlDbQueryValidator>();
}
else if (engineOptions.Provider == StreamEngineProvider.Flink)
{
    builder.Services.AddHttpClient<IStreamQueryEngine, FlinkEngine>(client =>
    {
        client.DefaultRequestVersion = new Version(2, 0);
        client.Timeout = TimeSpan.FromMinutes(30);
    });
    builder.Services.AddSingleton<IQueryValidator, FlinkQueryValidator>();
}
else
{
    throw new InvalidOperationException($"Unknown stream engine provider: {engineOptions.Provider}");
}

// Register custom services
builder.Services.AddSingleton<QueryRateLimiter>();
builder.Services.AddHostedService<TopicProxyService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseDeveloperExceptionPage();
}

// Apply database migrations
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<StreamManagerDbContext>();
    try
    {
        await context.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database");
    }
}

app.UseHttpsRedirection();
app.UseCors("AllowWebApp");

app.UseRouting();

app.MapControllers();
app.MapHub<StreamHub>("/hub/stream");

app.Run();
