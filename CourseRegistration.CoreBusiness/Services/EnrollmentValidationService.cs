using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.CoreBusiness.Services.Interfaces;

namespace CourseRegistration.CoreBusiness.Services
{
    /// <summary>
    /// ========================================================
    /// LUẬT NGHIỆP VỤ 1: Kiểm tra đăng ký học phần
    /// (1) Chặn khi chưa đạt học phần tiên quyết
    /// (2) Chặn khi trùng lịch với lớp đã đăng ký
    /// (3) Chặn khi vượt số tín chỉ tối đa của kỳ
    /// ========================================================
    /// Luật này nằm trong CoreBusiness, KHÔNG nằm trong .razor (K1.3)
    /// </summary>
    public class EnrollmentValidationService : IEnrollmentValidationService
    {
        /// <summary>
        /// Kiểm tra tất cả luật nghiệp vụ trước khi cho phép đăng ký 1 lớp học phần
        /// </summary>
        /// <param name="sectionToRegister">Lớp muốn đăng ký</param>
        /// <param name="alreadyRegisteredSections">Các lớp đã đăng ký trong kỳ</param>
        /// <param name="completedCourses">Các học phần SV đã hoàn thành</param>
        /// <param name="prerequisites">Danh sách tiên quyết của học phần</param>
        /// <param name="maxCreditsPerSemester">Số tín chỉ tối đa trong kỳ</param>
        /// <returns>Danh sách lỗi vi phạm (rỗng = hợp lệ)</returns>
        public List<string> ValidateEnrollment(
            Section sectionToRegister,
            List<Section> alreadyRegisteredSections,
            List<CompletedCourse> completedCourses,
            List<Prerequisite> prerequisites,
            int maxCreditsPerSemester)
        {
            var errors = new List<string>();

            // Kiểm tra học phần tiên quyết 
            ValidatePrerequisites(sectionToRegister, completedCourses, prerequisites, errors);

            //Kiểm tra trùng lịch
            ValidateScheduleConflict(sectionToRegister, alreadyRegisteredSections, errors);

            // Kiểm tra vượt số tín chỉ tối đa 
            ValidateMaxCredits(sectionToRegister, alreadyRegisteredSections, maxCreditsPerSemester, errors);

            // Kiểm tra trùng môn (đăng ký 2 lớp cùng 1 học phần) 
            ValidateDuplicateCourse(sectionToRegister, alreadyRegisteredSections, errors);

            return errors;
        }

        // Kiểm tra sinh viên đã hoàn thành tất cả học phần tiên quyết chưa
        private void ValidatePrerequisites(
            Section sectionToRegister,
            List<CompletedCourse> completedCourses,
            List<Prerequisite> prerequisites,
            List<string> errors)
        {
            if (sectionToRegister.Course == null) return;

            var coursePrerequisites = prerequisites
                .Where(p => p.CourseId == sectionToRegister.CourseId)
                .ToList();

            foreach (var prereq in coursePrerequisites)
            {
                var completed = completedCourses
                    .Any(cc => cc.CourseId == prereq.PrerequisiteCourseId);

                if (!completed)
                {
                    var prereqCourseName = prereq.PrerequisiteCourse?.CourseName ?? $"mã HP {prereq.PrerequisiteCourseId}";
                    errors.Add($"Chưa hoàn thành học phần tiên quyết: {prereqCourseName}");
                }
            }
        }

        // Kiểm tra lịch của lớp muốn đăng ký có trùng với các lớp đã đăng ký không
        private void ValidateScheduleConflict(
            Section sectionToRegister,
            List<Section> alreadyRegisteredSections,
            List<string> errors)
        {
            if (sectionToRegister.Schedules == null || !sectionToRegister.Schedules.Any())
                return;

            foreach (var registeredSection in alreadyRegisteredSections)
            {
                if (registeredSection.Schedules == null) continue;

                foreach (var newSchedule in sectionToRegister.Schedules)
                {
                    foreach (var existingSchedule in registeredSection.Schedules)
                    {
                        if (newSchedule.ConflictsWith(existingSchedule))
                        {
                            errors.Add(
                                $"Trùng lịch với lớp {registeredSection.SectionCode} " +
                                $"({registeredSection.Course?.CourseName}): " +
                                $"{newSchedule.DayOfWeekName} tiết {newSchedule.StartPeriod}-{newSchedule.EndPeriod}");
                        }
                    }
                }
            }
        }

        // Kiểm tra tổng tín chỉ sau khi đăng ký có vượt giới hạn không
        private void ValidateMaxCredits(
            Section sectionToRegister,
            List<Section> alreadyRegisteredSections,
            int maxCreditsPerSemester,
            List<string> errors)
        {
            if (sectionToRegister.Course == null) return;

            var currentTotalCredits = alreadyRegisteredSections
                .Where(s => s.Course != null)
                .Sum(s => s.Course!.Credits);

            var newTotal = currentTotalCredits + sectionToRegister.Course.Credits;

            if (newTotal > maxCreditsPerSemester)
            {
                errors.Add(
                    $"Vượt số tín chỉ tối đa ({maxCreditsPerSemester} TC). " +
                    $"Hiện tại: {currentTotalCredits} TC, " +
                    $"môn này: {sectionToRegister.Course.Credits} TC, " +
                    $"tổng: {newTotal} TC");
            }
        }

        /// Kiểm tra sinh viên đã đăng ký lớp khác cùng học phần chưa
        private void ValidateDuplicateCourse(
            Section sectionToRegister,
            List<Section> alreadyRegisteredSections,
            List<string> errors)
        {
            var duplicateSection = alreadyRegisteredSections
                .FirstOrDefault(s => s.CourseId == sectionToRegister.CourseId);

            if (duplicateSection != null)
            {
                errors.Add(
                    $"Đã đăng ký lớp {duplicateSection.SectionCode} " +
                    $"của học phần {duplicateSection.Course?.CourseName ?? "này"} rồi");
            }
        }
    }
}
