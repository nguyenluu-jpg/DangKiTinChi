using System.ComponentModel.DataAnnotations;

namespace CourseRegistration.CoreBusiness.Models
{
    // Lớp học phần (Section) - ánh xạ Product trong eShop
    // Mỗi Section thuộc về 1 Course, có giảng viên, sĩ số tối đa
    public class Section
    {
        public int SectionId { get; set; }

        [Required(ErrorMessage = "Mã lớp học phần không được để trống")]
        [StringLength(20, ErrorMessage = "Mã lớp tối đa 20 ký tự")]
        public string SectionCode { get; set; } = string.Empty;

        public int CourseId { get; set; }

        [Required(ErrorMessage = "Tên giảng viên không được để trống")]
        [StringLength(100, ErrorMessage = "Tên giảng viên tối đa 100 ký tự")]
        public string Instructor { get; set; } = string.Empty;

        [Required(ErrorMessage = "Sĩ số tối đa không được để trống")]
        [Range(1, 200, ErrorMessage = "Sĩ số tối đa từ 1 đến 200")]
        public int MaxCapacity { get; set; }

        public int CurrentEnrollment { get; set; } = 0;

        [StringLength(50)]
        public string Room { get; set; } = string.Empty;

        public string Semester { get; set; } = string.Empty;

        // Navigation properties
        public Course? Course { get; set; }
        public List<SectionSchedule> Schedules { get; set; } = new List<SectionSchedule>();

        // Kiểm tra lớp còn chỗ trống không
        public bool HasAvailableSlot => CurrentEnrollment < MaxCapacity;

        // Số chỗ còn trống
        public int AvailableSlots => MaxCapacity - CurrentEnrollment;
    }
}
