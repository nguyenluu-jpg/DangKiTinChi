using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.EntityFrameworkCore;

namespace CourseRegistration.DataStore.SqlServer
{
    public class StudentRepository : IStudentRepository
    {
        private readonly IDbContextFactory<CourseRegistrationContext> _contextFactory;

        public StudentRepository(IDbContextFactory<CourseRegistrationContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task<IEnumerable<Student>> GetAllStudentsAsync()
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Students
                .AsNoTracking()
                .OrderBy(s => s.StudentCode)
                .ToListAsync();
        }

        public async Task<Student?> GetStudentByIdAsync(int studentId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Students
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StudentId == studentId);
        }

        public async Task<Student?> GetStudentByCodeAsync(string studentCode)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Students
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StudentCode == studentCode);
        }

        public async Task AddStudentAsync(Student student)
        {
            using var context = _contextFactory.CreateDbContext();
            await context.Students.AddAsync(student);
            await context.SaveChangesAsync();
        }

        public async Task UpdateStudentAsync(Student student)
        {
            using var context = _contextFactory.CreateDbContext();
            var existing = await context.Students.FindAsync(student.StudentId);
            if (existing != null)
            {
                existing.FullName = student.FullName;
                existing.Email = student.Email;
                existing.PhoneNumber = student.PhoneNumber;
                existing.AcademicYear = student.AcademicYear;
                existing.Major = student.Major;
                existing.MaxCreditsPerSemester = student.MaxCreditsPerSemester;
                await context.SaveChangesAsync();
            }
        }

        public async Task<IEnumerable<CompletedCourse>> GetCompletedCoursesByStudentIdAsync(int studentId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.CompletedCourses
                .AsNoTracking()
                .Include(cc => cc.Course)
                .Where(cc => cc.StudentId == studentId)
                .ToListAsync();
        }
    }
}
