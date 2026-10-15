namespace CourseRegistration.CoreBusiness.Models
{
    // Phiếu đăng ký (Header) - Enrollment

    public class Enrollment
    {
        public Enrollment()
        {
            EnrollmentLines = new List<EnrollmentLine>();
        }

        public int? EnrollmentId { get; set; }
        public int StudentId { get; set; }
        public DateTime? DateRegistered { get; set; }
        public DateTime? DateCancelled { get; set; }
        public string Semester { get; set; } = string.Empty; //Học kì 
        public string Status { get; set; } = "Pending"; // Pending, Confirmed, Cancelled
        public string? UniqueId { get; set; }

        // Navigation properties
        public Student? Student { get; set; }
        public List<EnrollmentLine> EnrollmentLines { get; set; } = new List<EnrollmentLine>();

        // Tổng số tín chỉ trong phiếu đăng ký
        public int TotalCredits => EnrollmentLines.Where(el => el.Section?.Course != null).Sum(el => el.Section!.Course!.Credits);

        // Thêm lớp học phần vào phiếu đăng ký
        public void AddSection(int sectionId, Section section)
        {
            var existingLine = EnrollmentLines.FirstOrDefault(el => el.SectionId == sectionId);
            if (existingLine == null)
            {
                EnrollmentLines.Add(new EnrollmentLine
                {
                    SectionId = sectionId,
                    EnrollmentId = EnrollmentId,
                    Section = section
                });
            }
        }

        // Xóa lớp học phần khỏi phiếu đăng ký
        public void RemoveSection(int sectionId)
        {
            var line = EnrollmentLines.FirstOrDefault(el => el.SectionId == sectionId);
            if (line != null)
            {
                EnrollmentLines.Remove(line);
            }
        }
    }
}
