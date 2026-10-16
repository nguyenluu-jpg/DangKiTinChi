using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.EntityFrameworkCore;

namespace CourseRegistration.DataStore.SqlServer
{
    public class CourseRepository : ICourseRepository
    {
        private readonly IDbContextFactory<CourseRegistrationContext> _contextFactory;

        public CourseRepository(IDbContextFactory<CourseRegistrationContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task<IEnumerable<Course>> GetAllCoursesAsync()
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Courses
                .AsNoTracking()
                .OrderBy(c => c.CourseCode)
                .ToListAsync();
        }

        public async Task<Course?> GetCourseByIdAsync(int courseId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Courses
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.CourseId == courseId);
        }

        public async Task<IEnumerable<Course>> SearchCoursesAsync(string keyword)
        {
            using var context = _contextFactory.CreateDbContext();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return await context.Courses
                    .AsNoTracking()
                    .OrderBy(c => c.CourseCode)
                    .ToListAsync();
            }

            return await context.Courses
                .AsNoTracking()
                .Where(c => c.CourseCode.Contains(keyword) || c.CourseName.Contains(keyword))
                .OrderBy(c => c.CourseCode)
                .ToListAsync();
        }

        public async Task AddCourseAsync(Course course)
        {
            using var context = _contextFactory.CreateDbContext();
            await context.Courses.AddAsync(course);
            await context.SaveChangesAsync();
        }

        public async Task UpdateCourseAsync(Course course)
        {
            using var context = _contextFactory.CreateDbContext();
            var existing = await context.Courses.FindAsync(course.CourseId);
            if (existing != null)
            {
                existing.CourseCode = course.CourseCode;
                existing.CourseName = course.CourseName;
                existing.Credits = course.Credits;
                existing.Description = course.Description;
                await context.SaveChangesAsync();
            }
        }

        public async Task DeleteCourseAsync(int courseId)
        {
            using var context = _contextFactory.CreateDbContext();
            var course = await context.Courses.FindAsync(courseId);
            if (course != null)
            {
                context.Courses.Remove(course);
                await context.SaveChangesAsync();
            }
        }

        public async Task<IEnumerable<Prerequisite>> GetPrerequisitesByCourseIdAsync(int courseId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Prerequisites
                .AsNoTracking()
                .Include(p => p.Course)
                .Include(p => p.PrerequisiteCourse)
                .Where(p => p.CourseId == courseId)
                .ToListAsync();
        }

        public async Task<IEnumerable<Prerequisite>> GetAllPrerequisitesAsync()
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Prerequisites
                .AsNoTracking()
                .Include(p => p.Course)
                .Include(p => p.PrerequisiteCourse)
                .ToListAsync();
        }

        public async Task AddPrerequisiteAsync(Prerequisite prerequisite)
        {
            using var context = _contextFactory.CreateDbContext();
            await context.Prerequisites.AddAsync(prerequisite);
            await context.SaveChangesAsync();
        }

        public async Task DeletePrerequisiteAsync(int prerequisiteId)
        {
            using var context = _contextFactory.CreateDbContext();
            var prereq = await context.Prerequisites.FindAsync(prerequisiteId);
            if (prereq != null)
            {
                context.Prerequisites.Remove(prereq);
                await context.SaveChangesAsync();
            }
        }
    }
}
