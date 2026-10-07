using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.StudentPortal.ViewScheduleScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.ViewScheduleScreen
{
    public class ViewScheduleUseCase : IViewScheduleUseCase
    {
        private readonly IEnrollmentRepository _enrollmentRepository;

        public ViewScheduleUseCase(IEnrollmentRepository enrollmentRepository)
        {
            _enrollmentRepository = enrollmentRepository;
        }

        public async Task<IEnumerable<Section>> ExecuteAsync(int studentId, string semester)
        {
            return await _enrollmentRepository.GetRegisteredSectionsByStudentAsync(studentId, semester);
        }
    }
}
