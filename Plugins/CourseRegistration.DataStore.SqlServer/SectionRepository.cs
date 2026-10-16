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
                .Where(s => s.Semester == semester)
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
                query = query.Where(s => s.Semester == semester);
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
            var section = await context.Sections.FindAsync(sectionId);
            if (section != null && section.CurrentEnrollment < section.MaxCapacity)
            {
                section.CurrentEnrollment++;
                await context.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public async Task DecrementEnrollmentCountAsync(int sectionId)
        {
            using var context = _contextFactory.CreateDbContext();
            var section = await context.Sections.FindAsync(sectionId);
            if (section != null && section.CurrentEnrollment > 0)
            {
                section.CurrentEnrollment--;
                await context.SaveChangesAsync();
            }
        }
    }
}
