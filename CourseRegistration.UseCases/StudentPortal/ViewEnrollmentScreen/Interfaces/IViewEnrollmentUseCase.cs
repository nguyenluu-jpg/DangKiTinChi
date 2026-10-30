using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.StudentPortal.ViewEnrollmentScreen.Interfaces
{
    public interface IViewEnrollmentUseCase
    {
        Task<Enrollment?> ExecuteAsync(int enrollmentId);
        Task<IEnumerable<Enrollment>> GetByStudentIdAsync(int studentId);
        Task<bool> DropSectionAsync(int studentId, int sectionId);
    }
}
