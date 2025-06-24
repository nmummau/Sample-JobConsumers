using JobService.Components;
using JobService.Service;
using JobService.Service.Components;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSwag;
using ResQueue;
using ResQueue.Enums;
using Serilog;
using Serilog.Events;
using System.Reflection;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("MassTransit", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Fatal)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Fatal)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddControllers();
builder.Services.AddOpenApiDocument(cfg => cfg.PostProcess = d =>
{
    d.Info.Title = "Job Consumer Sample";
    d.Info.Contact = new OpenApiContact
    {
        Name = "Job Consumer Sample using MassTransit",
        Email = "support@masstransit.io"
    };
});

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var connectionString = builder.Configuration.GetConnectionString("JobService");

builder.Services.AddOptions<SqlTransportOptions>()
    .Configure(options =>
    {
        options.Host = "localhost,48364";
        options.Database = "WES_001";
        //options.Role = "transport";
        options.Schema = "transport";
        options.Username = "sa"; // the application-level credentials to use
        options.Password = "Password!";
        options.AdminUsername = "sa"; // the admin credentials to create the tables, etc.
        options.AdminPassword = "Password!";
    });

builder.Services.AddSqlServerMigrationHostedService();

// Add web-based dashboard
builder.Services.AddResQueue(opt =>
{
    opt.SqlEngine = ResQueueSqlEngine.SqlServer;
});
builder.Services.AddResQueueMigrationsHostedService();

builder.Services.AddDbContext<JobServiceSagaDbContext>(optionsBuilder =>
{
    optionsBuilder.UseSqlServer(connectionString, m =>
    {
        m.MigrationsAssembly(Assembly.GetExecutingAssembly().GetName().Name);
        m.MigrationsHistoryTable($"__{nameof(JobServiceSagaDbContext)}");
        
        m.EnableRetryOnFailure();
    });

    optionsBuilder.ConfigureWarnings(warnings =>
    {
        warnings.Ignore(RelationalEventId.PendingModelChangesWarning);
    });
});

builder.Services.AddHostedService<MigrationHostedService<JobServiceSagaDbContext>>();

builder.Services.AddMassTransit(x =>
{
    x.AddSqlMessageScheduler();
    
    x.AddConsumer<ConvertVideoJobConsumer, ConvertVideoJobConsumerDefinition>()
        .Endpoint(e => e.Name = "convert-job-queue");

    x.AddConsumer<TrackVideoConvertedConsumer>();

    x.TryAddJobDistributionStrategy<DataCenterJobDistributionStrategy>();

    x.AddConsumer<MaintenanceConsumer>();

    x.SetJobConsumerOptions();
    x.AddJobSagaStateMachines(options => options.FinalizeCompleted = false)
        .SetPartitionedReceiveMode()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<JobServiceSagaDbContext>();
            r.UseSqlServer();
        });

    x.SetKebabCaseEndpointNameFormatter();

    x.UsingSqlServer((context, cfg) =>
    {
        cfg.UseSqlMessageScheduler();
        cfg.UseJobSagaPartitionKeyFormatters();

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddOptions<MassTransitHostOptions>()
    .Configure(options =>
    {
        options.WaitUntilStarted = true;
        options.StartTimeout = TimeSpan.FromMinutes(1);
        options.StopTimeout = TimeSpan.FromMinutes(1);
    });

builder.Services.AddOptions<HostOptions>()
    .Configure(options => options.ShutdownTimeout = TimeSpan.FromMinutes(1));

builder.Services.AddHostedService<RecurringJobConfigurationService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();

app.UseOpenApi();
app.UseSwaggerUi();

app.UseRouting();
app.UseAuthorization();

// Bind dashboard to the /resqueue route
app.UseResQueue();

static Task HealthCheckResponseWriter(HttpContext context, HealthReport result)
{
    context.Response.ContentType = "application/json";

    return context.Response.WriteAsync(result.ToJsonString());
}

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter
});

app.MapHealthChecks("/health/live", new HealthCheckOptions { ResponseWriter = HealthCheckResponseWriter });

app.MapControllers();

await app.RunAsync();
