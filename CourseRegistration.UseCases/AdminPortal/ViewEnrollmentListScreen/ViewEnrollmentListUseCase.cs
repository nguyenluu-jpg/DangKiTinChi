using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.AdminPortal.ViewEnrollmentListScreen
{
    public interface IViewEnrollmentListUseCase
    {
        Task<IEnumerable<Enrollment>> ExecuteAsync(int sectionId);
        Task<IEnumerable<Enrollment>> GetBySemesterAsync(string semester);
        Task<IEnumerable<EnrollmentLine>> GetStudentRegisteredLinesAsync(int studentId, string semester);
        Task<bool> DropSectionForStudentAsync(int studentId, int sectionId, string? reason = null);
        Task<(bool IsSuccess, string? ErrorMessage)> AddSectionForStudentAsync(int studentId, int sectionId, string semester);
    }

    public class ViewEnrollmentListUseCase : IViewEnrollmentListUseCase
    {
        private readonly IEnrollmentRepository _enrollmentRepository;

        public ViewEnrollmentListUseCase(IEnrollmentRepository enrollmentRepository)
        {
            _enrollmentRepository = enrollmentRepository;
        }

        public async Task<IEnumerable<Enrollment>> ExecuteAsync(int sectionId)
        {
            return await _enrollmentRepository.GetEnrollmentsBySectionIdAsync(sectionId);
        }

        public async Task<IEnumerable<Enrollment>> GetBySemesterAsync(string semester)
        {
            return await _enrollmentRepository.GetEnrollmentsBySemesterAsync(semester);
        }

        public async Task<IEnumerable<EnrollmentLine>> GetStudentRegisteredLinesAsync(int studentId, string semester)
        {
            return await _enrollmentRepository.GetActiveEnrollmentLinesByStudentAsync(studentId, semester);
        }

        public async Task<bool> DropSectionForStudentAsync(int studentId, int sectionId, string? reason = null)
        {
            return await _enrollmentRepository.DropSectionForStudentAsync(studentId, sectionId, reason);
        }

        public async Task<(bool IsSuccess, string? ErrorMessage)> AddSectionForStudentAsync(int studentId, int sectionId, string semester)
        {
            return await _enrollmentRepository.AddSectionForStudentAsync(studentId, sectionId, semester);
        }
    }
}
