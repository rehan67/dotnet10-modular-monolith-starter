using System.Data.Common;
using EduCore.Contracts;
namespace EduCore.Courses;

public sealed class CoursesModule(IDatabase database) : ICourseSeats
{
    public async Task<Guid> CreateAsync(string? title, int capacity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || capacity is < 1 or > 1000) throw new ArgumentException("Invalid course.");
        var id = Guid.NewGuid(); await using var connection = await database.OpenAsync(ct);
        await using var command = Sql.Command(connection, null, "INSERT INTO courses.catalog(id,title,capacity,reserved) VALUES(@id,@title,@capacity,0)", ("id", id), ("title", title.Trim()), ("capacity", capacity));
        await command.ExecuteNonQueryAsync(ct); return id;
    }
    public async Task ReserveAsync(Guid courseId, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Sql.Command(transaction.Connection!, transaction, "UPDATE courses.catalog SET reserved=reserved+1 WHERE id=@id AND reserved<capacity", ("id", courseId));
        if (await command.ExecuteNonQueryAsync(ct) == 1) return;
        await using var exists = Sql.Command(transaction.Connection!, transaction, "SELECT COUNT(*) FROM courses.catalog WHERE id=@id", ("id", courseId));
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) throw new KeyNotFoundException("Course not found.");
        throw new InvalidOperationException("The course is full.");
    }
}
