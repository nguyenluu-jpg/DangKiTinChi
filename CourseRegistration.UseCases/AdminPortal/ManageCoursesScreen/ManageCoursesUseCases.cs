using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.AdminPortal.ManageCoursesScreen.Interfaces;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.AdminPortal.ManageCoursesScreen
{
    public class ViewCoursesUseCase : IViewCoursesUseCase
    {
        private readonly ICourseRepository _courseRepository;

        public ViewCoursesUseCase(ICourseRepository courseRepository)
        {
            _courseRepository = courseRepository;
        }

        public async Task<IEnumerable<Course>> ExecuteAsync()
        {
            return await _courseRepository.GetAllCoursesAsync();
        }
    }

    public class AddCourseUseCase : IAddCourseUseCase
    {
        private readonly ICourseRepository _courseRepository;

        public AddCourseUseCase(ICourseRepository courseRepository)
        {
            _courseRepository = courseRepository;
        }

        public async Task ExecuteAsync(Course course)
        {
            await _courseRepository.AddCourseAsync(course);
        }
    }
}
