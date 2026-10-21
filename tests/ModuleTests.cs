using System.Net;
using System.Net.Http.Json;
using EduCore.Contracts;
using EduCore.Enrollments;
using EduCore.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
public sealed class TestApplication : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) { builder.UseEnvironment("Testing"); builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Relay:Enabled"] = "false", ["ConnectionStrings:Courses"] = Environment.GetEnvironmentVariable("TEST_POSTGRES") ?? "Host=localhost;Port=5434;Database=educore_modular;Username=lab;Password=local-only-change-me" })); }
}
public sealed class ModuleTests : IClassFixture<TestApplication>
{
    private readonly TestApplication app; public ModuleTests(TestApplication app) { this.app = app; }
    [Fact]
    public void ModulesDependOnContractsNotEachOther()
    {
        foreach (var assembly in new[] { typeof(EduCore.Courses.CoursesModule).Assembly, typeof(EnrollmentsModule).Assembly, typeof(NotificationsModule).Assembly })
        {
            var names = assembly.GetReferencedAssemblies().Select(a => a.Name ?? ""); Assert.DoesNotContain(names, n => new[] { "Courses", "Enrollments", "Notifications" }.Contains(n));
        }
    }
    [Fact]
    public async Task CrashAfterConsumerCommitRedeliversButHasOneEffect()
    {
        using var client = app.CreateClient(); using (var drainScope = app.Services.CreateScope()) { var drain = drainScope.ServiceProvider.GetRequiredService<OutboxRelay>(); for (var i = 0; i < 1000 && await drain.RunOnceAsync(); i++) { } }
        var created = await client.PostAsJsonAsync("/courses", new { title = "Distributed systems", capacity = 10 }); created.EnsureSuccessStatusCode(); var course = await created.Content.ReadFromJsonAsync<IdResult>();
        var response = await client.PostAsJsonAsync($"/courses/{course!.Id}/enrollments", new { studentId = Guid.NewGuid() }); response.EnsureSuccessStatusCode(); var enrollment = await response.Content.ReadFromJsonAsync<IdResult>();
        using var scope = app.Services.CreateScope(); var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>(); var notifications = scope.ServiceProvider.GetRequiredService<NotificationsModule>();
        await Assert.ThrowsAsync<InjectedCrashException>(() => relay.RunOnceAsync(true));
        // Other tests may leave unrelated pending rows: drain until this event is acknowledged.
        for (var i = 0; i < 30; i++) { await relay.RunOnceAsync(); var inspected = await notifications.InspectAsync(enrollment!.Id, default); if (inspected.Deliveries >= 2) break; }
        var result = await notifications.InspectAsync(enrollment!.Id, default); Assert.Equal(2, result.Deliveries); Assert.Equal(1, result.Effects);
    }
    [Fact]
    public async Task CapacityIsAtomicAcrossConcurrentRequests()
    {
        using var client = app.CreateClient(); var created = await client.PostAsJsonAsync("/courses", new { title = "Capacity", capacity = 2 }); var course = await created.Content.ReadFromJsonAsync<IdResult>();
        var replies = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostAsJsonAsync($"/courses/{course!.Id}/enrollments", new { studentId = Guid.NewGuid() })));
        Assert.Equal(2, replies.Count(r => r.StatusCode == HttpStatusCode.OK)); Assert.Equal(8, replies.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }
    [Fact]
    public async Task FailedEnrolmentDoesNotWriteOutbox()
    {
        using var client = app.CreateClient(); var result = await client.PostAsJsonAsync($"/courses/{Guid.NewGuid()}/enrollments", new { studentId = Guid.NewGuid() }); Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }
    [Fact]
    public async Task ConcurrentDuplicateRequestsReserveOneSeatAndOneEvent()
    {
        using var client = app.CreateClient(); var created = await client.PostAsJsonAsync("/courses", new { title = "Replay", capacity = 1 }); var course = await created.Content.ReadFromJsonAsync<IdResult>(); var student = Guid.NewGuid();
        var replies = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostAsJsonAsync($"/courses/{course!.Id}/enrollments", new { studentId = student })));
        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = await Task.WhenAll(replies.Select(r => r.Content.ReadFromJsonAsync<IdResult>())); Assert.Single(ids.Select(r => r!.Id).Distinct());
        using var scope = app.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<IDatabase>();
        await using var connection = await database.OpenAsync(default);
        await using var count = Sql.Command(connection, null, "SELECT (SELECT reserved FROM courses.catalog WHERE id=@course),(SELECT COUNT(*) FROM enrollments.enrollment WHERE course_id=@course),(SELECT COUNT(*) FROM enrollments.outbox WHERE payload::jsonb->>'CourseId'=@courseText)", ("course", course!.Id), ("courseText", course.Id.ToString()));
        await using var reader = await count.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt32(0)); Assert.Equal(1, reader.GetInt64(1)); Assert.Equal(1, reader.GetInt64(2));
    }
    [Fact]
    public async Task FailureAfterReservationRollsBackTheSeat()
    {
        using var client = app.CreateClient(); var created = await client.PostAsJsonAsync("/courses", new { title = "Rollback", capacity = 1 }); var course = await created.Content.ReadFromJsonAsync<IdResult>();
        using var scope = app.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<IDatabase>(); var seats = scope.ServiceProvider.GetRequiredService<ICourseSeats>();
        var module = new EnrollmentsModule(database, new FailingSeats(seats));
        await Assert.ThrowsAsync<InjectedCrashException>(() => module.EnrollAsync(course!.Id, Guid.NewGuid(), default));
        await using var connection = await database.OpenAsync(default);
        await using var count = Sql.Command(connection, null, "SELECT reserved FROM courses.catalog WHERE id=@id", ("id", course!.Id)); Assert.Equal(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
        // A subsequent successful enrolment can use the rolled-back seat.
        var enrolled = await client.PostAsJsonAsync($"/courses/{course.Id}/enrollments", new { studentId = Guid.NewGuid() }); Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
    }
    private sealed class FailingSeats(ICourseSeats inner) : ICourseSeats
    {
        public async Task ReserveAsync(Guid courseId, System.Data.Common.DbTransaction transaction, CancellationToken ct) { await inner.ReserveAsync(courseId, transaction, ct); throw new InjectedCrashException(); }
    }
    private record IdResult(Guid Id);
}
