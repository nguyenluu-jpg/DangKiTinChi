using System.Data;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Dapper;

namespace CourseRegistration.DataStore.SqlServer
{
    public class SectionRepository : ISectionRepository
    {
        private readonly ISqlConnectionFactory _connectionFactory;

        public SectionRepository(ISqlConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        private const string BaseSectionQuery = @"
            SELECT s.SectionId, s.SectionCode, s.CourseId, s.Instructor, s.MaxCapacity, 
                   s.CurrentEnrollment, s.Semester, s.Room, s.RegistrationOpenAt, s.RegistrationCloseAt,
                   c.CourseId, c.CourseCode, c.CourseName, c.Credits, c.Description,
                   sch.ScheduleId, sch.SectionId, sch.DayOfWeek, sch.StartPeriod, sch.EndPeriod, sch.Room
            FROM Sections s
            INNER JOIN Courses c ON s.CourseId = c.CourseId
            LEFT JOIN SectionSchedules sch ON s.SectionId = sch.SectionId";

        public async Task<IEnumerable<Section>> GetAllSectionsAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            string sql = $"{BaseSectionQuery} ORDER BY s.SectionCode ASC;";

            var sectionDictionary = new Dictionary<int, Section>();
            await connection.QueryAsync<Section, Course, SectionSchedule, Section>(
                sql,
                (section, course, schedule) =>
                {
                    if (!sectionDictionary.TryGetValue(section.SectionId, out var currentSection))
                    {
                        currentSection = section;
                        currentSection.Course = course;
                        currentSection.Schedules = new List<SectionSchedule>();
                        sectionDictionary.Add(currentSection.SectionId, currentSection);
                    }

                    if (schedule != null && schedule.ScheduleId > 0 && !currentSection.Schedules.Any(s => s.ScheduleId == schedule.ScheduleId))
                    {
                        currentSection.Schedules.Add(schedule);
                    }

                    return currentSection;
                },
                splitOn: "CourseId,ScheduleId"
            );

            return sectionDictionary.Values;
        }

        public async Task<IEnumerable<Section>> GetSectionsBySemesterAsync(string semester)
        {
            using var connection = _connectionFactory.CreateConnection();
            string sql = $"{BaseSectionQuery} WHERE s.Semester = @Semester ORDER BY s.SectionCode ASC;";

            var sectionDictionary = new Dictionary<int, Section>();
            await connection.QueryAsync<Section, Course, SectionSchedule, Section>(
                sql,
                (section, course, schedule) =>
                {
                    if (!sectionDictionary.TryGetValue(section.SectionId, out var currentSection))
                    {
                        currentSection = section;
                        currentSection.Course = course;
                        currentSection.Schedules = new List<SectionSchedule>();
                        sectionDictionary.Add(currentSection.SectionId, currentSection);
                    }

                    if (schedule != null && schedule.ScheduleId > 0 && !currentSection.Schedules.Any(s => s.ScheduleId == schedule.ScheduleId))
                    {
                        currentSection.Schedules.Add(schedule);
                    }

                    return currentSection;
                },
                new { Semester = semester },
                splitOn: "CourseId,ScheduleId"
            );

            return sectionDictionary.Values;
        }

        public async Task<IEnumerable<Section>> SearchSectionsAsync(string keyword, string semester)
        {
            using var connection = _connectionFactory.CreateConnection();
            string sql = $@"{BaseSectionQuery}
                WHERE (@Semester IS NULL OR @Semester = '' OR s.Semester = @Semester)
                  AND (@Pattern IS NULL OR @Pattern = '' OR 
                       s.SectionCode LIKE @Pattern OR 
                       c.CourseName LIKE @Pattern OR 
                       c.CourseCode LIKE @Pattern OR 
                       s.Instructor LIKE @Pattern)
                ORDER BY s.SectionCode ASC;";

            string? pattern = string.IsNullOrWhiteSpace(keyword) ? null : $"%{keyword}%";

            var sectionDictionary = new Dictionary<int, Section>();
            await connection.QueryAsync<Section, Course, SectionSchedule, Section>(
                sql,
                (section, course, schedule) =>
                {
                    if (!sectionDictionary.TryGetValue(section.SectionId, out var currentSection))
                    {
                        currentSection = section;
                        currentSection.Course = course;
                        currentSection.Schedules = new List<SectionSchedule>();
                        sectionDictionary.Add(currentSection.SectionId, currentSection);
                    }

                    if (schedule != null && schedule.ScheduleId > 0 && !currentSection.Schedules.Any(s => s.ScheduleId == schedule.ScheduleId))
                    {
                        currentSection.Schedules.Add(schedule);
                    }

                    return currentSection;
                },
                new { Semester = semester, Pattern = pattern },
                splitOn: "CourseId,ScheduleId"
            );

            return sectionDictionary.Values;
        }

