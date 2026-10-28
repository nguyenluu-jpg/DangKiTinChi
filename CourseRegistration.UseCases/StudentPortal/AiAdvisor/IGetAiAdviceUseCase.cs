using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.UseCases.StudentPortal.AiAdvisor
{
    /// <summary>
    /// UseCase tư vấn đăng ký học phần thông minh bằng AI
    /// Được inject IChatClient (Microsoft.Extensions.AI), tuân thủ Clean Architecture.
    /// Không gọi trực tiếp từ file .razor (K1.1).
    /// </summary>
    public interface IGetAiAdviceUseCase
    {
        Task<AiCourseAdvice> ExecuteAsync(
            int studentId, 
            string semester, 
            string? studentGoalPrompt = null,
            List<AiConversationMessage>? conversationHistory = null);
    }
}
