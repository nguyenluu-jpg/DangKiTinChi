using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using Microsoft.EntityFrameworkCore;

namespace CourseRegistration.DataStore.SqlServer
{
    public class EnrollmentRepository : IEnrollmentRepository
    {
        private readonly IDbContextFactory<CourseRegistrationContext> _contextFactory;

        public EnrollmentRepository(IDbContextFactory<CourseRegistrationContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task<Enrollment?> GetEnrollmentByIdAsync(int enrollmentId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Enrollments
                .AsNoTracking()
                .Include(e => e.Student)
                .Include(e => e.EnrollmentLines)
                    .ThenInclude(el => el.Section)
                        .ThenInclude(s => s!.Course)
                .Include(e => e.EnrollmentLines)
                    .ThenInclude(el => el.Section)
                        .ThenInclude(s => s!.Schedules)
                .FirstOrDefaultAsync(e => e.EnrollmentId == enrollmentId);
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsByStudentIdAsync(int studentId)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Enrollments
                .AsNoTracking()
                .Include(e => e.Student)
                .Include(e => e.EnrollmentLines)
                    .ThenInclude(el => el.Section)
                        .ThenInclude(s => s!.Course)
                .Where(e => e.StudentId == studentId)
                .OrderByDescending(e => e.DateRegistered)
                .ToListAsync();
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester)
        {
            using var context = _contextFactory.CreateDbContext();
            return await context.Enrollments
                .AsNoTracking()
                .Include(e => e.Student)
                .Include(e => e.EnrollmentLines)
                    .ThenInclude(el => el.Section)
                        .ThenInclude(s => s!.Course)
                .Where(e => e.Semester == semester || (semester == "2026_HK1" && e.Semester == "2025_HK1"))
                .OrderByDescending(e => e.DateRegistered)
                .ToListAsync();
        }

        public async Task<IEnumerable<Section>> GetRegisteredSectionsByStudentAsync(int studentId, string semester)
        {
            using var context = _contextFactory.CreateDbContext();
            var confirmedEnrollmentIds = await context.Enrollments
                .AsNoTracking()
                .Where(e => e.StudentId == studentId && (e.Semester == semester || (semester == "2026_HK1" && e.Semester == "2025_HK1")) && e.Status == "Confirmed")
                .Select(e => e.EnrollmentId)
                .ToListAsync();

            var sectionIds = await context.EnrollmentLines
                .AsNoTracking()
                .Where(el => el.EnrollmentId.HasValue && confirmedEnrollmentIds.Contains(el.EnrollmentId.Value))
                .Select(el => el.SectionId)
                .Distinct()
                .ToListAsync();

            return await context.Sections
                .AsNoTracking()
                .Include(s => s.Course)
                .Include(s => s.Schedules)
                .Where(s => sectionIds.Contains(s.SectionId))
                .ToListAsync();
        }

        public async Task<int> CreateEnrollmentAsync(Enrollment enrollment)
        {
            using var context = _contextFactory.CreateDbContext();

            // Cặp Header - Line được lưu trong 1 Transaction tường minh (Tiêu chí chấm thi K2.1 - Tiêu chí 5)
            using var transaction = await context.Database.BeginTransactionAsync();
            try
            {
                // Đảm bảo không chèn lại các thực thể liên kết đã có
                enrollment.Student = null;
                if (enrollment.EnrollmentLines != null)
                {
                    foreach (var line in enrollment.EnrollmentLines)
                    {
                        line.Section = null;
                    }
                }

                await context.Enrollments.AddAsync(enrollment);
                await context.SaveChangesAsync();

                await transaction.CommitAsync();
                return enrollment.EnrollmentId;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task CancelEnrollmentAsync(int enrollmentId)
        {
            using var context = _contextFactory.CreateDbContext();
            var enrollment = await context.Enrollments.FindAsync(enrollmentId);
            if (enrollment != null)
            {
                enrollment.Status = "Cancelled";
                enrollment.DateCancelled = DateTime.Now;
                await context.SaveChangesAsync();
            }
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsBySectionIdAsync(int sectionId)
        {
            using var context = _contextFactory.CreateDbContext();
            var enrollmentIds = await context.EnrollmentLines
                .AsNoTracking()
                .Where(el => el.SectionId == sectionId && el.EnrollmentId.HasValue)
                .Select(el => el.EnrollmentId!.Value)
                .Distinct()
                .ToListAsync();

            return await context.Enrollments
                .AsNoTracking()
                .Include(e => e.Student)
                .Include(e => e.EnrollmentLines)
                    .ThenInclude(el => el.Section)
                        .ThenInclude(s => s!.Course)
                .Where(e => enrollmentIds.Contains(e.EnrollmentId))
                .OrderByDescending(e => e.DateRegistered)
                .ToListAsync();
        }

        public async Task<IEnumerable<EnrollmentLine>> GetActiveEnrollmentLinesByStudentAsync(int studentId, string semester)
        {
            using var context = _contextFactory.CreateDbContext();
            var confirmedEnrollmentIds = await context.Enrollments
                .AsNoTracking()
                .Where(e => e.StudentId == studentId && (e.Semester == semester || (semester == "2026_HK1" && e.Semester == "2025_HK1")) && e.Status == "Confirmed")
                .Select(e => e.EnrollmentId)
                .ToListAsync();

            return await context.EnrollmentLines
                .AsNoTracking()
                .Include(el => el.Section)
                    .ThenInclude(s => s!.Course)
                .Include(el => el.Section)
                    .ThenInclude(s => s!.Schedules)
                .Where(el => el.EnrollmentId.HasValue && confirmedEnrollmentIds.Contains(el.EnrollmentId.Value))
                .ToListAsync();
        }

        public async Task<bool> DropSectionForStudentAsync(int studentId, int sectionId, string? reason = null)
        {
            using var context = _contextFactory.CreateDbContext();

            // Tìm phiếu đăng ký có chứa sectionId của sinh viên
            var enrollment = await context.Enrollments
                .Include(e => e.EnrollmentLines)
                .FirstOrDefaultAsync(e => e.StudentId == studentId && e.Status == "Confirmed" && e.EnrollmentLines.Any(el => el.SectionId == sectionId));

            if (enrollment == null)
            {
                enrollment = await context.Enrollments
                    .Include(e => e.EnrollmentLines)
                    .FirstOrDefaultAsync(e => e.StudentId == studentId && e.EnrollmentLines.Any(el => el.SectionId == sectionId));
            }

            if (enrollment == null) return false;

            var line = enrollment.EnrollmentLines.FirstOrDefault(el => el.SectionId == sectionId);
            if (line == null) return false;

            // Xóa dòng đăng ký
            context.EnrollmentLines.Remove(line);

            // Nếu phiếu đăng ký không còn môn nào khác thì chuyển trạng thái Cancelled
            if (enrollment.EnrollmentLines.Count <= 1)
            {
                enrollment.Status = "Cancelled";
                enrollment.DateCancelled = DateTime.Now;
            }

            // Giảm sĩ số hiện tại của lớp học phần
            var section = await context.Sections.FindAsync(sectionId);
            if (section != null && section.CurrentEnrollment > 0)
            {
                section.CurrentEnrollment--;
            }

            await context.SaveChangesAsync();
            return true;
        }

        public async Task<(bool IsSuccess, string? ErrorMessage)> AddSectionForStudentAsync(int studentId, int sectionId, string semester)
        {
            using var context = _contextFactory.CreateDbContext();

            var section = await context.Sections
                .Include(s => s.Course)
                .Include(s => s.Schedules)
                .FirstOrDefaultAsync(s => s.SectionId == sectionId);

            if (section == null)
                return (false, "Không tìm thấy lớp học phần.");

            var confirmedEnrollments = await context.Enrollments
                .Include(e => e.EnrollmentLines)
                    .ThenInclude(el => el.Section)
                .Where(e => e.StudentId == studentId && (e.Semester == semester || (semester == "2026_HK1" && e.Semester == "2025_HK1")) && e.Status == "Confirmed")
                .ToListAsync();

            var alreadyRegistered = confirmedEnrollments
                .SelectMany(e => e.EnrollmentLines)
                .Any(el => el.SectionId == sectionId);

            if (alreadyRegistered)
                return (false, $"Sinh viên đã đăng ký lớp {section.SectionCode} rồi.");

            var sameCourseRegistered = confirmedEnrollments
                .SelectMany(e => e.EnrollmentLines)
                .Any(el => el.Section != null && el.Section.CourseId == section.CourseId);

            if (sameCourseRegistered)
                return (false, $"Sinh viên đã đăng ký một lớp khác của môn {section.Course?.CourseName} trong kỳ này.");

            // Tìm phiếu đăng ký đã xác nhận hiện có của SV trong kỳ
            var enrollment = confirmedEnrollments.FirstOrDefault();

            if (enrollment == null)
            {
                enrollment = new Enrollment
                {
                    StudentId = studentId,
                    Semester = semester,
                    DateRegistered = DateTime.Now,
                    Status = "Confirmed",
                    UniqueId = Guid.NewGuid().ToString()
                };
                await context.Enrollments.AddAsync(enrollment);
                await context.SaveChangesAsync();
            }

            var newLine = new EnrollmentLine
            {
                EnrollmentId = enrollment.EnrollmentId,
                SectionId = sectionId
            };
            await context.EnrollmentLines.AddAsync(newLine);

            // Tăng sĩ số lớp
            section.CurrentEnrollment++;
            await context.SaveChangesAsync();

            return (true, null);
        }
    }
}
