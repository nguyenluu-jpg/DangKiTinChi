namespace CourseRegistration.CoreBusiness.Models
{
    // Mô hình kết quả tư vấn của Cố vấn Học tập AI
    public class AiCourseAdvice
    {
        public int StudentId { get; set; }
        public string StudentCode { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string Semester { get; set; } = string.Empty;

        // Nội dung phân tích, nhận xét và lời khuyên tổng quan từ AI
        public string AiMessage { get; set; } = string.Empty;

        // Danh sách các lớp học phần được đề xuất (chỉ mang tính chất gợi ý)
        public List<AiRecommendedSection> RecommendedSections { get; set; } = new List<AiRecommendedSection>();

        // Tổng số tín chỉ của các môn được gợi ý
        public int TotalCredits => RecommendedSections.Sum(s => s.Credits);

        // Đánh dấu kết quả sinh trực tiếp từ LLM hay từ hệ thống quy tắc thông minh dự phòng
        public bool IsAiGenerated { get; set; } = true;

        // Tên mô hình AI đã xử lý (ví dụ: llama3, gpt-4o-mini, hoặc Rule-Based Heuristic)
        public string ModelUsed { get; set; } = string.Empty;

        // Cảnh báo thân thiện nếu API lỗi/hết hạn mức (để ứng dụng không bao giờ bị dừng)
        public string? WarningMessage { get; set; }

        public DateTime GeneratedAt { get; set; } = DateTime.Now;
    }

    // Chi tiết lớp học phần được AI gợi ý
    public class AiRecommendedSection
    {
        public int SectionId { get; set; }
        public string SectionCode { get; set; } = string.Empty;
        public int CourseId { get; set; }
        public string CourseCode { get; set; } = string.Empty;
        public string CourseName { get; set; } = string.Empty;
        public int Credits { get; set; }
        public string Instructor { get; set; } = string.Empty;
        public string Room { get; set; } = string.Empty;
        public string ScheduleSummary { get; set; } = string.Empty;
        
        // Lý do cụ thể môn này được gợi ý cho sinh viên
        public string RecommendationReason { get; set; } = string.Empty;

        // Trạng thái được chọn trước khi người dùng xác nhận ghi vào CSDL (Mặc định chọn)
        public bool IsSelected { get; set; } = true;
    }
}
