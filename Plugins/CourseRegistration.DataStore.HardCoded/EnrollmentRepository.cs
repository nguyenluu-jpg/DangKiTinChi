using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.DataStore.HardCoded
{
    public class EnrollmentRepository : IEnrollmentRepository
    {
        private static readonly List<Enrollment> _enrollments = new List<Enrollment>();
        private static readonly List<EnrollmentLine> _enrollmentLines = new List<EnrollmentLine>();
        private static readonly object _lock = new object();

        private readonly ISectionRepository _sectionRepository;
        private readonly IStudentRepository _studentRepository;

        public EnrollmentRepository(ISectionRepository sectionRepository, IStudentRepository studentRepository)
        {
            _sectionRepository = sectionRepository;
            _studentRepository = studentRepository;
        }

        public async Task<Enrollment?> GetEnrollmentByIdAsync(int enrollmentId)
        {
            var enrollment = _enrollments.FirstOrDefault(e => e.EnrollmentId == enrollmentId);
            if (enrollment == null) return null;

            enrollment.Student = await _studentRepository.GetStudentByIdAsync(enrollment.StudentId);
            var lines = _enrollmentLines.Where(l => l.EnrollmentId == enrollmentId).ToList();
            foreach (var line in lines)
            {
                line.Section = await _sectionRepository.GetSectionByIdAsync(line.SectionId);
            }
            enrollment.EnrollmentLines = lines;
            return enrollment;
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsByStudentIdAsync(int studentId)
        {
            var list = _enrollments.Where(e => e.StudentId == studentId).ToList();
            foreach (var e in list)
            {
                e.Student = await _studentRepository.GetStudentByIdAsync(e.StudentId);
                var lines = _enrollmentLines.Where(l => l.EnrollmentId == e.EnrollmentId).ToList();
                foreach (var line in lines)
                {
                    line.Section = await _sectionRepository.GetSectionByIdAsync(line.SectionId);
                }
                e.EnrollmentLines = lines;
            }
            return list;
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester)
        {
            var list = _enrollments.Where(e => e.Semester.Equals(semester, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var e in list)
            {
                e.Student = await _studentRepository.GetStudentByIdAsync(e.StudentId);
            }
            return list;
        }

        public async Task<IEnumerable<Section>> GetRegisteredSectionsByStudentAsync(int studentId, string semester)
        {
            var confirmedEnrollments = _enrollments
                .Where(e => e.StudentId == studentId && (e.Semester.Equals(semester, StringComparison.OrdinalIgnoreCase) || (semester == "2026_HK1" && e.Semester.Equals("2025_HK1", StringComparison.OrdinalIgnoreCase))) && e.Status == "Confirmed")
                .Select(e => e.EnrollmentId).ToList();

            var sectionIds = _enrollmentLines
                .Where(l => l.EnrollmentId.HasValue && confirmedEnrollments.Contains(l.EnrollmentId.Value))
                .Select(l => l.SectionId).Distinct().ToList();

            var sections = new List<Section>();
            foreach (var sId in sectionIds)
            {
                var sec = await _sectionRepository.GetSectionByIdAsync(sId);
                if (sec != null) sections.Add(sec);
            }
            return sections;
        }

        public Task<int> CreateEnrollmentAsync(Enrollment enrollment)
        {
            lock (_lock)
            {
                int newEnrollmentId = _enrollments.Any() ? _enrollments.Max(e => e.EnrollmentId) + 1 : 1;
                enrollment.EnrollmentId = newEnrollmentId;
                _enrollments.Add(enrollment);

                foreach (var line in enrollment.EnrollmentLines)
                {
                    int newLineId = _enrollmentLines.Any() ? _enrollmentLines.Max(l => l.EnrollmentLineId) + 1 : 1;
                    line.EnrollmentLineId = newLineId;
                    line.EnrollmentId = newEnrollmentId;
                    _enrollmentLines.Add(line);
                }

                return Task.FromResult(newEnrollmentId);
            }
        }

        public Task CancelEnrollmentAsync(int enrollmentId)
        {
            lock (_lock)
            {
                var existing = _enrollments.FirstOrDefault(e => e.EnrollmentId == enrollmentId);
                if (existing != null)
                {
                    existing.Status = "Cancelled";
                }
            }
            return Task.CompletedTask;
        }

        public async Task<IEnumerable<Enrollment>> GetEnrollmentsBySectionIdAsync(int sectionId)
        {
            var enrollmentIds = _enrollmentLines
                .Where(l => l.SectionId == sectionId && l.EnrollmentId.HasValue)
                .Select(l => l.EnrollmentId!.Value).Distinct().ToList();

            var list = new List<Enrollment>();
            foreach (var eId in enrollmentIds)
            {
                var e = await GetEnrollmentByIdAsync(eId);
                if (e != null && e.Status == "Confirmed")
                {
                    list.Add(e);
                }
            }
            return list;
        }

        public async Task<IEnumerable<EnrollmentLine>> GetActiveEnrollmentLinesByStudentAsync(int studentId, string semester)
        {
            var confirmedEnrollmentIds = _enrollments
                .Where(e => e.StudentId == studentId && (e.Semester.Equals(semester, StringComparison.OrdinalIgnoreCase) || (semester == "2026_HK1" && e.Semester.Equals("2025_HK1", StringComparison.OrdinalIgnoreCase))) && e.Status == "Confirmed")
                .Select(e => e.EnrollmentId)
                .ToList();

            var lines = _enrollmentLines
                .Where(l => l.EnrollmentId.HasValue && confirmedEnrollmentIds.Contains(l.EnrollmentId.Value))
                .ToList();

            foreach (var line in lines)
            {
                line.Section = await _sectionRepository.GetSectionByIdAsync(line.SectionId);
            }

            return lines;
        }

        public async Task<bool> DropSectionForStudentAsync(int studentId, int sectionId, string? reason = null)
        {
            lock (_lock)
            {
                var enrollment = _enrollments.FirstOrDefault(e => e.StudentId == studentId && e.Status == "Confirmed");
                if (enrollment == null) return false;

                var line = _enrollmentLines.FirstOrDefault(l => l.EnrollmentId == enrollment.EnrollmentId && l.SectionId == sectionId);
                if (line == null) return false;

                _enrollmentLines.Remove(line);
                _sectionRepository.DecrementEnrollmentCountAsync(sectionId);
                return true;
            }
        }

        public Task<(bool IsSuccess, string? ErrorMessage)> AddSectionForStudentAsync(int studentId, int sectionId, string semester)
        {
            lock (_lock)
            {
                var enrollment = _enrollments.FirstOrDefault(e => e.StudentId == studentId && e.Semester.Equals(semester, StringComparison.OrdinalIgnoreCase) && e.Status == "Confirmed");
                if (enrollment == null)
                {
                    int newId = _enrollments.Any() ? _enrollments.Max(e => e.EnrollmentId) + 1 : 1;
                    enrollment = new Enrollment
                    {
                        EnrollmentId = newId,
                        StudentId = studentId,
                        Semester = semester,
                        Status = "Confirmed",
                        DateRegistered = DateTime.Now
                    };
                    _enrollments.Add(enrollment);
                }

                int newLineId = _enrollmentLines.Any() ? _enrollmentLines.Max(l => l.EnrollmentLineId) + 1 : 1;
                _enrollmentLines.Add(new EnrollmentLine
                {
                    EnrollmentLineId = newLineId,
                    EnrollmentId = enrollment.EnrollmentId,
                    SectionId = sectionId
                });

                _sectionRepository.IncrementEnrollmentCountAsync(sectionId);
                return Task.FromResult((true, (string?)null));
            }
        }
    }
}
