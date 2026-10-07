namespace CourseRegistration.CoreBusiness.Models
{
    /// <summary>
    /// Tiên quyết - Prerequisite
    /// Liên kết giữa các học phần: CourseId yêu cầu phải hoàn thành PrerequisiteCourseId trước
    /// </summary>
    public class Prerequisite
    {
        public int PrerequisiteId { get; set; }
        public int CourseId { get; set; }
        public int PrerequisiteCourseId { get; set; }

        // Navigation properties
        public Course? Course { get; set; }
        public Course? PrerequisiteCourse { get; set; }
    }
}
