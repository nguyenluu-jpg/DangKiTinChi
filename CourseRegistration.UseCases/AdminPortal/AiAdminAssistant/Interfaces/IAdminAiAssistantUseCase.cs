using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.AdminPortal.AiAdminAssistant.Interfaces
{
    /// <summary>
    /// UseCase Trợ lý AI Hỗ trợ Phòng Đào Tạo (Admin Copilot)
    /// Tra cứu nhanh dữ liệu học vụ, thống kê sĩ số lớp, phát hiện lớp nguy cơ/quá tải và đề xuất mở lớp.
    /// Tuân thủ Clean Architecture và tiêu chí inject IChatClient.
    /// </summary>
    public interface IAdminAiAssistantUseCase
    {
        Task<AdminAiQueryResult> ExecuteAsync(string query, string semester = "2026_HK1");
    }
}
