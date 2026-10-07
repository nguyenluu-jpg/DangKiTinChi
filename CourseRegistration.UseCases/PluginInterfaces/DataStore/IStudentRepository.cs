using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.PluginInterfaces.DataStore
{
    /// <summary>
    /// Interface Repository cho Student (sinh viên)
    /// </summary>
    public interface IStudentRepository
    {
        Task<IEnumerable<Student>> GetAllStudentsAsync();
        Task<Student?> GetStudentByIdAsync(int studentId);
        Task<Student?> GetStudentByCodeAsync(string studentCode);
        Task AddStudentAsync(Student student);
        Task UpdateStudentAsync(Student student);
        Task<IEnumerable<CompletedCourse>> GetCompletedCoursesByStudentIdAsync(int studentId);
    }
}
