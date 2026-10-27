using System;
using System.Collections.Generic;
using System.Linq;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.CoreBusiness.Services.Interfaces;

namespace CourseRegistration.CoreBusiness.Services
{
    public class RegistrationPeriodService : IRegistrationPeriodService
    {
        public string CurrentSemesterCode { get; set; } = "2026_HK1";
        public int? SimulatedPhaseOverride { get; set; } = null;

        public List<SemesterInfo> GetAllSemesters()
        {
            return new List<SemesterInfo>
            {
                new SemesterInfo
                {
                    SemesterCode = "2026_HK1",
                    SemesterName = "Học kỳ 1 (Năm học 2026 - 2027)",
                    ShortName = "Học kỳ 1 (2026 - 2027)",
                    AcademicYear = "2026 - 2027",
                    StartDate = new DateTime(2026, 10, 15),
                    EndDate = new DateTime(2027, 2, 28),
                    IsCurrentSemester = true,
                    Phases = new List<RegistrationPhaseInfo>
                    {
                        new RegistrationPhaseInfo
                        {
                            PhaseNumber = 1,
                            PhaseName = "Giai đoạn 1: Đăng ký chính thức theo kế hoạch đào tạo",
                            ShortName = "Giai đoạn 1: ĐK Chính Thức",
                            Description = "Dành cho sinh viên đăng ký môn học theo đúng tiến độ chương trình đào tạo chuẩn.",
                            TargetAudience = "Sinh viên chính quy đăng ký môn học theo lộ trình chuẩn của khóa.",
                            StartDate = new DateTime(2026, 10, 15, 0, 0, 0),
                            EndDate = new DateTime(2026, 10, 26, 23, 59, 59)
                        },
                        new RegistrationPhaseInfo
                        {
                            PhaseNumber = 2,
                            PhaseName = "Giai đoạn 2: Đăng ký bổ sung, đổi lớp & Học vượt / Học lại",
                            ShortName = "Giai đoạn 2: ĐK Bổ Sung & Học Vượt",
                            Description = "Mở rộng cho sinh viên đăng ký thêm môn còn chỉ tiêu, đổi nhóm lớp, sinh viên học vượt hoặc học cải thiện điểm theo kế hoạch.",
                            TargetAudience = "Toàn bộ sinh viên có nhu cầu bổ sung môn, chuyển lớp hoặc học vượt.",
                            StartDate = new DateTime(2026, 10, 27, 0, 0, 0),
                            EndDate = new DateTime(2026, 11, 5, 23, 59, 59)
                        },
                        new RegistrationPhaseInfo
                        {
                            PhaseNumber = 3,
                            PhaseName = "Giai đoạn 3: Rút học phần, Hiệu chỉnh học vụ & Nộp học phí",
                            ShortName = "Giai đoạn 3: Rút Môn & Nộp Học Phí",
                            Description = "Xử lý đơn xin rút môn học, cứu xét học vụ quá hạn, thanh toán học phí VietQR và chốt danh sách thi học kỳ.",
                            TargetAudience = "Phòng Đào Tạo & Sinh viên hoàn tất nghĩa vụ học vụ.",
                            StartDate = new DateTime(2026, 11, 6, 0, 0, 0),
                            EndDate = new DateTime(2026, 11, 20, 23, 59, 59)
                        }
                    }
                },
                new SemesterInfo
                {
                    SemesterCode = "2026_HK2",
                    SemesterName = "Học kỳ 2 (Năm học 2026 - 2027)",
                    ShortName = "Học kỳ 2 (2026 - 2027)",
                    AcademicYear = "2026 - 2027",
                    StartDate = new DateTime(2026, 12, 15),
                    EndDate = new DateTime(2027, 6, 30),
                    IsCurrentSemester = false,
                    Phases = new List<RegistrationPhaseInfo>
                    {
                        new RegistrationPhaseInfo
                        {
                            PhaseNumber = 1,
                            PhaseName = "Giai đoạn 1: Đăng ký chính thức theo kế hoạch đào tạo HK2",
                            ShortName = "Giai đoạn 1: ĐK Chính Thức HK2",
                            Description = "Mở cổng đăng ký học phần chính khóa cho Học kỳ 2.",
                            TargetAudience = "Sinh viên chính quy toàn trường theo thời khóa biểu HK2.",
                            StartDate = new DateTime(2026, 12, 15, 0, 0, 0),
                            EndDate = new DateTime(2026, 12, 28, 23, 59, 59)
                        },
                        new RegistrationPhaseInfo
                        {
                            PhaseNumber = 2,
                            PhaseName = "Giai đoạn 2: Đăng ký bổ sung, đổi lớp & Học vượt HK2",
                            ShortName = "Giai đoạn 2: ĐK Bổ Sung HK2",
                            Description = "Đăng ký các môn học tự chọn, học vượt và ghép lớp học phần.",
                            TargetAudience = "Sinh viên đăng ký bổ sung và điều chỉnh nguyện vọng học tập.",
                            StartDate = new DateTime(2026, 12, 29, 0, 0, 0),
                            EndDate = new DateTime(2027, 1, 8, 23, 59, 59)
                        },
                        new RegistrationPhaseInfo
                        {
                            PhaseNumber = 3,
                            PhaseName = "Giai đoạn 3: Rút học phần & Chốt danh sách học phí HK2",
                            ShortName = "Giai đoạn 3: Rút Môn & Học Phí HK2",
                            Description = "Hoàn tất thủ tục rút môn không tính điểm rớt và nộp học phí tín chỉ HK2.",
                            TargetAudience = "Phòng Đào Tạo & Sinh viên thực hiện nghĩa vụ tài chính.",
                            StartDate = new DateTime(2027, 1, 9, 0, 0, 0),
                            EndDate = new DateTime(2027, 1, 22, 23, 59, 59)
                        }
                    }
                }
            };
        }

        public SemesterInfo GetCurrentSemester()
        {
            var semesters = GetAllSemesters();
            return semesters.FirstOrDefault(s => s.SemesterCode == CurrentSemesterCode) 
                   ?? semesters.First();
        }

        public SemesterInfo? GetSemester(string semesterCode)
        {
            return GetAllSemesters().FirstOrDefault(s => s.SemesterCode == semesterCode);
        }

        public RegistrationPhaseInfo? GetActivePhase(string semesterCode)
        {
            var semester = GetSemester(semesterCode);
            if (semester == null) return null;

            if (SimulatedPhaseOverride.HasValue && SimulatedPhaseOverride.Value >= 1 && SimulatedPhaseOverride.Value <= 3)
            {
                return semester.Phases.FirstOrDefault(p => p.PhaseNumber == SimulatedPhaseOverride.Value);
            }

            return semester.Phases.FirstOrDefault(p => p.IsActive) ?? semester.Phases.FirstOrDefault();
        }

        public bool IsRegistrationOpen(string semesterCode)
        {
            if (SimulatedPhaseOverride.HasValue) return true;

            var active = GetActivePhase(semesterCode);
            if (active != null) return active.IsActive;

            // Mặc định cho HK1 trong giai đoạn hiện tại (tháng 10 - tháng 11/2026) luôn mở
            if (semesterCode == "2026_HK1")
            {
                var now = DateTime.Now;
                return now >= new DateTime(2026, 10, 15) && now <= new DateTime(2026, 11, 20);
            }

            return false;
        }

        public void SetSimulatedPhase(int? phaseNumber)
        {
            SimulatedPhaseOverride = phaseNumber;
        }
    }
}
