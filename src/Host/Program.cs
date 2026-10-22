using EduCore.Contracts;
using EduCore.Courses;
using EduCore.Enrollments;
using EduCore.Host;
using EduCore.Notifications;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
    NpgsqlDataSource.Create(
        services.GetRequiredService<IConfiguration>().GetConnectionString("Courses")
            ?? "Host=localhost;Port=5434;Database=educore_modular;Username=lab;Password=local-only-change-me"
    )
);
builder.Services.AddSingleton<PostgresDatabase>();
builder.Services.AddSingleton<IDatabase>(sp => sp.GetRequiredService<PostgresDatabase>());
builder.Services.AddScoped<CoursesModule>();
builder.Services.AddScoped<ICourseSeats>(sp => sp.GetRequiredService<CoursesModule>());
builder.Services.AddScoped<EnrollmentsModule>();
builder.Services.AddScoped<OutboxRelay>();
builder.Services.AddScoped<NotificationsModule>();
builder.Services.AddScoped<IEnrollmentConsumer>(sp => sp.GetRequiredService<NotificationsModule>());
builder.Services.AddHostedService<RelayWorker>();
var app = builder.Build();
await app.Services.GetRequiredService<PostgresDatabase>().InitialiseAsync();
app.UseExceptionHandler(error =>
    error.Run(async context =>
    {
        var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var status = ex switch
        {
            ArgumentException => 400,
            KeyNotFoundException => 404,
            InvalidOperationException => 409,
            _ => 500,
        };
        await Results
            .Problem(
                statusCode: status,
                title: status == 500 ? "Request could not be completed" : "Request rejected"
            )
            .ExecuteAsync(context);
    })
);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost(
    "/courses",
    async (CreateRequest request, CoursesModule module, CancellationToken ct) =>
    {
        var id = await module.CreateAsync(request.Title, request.Capacity, ct);
        return Results.Created($"/courses/{id}", new { id });
    }
);
app.MapPost(
    "/courses/{id:guid}/enrollments",
    async (Guid id, EnrollRequest request, EnrollmentsModule module, CancellationToken ct) =>
        Results.Ok(new { id = await module.EnrollAsync(id, request.StudentId, ct) })
);
app.MapGet(
    "/notifications/{eventId:guid}",
    async (Guid eventId, NotificationsModule module, CancellationToken ct) =>
    {
        var result = await module.InspectAsync(eventId, ct);
        return Results.Ok(new { result.Deliveries, result.Effects });
    }
);
app.Run();

public record CreateRequest(string? Title, int Capacity);

public record EnrollRequest(Guid StudentId);

public partial class Program { }

public sealed class RelayWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<RelayWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (config["Relay:Enabled"] == "false")
            return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OutboxRelay>().RunOnceAsync(ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Relay attempt failed: {ErrorType}", exception.GetType().Name);
            }
            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
