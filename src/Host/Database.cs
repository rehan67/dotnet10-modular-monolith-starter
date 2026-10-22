using System.Data.Common;
using EduCore.Contracts;
using Npgsql;

namespace EduCore.Host;

public sealed class PostgresDatabase(NpgsqlDataSource source) : IDatabase
{
    public async Task<DbConnection> OpenAsync(CancellationToken ct) =>
        await source.OpenConnectionAsync(ct);

    public async Task InitialiseAsync()
    {
        await using var connection = await OpenAsync(default);
        await using var command = Sql.Command(
            connection,
            null,
            """
CREATE SCHEMA IF NOT EXISTS courses; CREATE SCHEMA IF NOT EXISTS enrollments; CREATE SCHEMA IF NOT EXISTS notifications;
CREATE TABLE IF NOT EXISTS courses.catalog(id uuid PRIMARY KEY,title text NOT NULL,capacity integer NOT NULL CHECK(capacity BETWEEN 1 AND 1000),reserved integer NOT NULL CHECK(reserved>=0 AND reserved<=capacity));
CREATE TABLE IF NOT EXISTS enrollments.enrollment(id uuid PRIMARY KEY,course_id uuid NOT NULL,student_id uuid NOT NULL,UNIQUE(course_id,student_id));
CREATE TABLE IF NOT EXISTS enrollments.outbox(id uuid PRIMARY KEY,payload text NOT NULL,created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,processed_at timestamptz);
CREATE INDEX IF NOT EXISTS pending_outbox ON enrollments.outbox(created_at,id) WHERE processed_at IS NULL;
CREATE TABLE IF NOT EXISTS notifications.inbox(event_id uuid PRIMARY KEY);
CREATE TABLE IF NOT EXISTS notifications.mailbox(event_id uuid PRIMARY KEY,student_id uuid NOT NULL);
CREATE TABLE IF NOT EXISTS notifications.delivery_log(id uuid PRIMARY KEY,event_id uuid NOT NULL);
"""
        );
        await command.ExecuteNonQueryAsync();
    }
}
