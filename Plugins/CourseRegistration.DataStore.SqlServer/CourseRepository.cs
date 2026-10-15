using System.Data;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Dapper;

namespace CourseRegistration.DataStore.SqlServer
{
    public class CourseRepository : ICourseRepository
    {
        private readonly ISqlConnectionFactory _connectionFactory;

        public CourseRepository(ISqlConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<IEnumerable<Course>> GetAllCoursesAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Courses ORDER BY CourseCode ASC;";
            return await connection.QueryAsync<Course>(sql);
        }

        public async Task<Course?> GetCourseByIdAsync(int courseId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Courses WHERE CourseId = @CourseId;";
            return await connection.QueryFirstOrDefaultAsync<Course>(sql, new { CourseId = courseId });
        }

        public async Task<IEnumerable<Course>> SearchCoursesAsync(string keyword)
        {
            using var connection = _connectionFactory.CreateConnection();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return await GetAllCoursesAsync();
            }

            const string sql = @"
                SELECT * FROM Courses 
                WHERE CourseCode LIKE @Pattern OR CourseName LIKE @Pattern
                ORDER BY CourseCode ASC;";

            return await connection.QueryAsync<Course>(sql, new { Pattern = $"%{keyword}%" });
        }

        public async Task AddCourseAsync(Course course)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                INSERT INTO Courses (CourseCode, CourseName, Credits, Description)
                VALUES (@CourseCode, @CourseName, @Credits, @Description);
                SELECT CAST(SCOPE_IDENTITY() as int);";

            var id = await connection.ExecuteScalarAsync<int>(sql, course);
            course.CourseId = id;
        }

        public async Task UpdateCourseAsync(Course course)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                UPDATE Courses
                SET CourseCode = @CourseCode,
                    CourseName = @CourseName,
                    Credits = @Credits,
                    Description = @Description
                WHERE CourseId = @CourseId;";

            await connection.ExecuteAsync(sql, course);
        }

        public async Task DeleteCourseAsync(int courseId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "DELETE FROM Courses WHERE CourseId = @CourseId;";
            await connection.ExecuteAsync(sql, new { CourseId = courseId });
        }

        public async Task<IEnumerable<Prerequisite>> GetPrerequisitesByCourseIdAsync(int courseId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                SELECT p.PrerequisiteId, p.CourseId, p.PrerequisiteCourseId,
                       c.CourseId, c.CourseCode, c.CourseName, c.Credits, c.Description,
                       pc.CourseId, pc.CourseCode, pc.CourseName, pc.Credits, pc.Description
                FROM Prerequisites p
                INNER JOIN Courses c ON p.CourseId = c.CourseId
                INNER JOIN Courses pc ON p.PrerequisiteCourseId = pc.CourseId
                WHERE p.CourseId = @CourseId;";

            var result = await connection.QueryAsync<Prerequisite, Course, Course, Prerequisite>(
                sql,
                (prereq, course, prereqCourse) =>
                {
                    prereq.Course = course;
                    prereq.PrerequisiteCourse = prereqCourse;
                    return prereq;
                },
                new { CourseId = courseId },
                splitOn: "CourseId,CourseId"
            );

            return result;
        }

        public async Task<IEnumerable<Prerequisite>> GetAllPrerequisitesAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                SELECT p.PrerequisiteId, p.CourseId, p.PrerequisiteCourseId,
                       c.CourseId, c.CourseCode, c.CourseName, c.Credits, c.Description,
                       pc.CourseId, pc.CourseCode, pc.CourseName, pc.Credits, pc.Description
                FROM Prerequisites p
                INNER JOIN Courses c ON p.CourseId = c.CourseId
                INNER JOIN Courses pc ON p.PrerequisiteCourseId = pc.CourseId;";

            var result = await connection.QueryAsync<Prerequisite, Course, Course, Prerequisite>(
                sql,
                (prereq, course, prereqCourse) =>
                {
                    prereq.Course = course;
                    prereq.PrerequisiteCourse = prereqCourse;
                    return prereq;
                },
                splitOn: "CourseId,CourseId"
            );

            return result;
        }

        public async Task AddPrerequisiteAsync(Prerequisite prerequisite)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                INSERT INTO Prerequisites (CourseId, PrerequisiteCourseId)
                VALUES (@CourseId, @PrerequisiteCourseId);
                SELECT CAST(SCOPE_IDENTITY() as int);";

            var id = await connection.ExecuteScalarAsync<int>(sql, prerequisite);
            prerequisite.PrerequisiteId = id;
        }

        public async Task DeletePrerequisiteAsync(int prerequisiteId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "DELETE FROM Prerequisites WHERE PrerequisiteId = @PrerequisiteId;";
            await connection.ExecuteAsync(sql, new { PrerequisiteId = prerequisiteId });
        }
    }
}
