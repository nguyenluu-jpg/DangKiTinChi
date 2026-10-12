namespace CourseRegistration.CoreBusiness.Models
{
    //  Dòng đăng ký (Line) - EnrollmentLine

    public class EnrollmentLine
    {
        public int EnrollmentLineId { get; set; }
        public int SectionId { get; set; }
        public int? EnrollmentId { get; set; }

        // Navigation property
        public Section? Section { get; set; }
    }
}
