using EduCore.Contracts;

namespace EduCore.Notifications;

public sealed class NotificationsModule(IDatabase database) : IEnrollmentConsumer
{
    public async Task ConsumeAsync(EnrollmentRecorded message, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (
            var delivery = Sql.Command(
                connection,
                transaction,
                "INSERT INTO notifications.delivery_log(id,event_id) VALUES(@id,@event)",
                ("id", Guid.NewGuid()),
                ("event", message.Id)
            )
        )
            await delivery.ExecuteNonQueryAsync(ct);
        await using var inbox = Sql.Command(
            connection,
            transaction,
            "INSERT INTO notifications.inbox(event_id) VALUES(@id) ON CONFLICT DO NOTHING",
            ("id", message.Id)
        );
        if (await inbox.ExecuteNonQueryAsync(ct) == 1)
        {
            await using var effect = Sql.Command(
                connection,
                transaction,
                "INSERT INTO notifications.mailbox(event_id,student_id) VALUES(@id,@student)",
                ("id", message.Id),
                ("student", message.StudentId)
            );
            await effect.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<(long Deliveries, long Effects)> InspectAsync(
        Guid eventId,
        CancellationToken ct
    )
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = Sql.Command(
            connection,
            null,
            "SELECT (SELECT COUNT(*) FROM notifications.delivery_log WHERE event_id=@id),(SELECT COUNT(*) FROM notifications.mailbox WHERE event_id=@id)",
            ("id", eventId)
        );
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
