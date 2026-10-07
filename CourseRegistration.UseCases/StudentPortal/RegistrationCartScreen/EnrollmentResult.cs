namespace CourseRegistration.UseCases.StudentPortal.RegistrationCartScreen
{
    public class EnrollmentResult
    {
        public bool IsSuccess { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public int? EnrollmentId { get; set; }
    }
}
