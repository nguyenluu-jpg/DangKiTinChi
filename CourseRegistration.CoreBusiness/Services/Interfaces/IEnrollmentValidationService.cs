using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.CoreBusiness.Services.Interfaces
{
    // Interface cho dịch vụ kiểm tra luật nghiệp vụ đăng ký học phần
    public interface IEnrollmentValidationService
    {
        // Kiểm tra tất cả luật nghiệp vụ trước khi đăng ký. Trả về danh sách lỗi (rỗng = hợp lệ)
        List<string> ValidateEnrollment( Section sectionToRegister, List<Section> alreadyRegisteredSections,
                   List<CompletedCourse> completedCourses, 
                   List<Prerequisite> prerequisites, int maxCreditsPerSemester);
    }
}
