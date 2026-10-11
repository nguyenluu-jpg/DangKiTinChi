using System.ComponentModel.DataAnnotations;

namespace CourseRegistration.CoreBusiness.Models
{
    // Sinh viên 
    public class Student
    {
        public int StudentId { get; set; }

        [Required(ErrorMessage = "Mã sinh viên không được để trống")]
        [StringLength(20, ErrorMessage = "Mã sinh viên tối đa 20 ký tự")]
        public string StudentCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Họ tên không được để trống")]
        [StringLength(100, ErrorMessage = "Họ tên tối đa 100 ký tự")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email không được để trống")]
        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        public string Email { get; set; } = string.Empty;

        [StringLength(20)]
        public string PhoneNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Khóa học không được để trống")]
        [StringLength(20)]
        public string AcademicYear { get; set; } = string.Empty; // Khóa

        [StringLength(100)]
        public string Major { get; set; } = string.Empty; // Ngành

        // Số tín chỉ tối đa được đăng ký trong 1 kỳ
        public int MaxCreditsPerSemester { get; set; } = 24;
    }
}
