using System.Data.Common;
namespace EduCore.Contracts;

public interface IDatabase { Task<DbConnection> OpenAsync(CancellationToken ct); }
public interface ICourseSeats { Task ReserveAsync(Guid courseId, DbTransaction transaction, CancellationToken ct); }
public record EnrollmentRecorded(Guid Id, Guid CourseId, Guid StudentId);
public interface IEnrollmentConsumer { Task ConsumeAsync(EnrollmentRecorded message, CancellationToken ct); }
public static class Sql
{
    public static DbCommand Command(DbConnection connection, DbTransaction? transaction, string text, params (string, object)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = text; command.CommandTimeout = 10;
        foreach (var (name, value) in parameters) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
        return command;
    }
}
