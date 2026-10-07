using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.StudentPortal.ViewEnrollmentScreen.Interfaces;

namespace CourseRegistration.UseCases.StudentPortal.ViewEnrollmentScreen
{
    public class ViewEnrollmentUseCase : IViewEnrollmentUseCase
    {
        private readonly IEnrollmentRepository _enrollmentRepository;

        public ViewEnrollmentUseCase(IEnrollmentRepository enrollmentRepository)
        {
            _enrollmentRepository = enrollmentRepository;
        }

        public async Task<Enrollment?> ExecuteAsync(int enrollmentId)
        {
            return await _enrollmentRepository.GetEnrollmentByIdAsync(enrollmentId);
        }

        public async Task<IEnumerable<Enrollment>> GetByStudentIdAsync(int studentId)
        {
            return await _enrollmentRepository.GetEnrollmentsByStudentIdAsync(studentId);
        }
    }
}
