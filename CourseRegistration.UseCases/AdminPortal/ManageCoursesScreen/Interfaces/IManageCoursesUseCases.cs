using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.AdminPortal.ManageCoursesScreen.Interfaces
{
    public interface IViewCoursesUseCase
    {
        Task<IEnumerable<Course>> ExecuteAsync();
    }

    public interface IAddCourseUseCase
    {
        Task ExecuteAsync(Course course);
    }
}
