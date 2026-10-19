namespace CourseRegistration.CoreBusiness.Models
{
    // Mô hình kết quả tra cứu và tư vấn thông minh dành cho Phòng Đào Tạo (Admin Copilot)
    public class AdminAiQueryResult
    {
        public string Query { get; set; } = string.Empty;
        public string AnswerSummary { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public List<AdminAiSectionItem> Sections { get; set; } = new List<AdminAiSectionItem>();
        public List<AdminAiStudentItem> Students { get; set; } = new List<AdminAiStudentItem>();
        public List<string> ActionableSuggestions { get; set; } = new List<string>();
        public bool IsAiGenerated { get; set; } = true;
        public string ModelUsed { get; set; } = string.Empty;
        public string? WarningMessage { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
    }

    public class AdminAiSectionItem
    {
        public int SectionId { get; set; }
        public string SectionCode { get; set; } = string.Empty;
        public string CourseName { get; set; } = string.Empty;
        public string Instructor { get; set; } = string.Empty;
        public int CurrentEnrollment { get; set; }
        public int MaxCapacity { get; set; }
        public double FillRate => MaxCapacity > 0 ? Math.Round((double)CurrentEnrollment / MaxCapacity * 100, 1) : 0;
        public string StatusBadge { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }

    public class AdminAiStudentItem
    {
        public int StudentId { get; set; }
        public string StudentCode { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;
        public int RegisteredCredits { get; set; }
        public int RegisteredCoursesCount { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
