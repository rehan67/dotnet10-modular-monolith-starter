using System.Data.Common;
using System.Text.Json;
using EduCore.Contracts;
namespace EduCore.Enrollments;

public sealed class EnrollmentsModule(IDatabase database, ICourseSeats seats)
{
    public async Task<Guid> EnrollAsync(Guid courseId, Guid studentId, CancellationToken ct)
    {
        if (courseId == Guid.Empty || studentId == Guid.Empty) throw new ArgumentException("Valid IDs are required.");
        await using var connection = await database.OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var guard = Sql.Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", ("key", $"{courseId:N}:{studentId:N}"))) await guard.ExecuteNonQueryAsync(ct);
        await using (var existing = Sql.Command(connection, transaction, "SELECT id FROM enrollments.enrollment WHERE course_id=@course AND student_id=@student", ("course", courseId), ("student", studentId)))
        {
            if (await existing.ExecuteScalarAsync(ct) is Guid previous) return previous;
        }
        await seats.ReserveAsync(courseId, transaction, ct);
        var id = Guid.NewGuid(); var payload = JsonSerializer.Serialize(new EnrollmentRecorded(id, courseId, studentId));
        try
        {
            await using var command = Sql.Command(connection, transaction, "INSERT INTO enrollments.enrollment(id,course_id,student_id) VALUES(@id,@course,@student); INSERT INTO enrollments.outbox(id,payload) VALUES(@id,@payload)", ("id", id), ("course", courseId), ("student", studentId), ("payload", payload));
            await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct); return id;
        }
        catch (DbException) { await transaction.RollbackAsync(ct); throw; }
    }
}
public sealed class OutboxRelay(IDatabase database, IEnrollmentConsumer consumer)
{
    public async Task<bool> RunOnceAsync(bool crashAfterDelivery = false, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid id; string payload;
        await using (var command = Sql.Command(connection, transaction, "SELECT id,payload FROM enrollments.outbox WHERE processed_at IS NULL ORDER BY created_at,id FOR UPDATE SKIP LOCKED LIMIT 1"))
        {
            await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return false; id = reader.GetGuid(0); payload = reader.GetString(1);
        }
        var message = JsonSerializer.Deserialize<EnrollmentRecorded>(payload) ?? throw new InvalidOperationException("Invalid event.");
        // The consumer commits independently. A relay crash here can redeliver.
        await consumer.ConsumeAsync(message, ct);
        if (crashAfterDelivery) throw new InjectedCrashException();
        await using var ack = Sql.Command(connection, transaction, "UPDATE enrollments.outbox SET processed_at=CURRENT_TIMESTAMP WHERE id=@id", ("id", id));
        await ack.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct); return true;
    }
}
public sealed class InjectedCrashException : Exception { }
