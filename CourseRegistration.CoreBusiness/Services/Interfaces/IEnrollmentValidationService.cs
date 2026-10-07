using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.CoreBusiness.Services.Interfaces
{
    /// <summary>
    /// Interface cho dịch vụ kiểm tra luật nghiệp vụ đăng ký học phần
    /// </summary>
    public interface IEnrollmentValidationService
    {
        /// <summary>
        /// Kiểm tra tất cả luật nghiệp vụ trước khi đăng ký
        /// Trả về danh sách lỗi (rỗng = hợp lệ)
        /// </summary>
        List<string> ValidateEnrollment(
            Section sectionToRegister,
            List<Section> alreadyRegisteredSections,
            List<CompletedCourse> completedCourses,
            List<Prerequisite> prerequisites,
            int maxCreditsPerSemester);
    }
}
