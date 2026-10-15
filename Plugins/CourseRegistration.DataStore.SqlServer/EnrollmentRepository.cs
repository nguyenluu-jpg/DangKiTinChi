using System.Data;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Dapper;

namespace CourseRegistration.DataStore.SqlServer
{
    public class EnrollmentRepository : IEnrollmentRepository
    {
        private readonly ISqlConnectionFactory _connectionFactory;
        private readonly ISectionRepository _sectionRepository;
        private readonly IStudentRepository _studentRepository;

        public EnrollmentRepository(
            ISqlConnectionFactory connectionFactory,
            ISectionRepository sectionRepository,
            IStudentRepository studentRepository)
        {
            _connectionFactory = connectionFactory;
            _sectionRepository = sectionRepository;
            _studentRepository = studentRepository;
        }

        public async Task<int> CreateEnrollmentAsync(Enrollment enrollment)
        {
            using var connection = (Microsoft.Data.SqlClient.SqlConnection)_connectionFactory.CreateConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                const string insertEnrollmentSql = @"
                    INSERT INTO Enrollments (StudentId, Semester, DateRegistered, Status, UniqueId)
                    VALUES (@StudentId, @Semester, @DateRegistered, @Status, @UniqueId);
                    SELECT CAST(SCOPE_IDENTITY() as int);";

                var enrollmentId = await connection.ExecuteScalarAsync<int>(
                    insertEnrollmentSql,
                    new
                    {
                        enrollment.StudentId,
                        enrollment.Semester,
                        DateRegistered = enrollment.DateRegistered ?? DateTime.Now,
                        enrollment.Status,
                        enrollment.UniqueId
                    },
                    transaction
                );

                enrollment.EnrollmentId = enrollmentId;

                const string insertLineSql = @"
                    INSERT INTO EnrollmentLines (EnrollmentId, SectionId)
                    VALUES (@EnrollmentId, @SectionId);";

                foreach (var line in enrollment.EnrollmentLines)
                {
                    await connection.ExecuteAsync(
                        insertLineSql,
                        new { EnrollmentId = enrollmentId, line.SectionId },
                        transaction
                    );
                }

                transaction.Commit();
                return enrollmentId;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task CancelEnrollmentAsync(int enrollmentId)
        {
            using var connection = (Microsoft.Data.SqlClient.SqlConnection)_connectionFactory.CreateConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                // Cập nhật trạng thái phiếu và lưu thời gian hủy
                const string cancelEnrollmentSql = @"
                    UPDATE Enrollments 
                    SET Status = 'Cancelled', DateCancelled = GETDATE()
                    WHERE EnrollmentId = @EnrollmentId;";

                await connection.ExecuteAsync(cancelEnrollmentSql, new { EnrollmentId = enrollmentId }, transaction);

                // Giảm sĩ số hoàn lại cho các lớp trong phiếu
                const string decrementSectionSql = @"
                    UPDATE s
                    SET s.CurrentEnrollment = s.CurrentEnrollment - 1
                    FROM Sections s
                    INNER JOIN EnrollmentLines el ON s.SectionId = el.SectionId
                    WHERE el.EnrollmentId = @EnrollmentId AND s.CurrentEnrollment > 0;";

                await connection.ExecuteAsync(decrementSectionSql, new { EnrollmentId = enrollmentId }, transaction);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task<Enrollment?> GetEnrollmentByIdAsync(int enrollmentId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Enrollments WHERE EnrollmentId = @EnrollmentId;";
            var enrollment = await connection.QueryFirstOrDefaultAsync<Enrollment>(sql, new { EnrollmentId = enrollmentId });
            if (enrollment == null) return null;

            enrollment.Student = await _studentRepository.GetStudentByIdAsync(enrollment.StudentId);

            const string linesSql = "SELECT * FROM EnrollmentLines WHERE EnrollmentId = @EnrollmentId;";
            var lines = (await connection.QueryAsync<EnrollmentLine>(linesSql, new { EnrollmentId = enrollmentId })).ToList();

            foreach (var line in lines)
            {
                line.Section = await _sectionRepository.GetSectionByIdAsync(line.SectionId);
            }

            enrollment.EnrollmentLines = lines;
            return enrollment;
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsByStudentIdAsync(int studentId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Enrollments WHERE StudentId = @StudentId ORDER BY DateRegistered DESC;";
            var enrollments = (await connection.QueryAsync<Enrollment>(sql, new { StudentId = studentId })).ToList();

            foreach (var e in enrollments)
            {
                e.Student = await _studentRepository.GetStudentByIdAsync(e.StudentId);

                const string linesSql = "SELECT * FROM EnrollmentLines WHERE EnrollmentId = @EnrollmentId;";
                var lines = (await connection.QueryAsync<EnrollmentLine>(linesSql, new { EnrollmentId = e.EnrollmentId })).ToList();

                foreach (var line in lines)
                {
                    line.Section = await _sectionRepository.GetSectionByIdAsync(line.SectionId);
                }

                e.EnrollmentLines = lines;
            }

            return enrollments;
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM Enrollments WHERE Semester = @Semester ORDER BY DateRegistered DESC;";
            var enrollments = (await connection.QueryAsync<Enrollment>(sql, new { Semester = semester })).ToList();

            foreach (var e in enrollments)
            {
                e.Student = await _studentRepository.GetStudentByIdAsync(e.StudentId);

                const string linesSql = "SELECT * FROM EnrollmentLines WHERE EnrollmentId = @EnrollmentId;";
                var lines = (await connection.QueryAsync<EnrollmentLine>(linesSql, new { EnrollmentId = e.EnrollmentId })).ToList();

                foreach (var line in lines)
                {
                    line.Section = await _sectionRepository.GetSectionByIdAsync(line.SectionId);
                }

                e.EnrollmentLines = lines;
            }

            return enrollments;
        }

        public async Task<IEnumerable<Section>> GetRegisteredSectionsByStudentAsync(int studentId, string semester)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                SELECT DISTINCT el.SectionId
                FROM Enrollments e
                INNER JOIN EnrollmentLines el ON e.EnrollmentId = el.EnrollmentId
                WHERE e.StudentId = @StudentId 
                  AND e.Semester = @Semester 
                  AND e.Status = 'Confirmed';";

            var sectionIds = (await connection.QueryAsync<int>(sql, new { StudentId = studentId, Semester = semester })).ToList();

            var sections = new List<Section>();
            foreach (var id in sectionIds)
            {
                var sec = await _sectionRepository.GetSectionByIdAsync(id);
                if (sec != null)
                {
                    sections.Add(sec);
                }
            }

            return sections;
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsBySectionIdAsync(int sectionId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                SELECT DISTINCT e.EnrollmentId
                FROM Enrollments e
                INNER JOIN EnrollmentLines el ON e.EnrollmentId = el.EnrollmentId
                WHERE el.SectionId = @SectionId AND e.Status = 'Confirmed';";

            var enrollmentIds = await connection.QueryAsync<int>(sql, new { SectionId = sectionId });

            var list = new List<Enrollment>();
            foreach (var eId in enrollmentIds)
            {
                var enrollment = await GetEnrollmentByIdAsync(eId);
                if (enrollment != null)
                {
                    list.Add(enrollment);
                }
            }

            return list;
        }
    }
}
