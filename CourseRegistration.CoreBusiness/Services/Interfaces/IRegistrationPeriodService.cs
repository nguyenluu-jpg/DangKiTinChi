using System.Collections.Generic;
using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.CoreBusiness.Services.Interfaces
{
    public interface IRegistrationPeriodService
    {
        string CurrentSemesterCode { get; set; }
        int? SimulatedPhaseOverride { get; set; } // Hỗ trợ chuyển đổi linh hoạt giai đoạn để demo cho Giảng viên

        List<SemesterInfo> GetAllSemesters();
        SemesterInfo GetCurrentSemester();
        SemesterInfo? GetSemester(string semesterCode);
        RegistrationPhaseInfo? GetActivePhase(string semesterCode);
        bool IsRegistrationOpen(string semesterCode);
        void SetSimulatedPhase(int? phaseNumber);
    }
}
