using System.ComponentModel.DataAnnotations;

namespace CourseRegistration.CoreBusiness.Models
{
    /// <summary>
    /// Tài khoản người dùng hệ thống (Sprint 2 - Phân quyền Quản trị & Sinh viên)
    /// </summary>
    public class UserAccount
    {
        public int AccountId { get; set; }

        [Required(ErrorMessage = "Tên đăng nhập không được để trống")]
        [StringLength(50, ErrorMessage = "Tên đăng nhập tối đa 50 ký tự")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mật khẩu không được để trống")]
        [StringLength(100, ErrorMessage = "Mật khẩu tối đa 100 ký tự")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Họ tên không được để trống")]
        [StringLength(100, ErrorMessage = "Họ tên tối đa 100 ký tự")]
        public string FullName { get; set; } = string.Empty;

        [Required]
        [StringLength(20)]
        public string Role { get; set; } = "Student"; // "Admin" hoặc "Student"

        // Nếu là sinh viên thì liên kết với StudentId
        public int? StudentId { get; set; }
        public Student? Student { get; set; }
    }
}
