using System.ComponentModel.DataAnnotations;

namespace CourseRegistration.CoreBusiness.Models
{
    /// <summary>
    /// Lịch học của lớp học phần (SectionSchedule)
    /// Mỗi Section có thể có nhiều buổi học trong tuần
    /// </summary>
    public class SectionSchedule
    {
        public int ScheduleId { get; set; }
        public int SectionId { get; set; }

        [Required(ErrorMessage = "Ngày trong tuần không được để trống")]
        [Range(2, 8, ErrorMessage = "Thứ từ 2 (Thứ 2) đến 8 (Chủ nhật)")]
        public int DayOfWeek { get; set; } // 2 = Thứ 2, 3 = Thứ 3, ..., 8 = Chủ nhật

        [Required(ErrorMessage = "Tiết bắt đầu không được để trống")]
        [Range(1, 15, ErrorMessage = "Tiết bắt đầu từ 1 đến 15")]
        public int StartPeriod { get; set; } // Tiết bắt đầu (1-15)

        [Required(ErrorMessage = "Tiết kết thúc không được để trống")]
        [Range(1, 15, ErrorMessage = "Tiết kết thúc từ 1 đến 15")]
        public int EndPeriod { get; set; } // Tiết kết thúc (1-15)

        [StringLength(50)]
        public string Room { get; set; } = string.Empty;

        // Navigation property
        public Section? Section { get; set; }

        /// <summary>
        /// Hiển thị tên ngày trong tuần
        /// </summary>
        public string DayOfWeekName => DayOfWeek switch
        {
            2 => "Thứ 2",
            3 => "Thứ 3",
            4 => "Thứ 4",
            5 => "Thứ 5",
            6 => "Thứ 6",
            7 => "Thứ 7",
            8 => "Chủ nhật",
            _ => "Không xác định"
        };

        /// <summary>
        /// Kiểm tra 2 lịch có trùng nhau không (cùng thứ + tiết trùng)
        /// </summary>
        public bool ConflictsWith(SectionSchedule other)
        {
            if (DayOfWeek != other.DayOfWeek) return false;
            return StartPeriod <= other.EndPeriod && EndPeriod >= other.StartPeriod;
        }
    }
}
