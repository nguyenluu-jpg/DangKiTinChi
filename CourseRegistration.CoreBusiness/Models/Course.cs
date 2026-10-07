using System.ComponentModel.DataAnnotations;

namespace CourseRegistration.CoreBusiness.Models
{
    // Học phần (môn học) - Course
    public class Course
    {
        public int CourseId { get; set; }

        [Required(ErrorMessage = "Mã học phần không được để trống")]
        [StringLength(10, ErrorMessage = "Mã học phần tối đa 10 ký tự")]
        public string CourseCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Tên học phần không được để trống")]
        [StringLength(200, ErrorMessage = "Tên học phần tối đa 200 ký tự")]
        public string CourseName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Số tín chỉ không được để trống")]
        [Range(1, 10, ErrorMessage = "Số tín chỉ từ 1 đến 10")]
        public int Credits { get; set; }

        [StringLength(500)]
        public string Description { get; set; } = string.Empty;
    }
}
