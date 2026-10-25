using System;
using System.Collections.Generic;
using System.Linq;

namespace CourseRegistration.CoreBusiness.Models
{
    /// <summary>
    /// Thông tin chi tiết một Giai đoạn trong Đợt đăng ký tín chỉ
    /// </summary>
    public class RegistrationPhaseInfo
    {
        public int PhaseNumber { get; set; } // 1, 2, 3
        public string PhaseName { get; set; } = string.Empty; // "Giai đoạn 1: Đăng ký chính thức theo kế hoạch"
        public string ShortName { get; set; } = string.Empty; // "ĐK Chính Thức"
        public string Description { get; set; } = string.Empty;
        public string TargetAudience { get; set; } = string.Empty; // Đối tượng áp dụng
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        public bool IsActive => DateTime.Now >= StartDate && DateTime.Now <= EndDate;
        public bool IsClosed => DateTime.Now > EndDate;
        public bool IsUpcoming => DateTime.Now < StartDate;

        public string StatusBadgeText
        {
            get
            {
                if (IsActive) return "Đang Mở Đăng Ký";
                if (IsClosed) return "Đã Kết Thúc";
                return "Sắp Diễn Ra";
            }
        }

        public string StatusBadgeClass
        {
            get
            {
                if (IsActive) return "bg-success text-white";
                if (IsClosed) return "bg-secondary text-white";
                return "bg-warning text-dark";
            }
        }
    }

    /// <summary>
    /// Thông tin một Học kỳ đào tạo và các giai đoạn đăng ký tín chỉ
    /// </summary>
    public class SemesterInfo
    {
        public string SemesterCode { get; set; } = string.Empty; // "2026_HK1", "2026_HK2"
        public string SemesterName { get; set; } = string.Empty; // "Học kỳ 1 (Năm học 2026 - 2027)"
        public string ShortName { get; set; } = string.Empty; // "HK1 (2026-2027)"
        public string AcademicYear { get; set; } = "2026 - 2027";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public bool IsCurrentSemester { get; set; }
        public List<RegistrationPhaseInfo> Phases { get; set; } = new List<RegistrationPhaseInfo>();

        /// <summary>
        /// Giai đoạn đăng ký đang hoạt động (nếu có)
        /// </summary>
        public RegistrationPhaseInfo? ActivePhase => Phases.FirstOrDefault(p => p.IsActive);

        /// <summary>
        /// Kiểm tra học kỳ có đang trong thời gian mở đăng ký tín chỉ hay không
        /// </summary>
        public bool IsRegistrationOpen => Phases.Any(p => p.IsActive);
    }
}
