using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.DataStore.HardCoded
{
    public class StudentRepository : IStudentRepository
    {
        private static readonly List<Student> _students = new List<Student>
        {
            //  Đã hoàn thành TIN101 và TIN102 -> Đủ điều kiện học WEB301, KTPM302
            new Student
            {
                StudentId = 1,
                StudentCode = "SV21001",
                FullName = "Nguyễn Văn Tuấn",
                Email = "tuan.nv@student.edu.vn",
                PhoneNumber = "0901234567",
                AcademicYear = "K21",
                Major = "Công nghệ Thông tin",
                MaxCreditsPerSemester = 24
            },
            // MỚI CHỈ HOÀN THÀNH TIN101 (CHƯA HỌC TIN102) -> DEMO VI PHẠM TIÊN QUYẾT KHI ĐĂNG KÝ WEB301!
            new Student
            {
                StudentId = 2,
                StudentCode = "SV22002",
                FullName = "Trần Thị Mai",
                Email = "mai.tt@student.edu.vn",
                PhoneNumber = "0912345678",
                AcademicYear = "K22",
                Major = "Hệ thống Thông tin",
                MaxCreditsPerSemester = 20
            }
        };

        private static readonly List<CompletedCourse> _completedCourses = new List<CompletedCourse>
        {
            // SV 1 đã học xong TIN101 và TIN102
            new CompletedCourse { CompletedCourseId = 1, StudentId = 1, CourseId = 1, Semester = "2023_HK1", Grade = 8.5 },
            new CompletedCourse { CompletedCourseId = 2, StudentId = 1, CourseId = 2, Semester = "2023_HK2", Grade = 7.5 },
            
            // SV 2 chỉ mới học TIN101, CHƯA HỌC TIN102
            new CompletedCourse { CompletedCourseId = 3, StudentId = 2, CourseId = 1, Semester = "2024_HK1", Grade = 9.0 }
        };

        public Task<IEnumerable<Student>> GetAllStudentsAsync()
        {
            return Task.FromResult<IEnumerable<Student>>(_students.ToList());
        }

        public Task<Student?> GetStudentByIdAsync(int studentId)
        {
            var student = _students.FirstOrDefault(s => s.StudentId == studentId);
            return Task.FromResult(student);
        }

        public Task<Student?> GetStudentByCodeAsync(string studentCode)
        {
            var student = _students.FirstOrDefault(s => s.StudentCode.Equals(studentCode, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(student);
        }

        public Task AddStudentAsync(Student student)
        {
            student.StudentId = _students.Any() ? _students.Max(s => s.StudentId) + 1 : 1;
            _students.Add(student);
            return Task.CompletedTask;
        }

        public Task UpdateStudentAsync(Student student)
        {
            var existing = _students.FirstOrDefault(s => s.StudentId == student.StudentId);
            if (existing != null)
            {
                existing.FullName = student.FullName;
                existing.Email = student.Email;
                existing.PhoneNumber = student.PhoneNumber;
                existing.Major = student.Major;
                existing.MaxCreditsPerSemester = student.MaxCreditsPerSemester;
            }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<CompletedCourse>> GetCompletedCoursesByStudentIdAsync(int studentId)
        {
            var list = _completedCourses.Where(cc => cc.StudentId == studentId).ToList();
            return Task.FromResult<IEnumerable<CompletedCourse>>(list);
        }
    }
}
