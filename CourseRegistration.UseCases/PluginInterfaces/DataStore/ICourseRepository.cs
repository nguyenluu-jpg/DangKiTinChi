using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.PluginInterfaces.DataStore
{
    /// <summary>
    /// Interface Repository cho Course (học phần)
    /// Nằm trong UseCases, implementation ở Plugins (K1.2)
    /// </summary>
    public interface ICourseRepository
    {
        Task<IEnumerable<Course>> GetAllCoursesAsync();
        Task<Course?> GetCourseByIdAsync(int courseId);
        Task<IEnumerable<Course>> SearchCoursesAsync(string keyword);
        Task AddCourseAsync(Course course);
        Task UpdateCourseAsync(Course course);
        Task DeleteCourseAsync(int courseId);
        Task<IEnumerable<Prerequisite>> GetPrerequisitesByCourseIdAsync(int courseId);
        Task<IEnumerable<Prerequisite>> GetAllPrerequisitesAsync();
        Task AddPrerequisiteAsync(Prerequisite prerequisite);
        Task DeletePrerequisiteAsync(int prerequisiteId);
    }
}
