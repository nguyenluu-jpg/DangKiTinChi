namespace CourseRegistration.CoreBusiness.Models
{
    // Học phần đã hoàn thành - CompletedCourse
    // Dùng để kiểm tra tiên quyết
    public class CompletedCourse
    {
        public int CompletedCourseId { get; set; }
        public int StudentId { get; set; }
        public int CourseId { get; set; }
        public string Semester { get; set; } = string.Empty; // HK đã hoàn thành
        public double Grade { get; set; } // Điểm đạt

        // Navigation properties
        public Student? Student { get; set; }
        public Course? Course { get; set; }
    }
}
