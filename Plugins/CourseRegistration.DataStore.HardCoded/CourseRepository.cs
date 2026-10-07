using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.DataStore.HardCoded
{
    public class CourseRepository : ICourseRepository
    {
        private static readonly List<Course> _courses = new List<Course>
        {
            new Course { CourseId = 1, CourseCode = "TIN101", CourseName = "Nhập môn Lập trình C#", Credits = 3, Description = "Kiến thức cơ bản về lập trình C# và tư duy thuật toán" },
            new Course { CourseId = 2, CourseCode = "TIN102", CourseName = "Lập trình Hướng đối tượng (OOP)", Credits = 3, Description = "Lập trình đối tượng, 4 tính chất OOP và Design Patterns cơ bản" },
            new Course { CourseId = 3, CourseCode = "CSDL201", CourseName = "Cơ sở Dữ liệu & SQL Server", Credits = 3, Description = "Mô hình quan hệ, chuẩn hóa 3NF, SQL và Indexing" },
            new Course { CourseId = 4, CourseCode = "TIN201", CourseName = "Cấu trúc Dữ liệu & Giải thuật", Credits = 3, Description = "Danh sách liên kết, cây nhị phân, thuật toán tìm kiếm và sắp xếp" },
            new Course { CourseId = 5, CourseCode = "WEB301", CourseName = "Lập trình Ứng dụng Web (Blazor & Clean Architecture)", Credits = 4, Description = "Xây dựng Web App hiện đại với Blazor Server, Clean Architecture và Dapper" },
            new Course { CourseId = 6, CourseCode = "KTPM302", CourseName = "Công nghệ Phần mềm & Scrum", Credits = 3, Description = "Quy trình phát triển phần mềm Agile/Scrum, thiết kế UML và kiểm thử" }
        };

        private static readonly List<Prerequisite> _prerequisites = new List<Prerequisite>
        {
            // OOP (TIN102) cần hoàn thành Nhập môn Lập trình (TIN101)
            new Prerequisite { PrerequisiteId = 1, CourseId = 2, PrerequisiteCourseId = 1 },
            // Cấu trúc dữ liệu (TIN201) cần hoàn thành Nhập môn Lập trình (TIN101)
            new Prerequisite { PrerequisiteId = 2, CourseId = 4, PrerequisiteCourseId = 1 },
            // Lập trình Web (WEB301) cần hoàn thành OOP (TIN102)
            new Prerequisite { PrerequisiteId = 3, CourseId = 5, PrerequisiteCourseId = 2 },
            // Công nghệ phần mềm (KTPM302) cần hoàn thành OOP (TIN102)
            new Prerequisite { PrerequisiteId = 4, CourseId = 6, PrerequisiteCourseId = 2 }
        };

        static CourseRepository()
        {
            // Liên kết Navigation property cho Prerequisites
            foreach (var p in _prerequisites)
            {
                p.Course = _courses.FirstOrDefault(c => c.CourseId == p.CourseId);
                p.PrerequisiteCourse = _courses.FirstOrDefault(c => c.CourseId == p.PrerequisiteCourseId);
            }
        }

        public Task<IEnumerable<Course>> GetAllCoursesAsync()
        {
            return Task.FromResult<IEnumerable<Course>>(_courses.ToList());
        }

        public Task<Course?> GetCourseByIdAsync(int courseId)
        {
            var course = _courses.FirstOrDefault(c => c.CourseId == courseId);
            return Task.FromResult(course);
        }

        public Task<IEnumerable<Course>> SearchCoursesAsync(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return Task.FromResult<IEnumerable<Course>>(_courses.ToList());

            var result = _courses.Where(c =>
                c.CourseCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                c.CourseName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult<IEnumerable<Course>>(result.ToList());
        }

        public Task AddCourseAsync(Course course)
        {
            course.CourseId = _courses.Any() ? _courses.Max(c => c.CourseId) + 1 : 1;
            _courses.Add(course);
            return Task.CompletedTask;
        }

        public Task UpdateCourseAsync(Course course)
        {
            var existing = _courses.FirstOrDefault(c => c.CourseId == course.CourseId);
            if (existing != null)
            {
                existing.CourseCode = course.CourseCode;
                existing.CourseName = course.CourseName;
                existing.Credits = course.Credits;
                existing.Description = course.Description;
            }
            return Task.CompletedTask;
        }

        public Task DeleteCourseAsync(int courseId)
        {
            var existing = _courses.FirstOrDefault(c => c.CourseId == courseId);
            if (existing != null)
            {
                _courses.Remove(existing);
            }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Prerequisite>> GetPrerequisitesByCourseIdAsync(int courseId)
        {
            var list = _prerequisites.Where(p => p.CourseId == courseId).ToList();
            return Task.FromResult<IEnumerable<Prerequisite>>(list);
        }

        public Task<IEnumerable<Prerequisite>> GetAllPrerequisitesAsync()
        {
            return Task.FromResult<IEnumerable<Prerequisite>>(_prerequisites.ToList());
        }

        public Task AddPrerequisiteAsync(Prerequisite prerequisite)
        {
            prerequisite.PrerequisiteId = _prerequisites.Any() ? _prerequisites.Max(p => p.PrerequisiteId) + 1 : 1;
            prerequisite.Course = _courses.FirstOrDefault(c => c.CourseId == prerequisite.CourseId);
            prerequisite.PrerequisiteCourse = _courses.FirstOrDefault(c => c.CourseId == prerequisite.PrerequisiteCourseId);
            _prerequisites.Add(prerequisite);
            return Task.CompletedTask;
        }

        public Task DeletePrerequisiteAsync(int prerequisiteId)
        {
            var existing = _prerequisites.FirstOrDefault(p => p.PrerequisiteId == prerequisiteId);
            if (existing != null)
            {
                _prerequisites.Remove(existing);
            }
            return Task.CompletedTask;
        }
    }
}
