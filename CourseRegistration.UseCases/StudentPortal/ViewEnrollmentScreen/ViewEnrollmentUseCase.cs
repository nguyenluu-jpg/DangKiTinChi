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

        public async Task<bool> DropSectionAsync(int studentId, int sectionId)
        {
            return await _enrollmentRepository.DropSectionForStudentAsync(studentId, sectionId, "Sinh viên tự hủy trong thời gian cho phép");
        }
    }
}