        public async Task<Section?> GetSectionByIdAsync(int sectionId)
        {
            using var connection = _connectionFactory.CreateConnection();
            string sql = $"{BaseSectionQuery} WHERE s.SectionId = @SectionId;";

            Section? resultSection = null;
            await connection.QueryAsync<Section, Course, SectionSchedule, Section>(
                sql,
                (section, course, schedule) =>
                {
                    if (resultSection == null)
                    {
                        resultSection = section;
                        resultSection.Course = course;
                        resultSection.Schedules = new List<SectionSchedule>();
                    }

                    if (schedule != null && schedule.ScheduleId > 0 && !resultSection.Schedules.Any(s => s.ScheduleId == schedule.ScheduleId))
                    {
                        resultSection.Schedules.Add(schedule);
                    }

                    return resultSection;
                },
                new { SectionId = sectionId },
                splitOn: "CourseId,ScheduleId"
            );

            return resultSection;
        }

        public async Task AddSectionAsync(Section section)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                INSERT INTO Sections (SectionCode, CourseId, Instructor, MaxCapacity, CurrentEnrollment, Semester, Room)
                VALUES (@SectionCode, @CourseId, @Instructor, @MaxCapacity, @CurrentEnrollment, @Semester, @Room);
                SELECT CAST(SCOPE_IDENTITY() as int);";

            var id = await connection.ExecuteScalarAsync<int>(sql, section);
            section.SectionId = id;
        }

        public async Task UpdateSectionAsync(Section section)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                UPDATE Sections
                SET SectionCode = @SectionCode,
                    CourseId = @CourseId,
                    Instructor = @Instructor,
                    MaxCapacity = @MaxCapacity,
                    CurrentEnrollment = @CurrentEnrollment,
                    Semester = @Semester,
                    Room = @Room
                WHERE SectionId = @SectionId;";

            await connection.ExecuteAsync(sql, section);
        }

        public async Task DeleteSectionAsync(int sectionId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "DELETE FROM Sections WHERE SectionId = @SectionId;";
            await connection.ExecuteAsync(sql, new { SectionId = sectionId });
        }

        public async Task AddScheduleAsync(SectionSchedule schedule)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                INSERT INTO SectionSchedules (SectionId, DayOfWeek, StartPeriod, EndPeriod, Room)
                VALUES (@SectionId, @DayOfWeek, @StartPeriod, @EndPeriod, @Room);
                SELECT CAST(SCOPE_IDENTITY() as int);";

            var id = await connection.ExecuteScalarAsync<int>(sql, schedule);
            schedule.ScheduleId = id;
        }

        public async Task DeleteScheduleAsync(int scheduleId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "DELETE FROM SectionSchedules WHERE ScheduleId = @ScheduleId;";
            await connection.ExecuteAsync(sql, new { ScheduleId = scheduleId });
        }

        public async Task<IEnumerable<SectionSchedule>> GetSchedulesBySectionIdAsync(int sectionId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = "SELECT * FROM SectionSchedules WHERE SectionId = @SectionId ORDER BY DayOfWeek, StartPeriod;";
            return await connection.QueryAsync<SectionSchedule>(sql, new { SectionId = sectionId });
        }

        public async Task<bool> IncrementEnrollmentCountAsync(int sectionId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                UPDATE Sections 
                SET CurrentEnrollment = CurrentEnrollment + 1 
                WHERE SectionId = @SectionId AND CurrentEnrollment < MaxCapacity;";

            var affectedRows = await connection.ExecuteAsync(sql, new { SectionId = sectionId });
            return affectedRows > 0;
        }

        public async Task DecrementEnrollmentCountAsync(int sectionId)
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                UPDATE Sections 
                SET CurrentEnrollment = CurrentEnrollment - 1 
                WHERE SectionId = @SectionId AND CurrentEnrollment > 0;";

            await connection.ExecuteAsync(sql, new { SectionId = sectionId });
        }
    }
}
