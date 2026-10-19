using CourseRegistration.CoreBusiness.Models;

namespace CourseRegistration.Web.Services
{
    public interface IReportExportService
    {
        byte[] GenerateSectionStudentsExcel(Section section, List<Enrollment> enrollments);
        byte[] GenerateSectionStudentsPdf(Section section, List<Enrollment> enrollments);
        byte[] GenerateStudentEnrollmentPdf(Student student, Enrollment enrollment, List<Section> sections);
        byte[] GenerateStudentEnrollmentExcel(Student student, Enrollment enrollment, List<Section> sections);
    }
}
