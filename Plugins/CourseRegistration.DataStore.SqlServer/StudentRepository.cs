using System.Data;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Dapper;

namespace CourseRegistration.DataStore.SqlServer
{
    public class StudentRepository : IStudentRepository
    {
        private readonly ISqlConnectionFactory _connectionFactory;

        public StudentRepository(ISqlConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<IEnumerable<Student>> GetAllStudentsAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Students ORDER BY StudentCode ASC;";
            return await connection.QueryAsync<Student>(sql);
        }

        public async Task<Student?> GetStudentByIdAsync(int studentId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Students WHERE StudentId = @StudentId;";
            return await connection.QueryFirstOrDefaultAsync<Student>(sql, new { StudentId = studentId });
        }

        public async Task<Student?> GetStudentByCodeAsync(string studentCode)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Students WHERE StudentCode = @StudentCode;";
            return await connection.QueryFirstOrDefaultAsync<Student>(sql, new { StudentCode = studentCode });
        }

        public async Task AddStudentAsync(Student student)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                INSERT INTO Students (StudentCode, FullName, Email, PhoneNumber, AcademicYear, Major, MaxCreditsPerSemester)
                VALUES (@StudentCode, @FullName, @Email, @PhoneNumber, @AcademicYear, @Major, @MaxCreditsPerSemester);
                SELECT CAST(SCOPE_IDENTITY() as int);";

            var id = await connection.ExecuteScalarAsync<int>(sql, student);
            student.StudentId = id;
        }

        public async Task UpdateStudentAsync(Student student)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                UPDATE Students 
                SET StudentCode = @StudentCode,
                    FullName = @FullName,
                    Email = @Email,
                    PhoneNumber = @PhoneNumber,
                    AcademicYear = @AcademicYear,
                    Major = @Major,
                    MaxCreditsPerSemester = @MaxCreditsPerSemester
                WHERE StudentId = @StudentId;";

            await connection.ExecuteAsync(sql, student);
        }

        public async Task<IEnumerable<CompletedCourse>> GetCompletedCoursesByStudentIdAsync(int studentId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                SELECT cc.CompletedCourseId, cc.StudentId, cc.CourseId, cc.Semester, cc.Grade,
                       c.CourseId, c.CourseCode, c.CourseName, c.Credits, c.Description
                FROM CompletedCourses cc
                INNER JOIN Courses c ON cc.CourseId = c.CourseId
                WHERE cc.StudentId = @StudentId;";

            var result = await connection.QueryAsync<CompletedCourse, Course, CompletedCourse>(
                sql,
                (completedCourse, course) =>
                {
                    completedCourse.Course = course;
                    return completedCourse;
                },
                new { StudentId = studentId },
                splitOn: "CourseId"
            );

            return result;
        }
    }
}
