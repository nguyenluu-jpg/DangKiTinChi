using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.PluginInterfaces.DataStore
{
    /// <summary>
    /// Interface Repository cho Enrollment (phiếu đăng ký)
    /// </summary>
    public interface IEnrollmentRepository
    {
        Task<Enrollment?> GetEnrollmentByIdAsync(int enrollmentId);
        Task<IEnumerable<Enrollment>> GetEnrollmentsByStudentIdAsync(int studentId);
        Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester);
        Task<IEnumerable<Section>> GetRegisteredSectionsByStudentAsync(int studentId, string semester);

        /// <summary>
        /// Tạo phiếu đăng ký mới (header + lines trong 1 transaction - K2.2)
        /// </summary>
        Task<int> CreateEnrollmentAsync(Enrollment enrollment);

        /// <summary>
        /// Hủy đăng ký (trong thời gian cho phép)
        /// </summary>
        Task CancelEnrollmentAsync(int enrollmentId);

        /// <summary>
        /// Lấy danh sách sinh viên đã đăng ký một lớp học phần
        /// </summary>
        Task<IEnumerable<Enrollment>> GetEnrollmentsBySectionIdAsync(int sectionId);
    }
}
