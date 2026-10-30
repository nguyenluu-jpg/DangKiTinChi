using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.EntityFrameworkCore;

namespace CourseRegistration.DataStore.SqlServer
{
    public class SectionRepository : ISectionRepository
    {
        private readonly IDbContextFactory<CourseRegistrationContext> _contextFactory;

        public SectionRepository(IDbContextFactory<CourseRegistrationContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task<IEnumerable<Section>> GetAllSectionsAsync()
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Sections
                .AsNoTracking()
                .Include(s => s.Course)
                .Include(s => s.Schedules)
                .OrderBy(s => s.SectionCode)
                .ToListAsync();
        }

        public async Task<IEnumerable<Section>> GetSectionsBySemesterAsync(string semester)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Sections
                .AsNoTracking()
                .Include(s => s.Course)
                .Include(s => s.Schedules)
                .Where(s => s.Semester == semester || (semester == "2026_HK1" && s.Semester == "2025_HK1"))
                .OrderBy(s => s.SectionCode)
                .ToListAsync();
        }

        public async Task<IEnumerable<Section>> SearchSectionsAsync(string keyword, string semester)
        {
            using var context = _contextFactory.CreateDbContext();
            var query = context.Sections
                .AsNoTracking()
                .Include(s => s.Course)
                .Include(s => s.Schedules)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(semester))
            {
                query = query.Where(s => s.Semester == semester || (semester == "2026_HK1" && s.Semester == "2025_HK1"));
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(s =>
                    s.SectionCode.Contains(keyword) ||
                    s.Instructor.Contains(keyword) ||
                    (s.Course != null && (s.Course.CourseCode.Contains(keyword) || s.Course.CourseName.Contains(keyword))));
            }

            return await query.OrderBy(s => s.SectionCode).ToListAsync();
        }

        public async Task<Section?> GetSectionByIdAsync(int sectionId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Sections
                .AsNoTracking()
                .Include(s => s.Course)
                .Include(s => s.Schedules)
                .FirstOrDefaultAsync(s => s.SectionId == sectionId);
        }

        public async Task AddSectionAsync(Section section)
        {
            using var context = _contextFactory.CreateDbContext();
            // Đảm bảo không gán lại Navigation property nếu đã có CourseId
            section.Course = null;
            await context.Sections.AddAsync(section);
            await context.SaveChangesAsync();
        }

        public async Task UpdateSectionAsync(Section section)
        {
            using var context = _contextFactory.CreateDbContext();
            var existing = await context.Sections.FindAsync(section.SectionId);
            if (existing != null)
            {
                existing.SectionCode = section.SectionCode;
                existing.CourseId = section.CourseId;
                existing.Instructor = section.Instructor;
                existing.MaxCapacity = section.MaxCapacity;
                existing.Room = section.Room;
                existing.Semester = section.Semester;
                existing.RegistrationOpenAt = section.RegistrationOpenAt;
                existing.RegistrationCloseAt = section.RegistrationCloseAt;
                await context.SaveChangesAsync();
            }
        }

        public async Task DeleteSectionAsync(int sectionId)
        {
            using var context = _contextFactory.CreateDbContext();
            var section = await context.Sections.FindAsync(sectionId);
            if (section != null)
            {
                context.Sections.Remove(section);
                await context.SaveChangesAsync();
            }
        }

        public async Task AddScheduleAsync(SectionSchedule schedule)
        {
            using var context = _contextFactory.CreateDbContext();
            schedule.Section = null;
            await context.SectionSchedules.AddAsync(schedule);
            await context.SaveChangesAsync();
        }

        public async Task DeleteScheduleAsync(int scheduleId)
        {
            using var context = _contextFactory.CreateDbContext();
            var schedule = await context.SectionSchedules.FindAsync(scheduleId);
            if (schedule != null)
            {
                context.SectionSchedules.Remove(schedule);
                await context.SaveChangesAsync();
            }
        }

        public async Task<IEnumerable<SectionSchedule>> GetSchedulesBySectionIdAsync(int sectionId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.SectionSchedules
                .AsNoTracking()
                .Where(sch => sch.SectionId == sectionId)
                .OrderBy(sch => sch.DayOfWeek)
                .ThenBy(sch => sch.StartPeriod)
                .ToListAsync();
        }

        public async Task<bool> IncrementEnrollmentCountAsync(int sectionId)
        {
            using var context = _contextFactory.CreateDbContext();
            // Sử dụng câu lệnh UPDATE nguyên tử ở mức SQL Server (Atomic conditional update - LUẬT 2)
            // SQL Server tự động đặt exclusive lock tại hàng được cập nhật.
            // Nếu 2 sinh viên cùng đăng ký chỗ cuối cùng (ví dụ sĩ số 29/30):
            // - Sinh viên 1: thỏa mãn CurrentEnrollment < MaxCapacity -> CurrentEnrollment tăng lên 30, affectedRows = 1 -> trả về true.
            // - Sinh viên 2: CurrentEnrollment lúc này đã là 30, không còn < MaxCapacity -> 0 rows affected -> trả về false.
            // Đảm bảo triệt để Luật 2 (sĩ số không bao giờ vượt giới hạn, loại trừ race condition kể cả khi nhiều SV cùng đăng ký chỗ cuối).
            int affected = await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Sections SET CurrentEnrollment = CurrentEnrollment + 1 WHERE SectionId = {sectionId} AND CurrentEnrollment < MaxCapacity");

            return affected > 0;
        }

        public async Task DecrementEnrollmentCountAsync(int sectionId)
        {
            using var context = _contextFactory.CreateDbContext();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Sections SET CurrentEnrollment = CurrentEnrollment - 1 WHERE SectionId = {sectionId} AND CurrentEnrollment > 0");
        }
    }
}
