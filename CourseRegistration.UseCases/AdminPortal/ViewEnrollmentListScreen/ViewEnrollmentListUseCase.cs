using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;

namespace CourseRegistration.UseCases.AdminPortal.ViewEnrollmentListScreen
{
    public interface IViewEnrollmentListUseCase
    {
        Task<IEnumerable<Enrollment>> ExecuteAsync(int sectionId);
        Task<IEnumerable<Enrollment>> GetBySemesterAsync(string semester);
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
    }
}
