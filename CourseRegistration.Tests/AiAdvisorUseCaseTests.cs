using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.CoreBusiness.Services;
using CourseRegistration.CoreBusiness.Services.Interfaces;
using CourseRegistration.StateStore.DI;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using CourseRegistration.UseCases.StudentPortal.AiAdvisor;
using Xunit;

namespace CourseRegistration.Tests
{
    public class AiAdvisorUseCaseTests
    {
        private readonly FakeStudentRepository _studentRepo;
        private readonly FakeSectionRepository _sectionRepo;
        private readonly FakeEnrollmentRepository _enrollmentRepo;
        private readonly FakeCourseRepository _courseRepo;
        private readonly CurrentUserStateStore _userStateStore;
        private readonly RegistrationPeriodService _periodService;

        public AiAdvisorUseCaseTests()
        {
            _studentRepo = new FakeStudentRepository();
            _sectionRepo = new FakeSectionRepository();
            _enrollmentRepo = new FakeEnrollmentRepository();
            _courseRepo = new FakeCourseRepository();
            _userStateStore = new CurrentUserStateStore();
            _periodService = new RegistrationPeriodService();
        }

        private GetAiAdviceUseCase CreateUseCase()
        {
            return new GetAiAdviceUseCase(
                _studentRepo,
                _sectionRepo,
                _enrollmentRepo,
                _courseRepo,
                chatClient: null,
                currentUserStateStore: _userStateStore,
                registrationPeriodService: _periodService);
        }

        [Fact]
        public async Task Test_CrossQuery_NguyenVanTuan_HaNgocLong_ReturnsOnlyMatchingSection()
        {
            // Scenario from prompt: "Sinh viên Nguyễn Văn Tuấn có đăng ký môn nào của thầy Hà Ngọc Long không?"
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 1,
                Username = "admin",
                FullName = "Admin",
                Role = "Admin"
            });

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Nguyễn Văn Tuấn có đăng ký môn nào của thầy Hà Ngọc Long không?");

            Assert.NotNull(result);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("Hà Ngọc Long", result.AiMessage);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.Contains("Lập trình Ứng dụng Web", result.AiMessage);
            // Must NOT contain sections not enrolled by Tuan (e.g. WEB302_01 in HK2)
            Assert.DoesNotContain("WEB302_01", result.AiMessage);
        }

        [Fact]
        public async Task Test_CrossQuery_TranThiMai_HaNgocLong_NegativeResult()
        {
            // Scenario: Tran Thi Mai (SV21002) is NOT enrolled in Ha Ngoc Long's classes
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 1,
                Username = "admin",
                FullName = "Admin",
                Role = "Admin"
            });

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Trần Thị Mai có đăng ký môn nào của thầy Hà Ngọc Long không?");

            Assert.NotNull(result);
            Assert.Contains("Trần Thị Mai", result.AiMessage);
            Assert.Contains("KHÔNG ĐĂNG KÝ", result.AiMessage);
        }

        [Fact]
        public async Task Test_LoggedIn_Student_PronounQuery()
        {
            // Logged in as SV21001 (Nguyen Van Tuan), asks "Tôi có đăng ký môn nào của thầy Hà Ngọc Long không?"
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Tôi có đăng ký môn nào của thầy Hà Ngọc Long không?");

            Assert.NotNull(result);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.Contains("Hà Ngọc Long", result.AiMessage);
        }

        [Fact]
        public async Task Test_English_RegisteredCourses()
        {
            // User asks in English: "What courses have I registered for?"
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "What courses have I registered for?");

            Assert.NotNull(result);
            Assert.Contains("Your Registered Courses", result.AiMessage);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.Contains("CSDL201_01", result.AiMessage);
            Assert.Contains("Credits", result.AiMessage);
        }

        [Fact]
        public async Task Test_English_TomorrowSchedule()
        {
            // User asks in English: "Do I have any classes tomorrow?"
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Do I have any classes tomorrow?");

            Assert.NotNull(result);
            Assert.Contains("Schedule", result.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
        }

        [Fact]
        public async Task Test_Student_Privacy_Protection_When_Querying_Other_Student()
        {
            // SV21001 tries to query Tran Thi Mai's enrollment
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Sinh viên Trần Thị Mai đã đăng ký môn nào?");

            Assert.NotNull(result);
            Assert.Contains("Bảo Mật", result.AiMessage);
            // Must NOT reveal Tran Thi Mai's courses
            Assert.DoesNotContain("CSDL201_01", result.AiMessage);
        }

        [Fact]
        public async Task Test_MultiTurn_Conversation_Context()
        {
            // Turn 1: "Tôi đã đăng ký môn nào?"
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var history = new List<AiConversationMessage>
            {
                new AiConversationMessage { IsUser = true, Content = "Tôi đã đăng ký môn nào?" },
                new AiConversationMessage { IsUser = false, Content = "Bạn đã đăng ký 4 môn: WEB301_01 (Lập trình Ứng dụng Web), CSDL201_01, ATTT401_01, TOEIC_01." }
            };

            // Turn 2: Follow up "Ai dạy môn đó?" when focusing on WEB301
            history.Add(new AiConversationMessage { IsUser = true, Content = "Lớp WEB301_01 học ở đâu và ai dạy?" });
            history.Add(new AiConversationMessage { IsUser = false, Content = "Lớp WEB301_01 học tại Lab 402, do TS. Hà Ngọc Long giảng dạy." });

            // Turn 3: "Ai dạy môn đó?"
            var turn3 = await useCase.ExecuteAsync(1, "2026_HK1", "Ai dạy môn đó?", history);

            Assert.NotNull(turn3);
            Assert.Contains("Hà Ngọc Long", turn3.AiMessage);
        }

        [Fact]
        public async Task Test_Lecturer_Lookup_WholeSchool()
        {
            // User asks: "Thầy Hà Ngọc Long dạy những môn nào?"
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Thầy Hà Ngọc Long dạy những môn nào?");

            Assert.NotNull(result);
            Assert.Contains("Hà Ngọc Long", result.AiMessage);
            Assert.Contains("Lập trình Ứng dụng Web", result.AiMessage);
        }

        [Fact]
        public async Task Test_Tuition_And_VietQR_Query()
        {
            // User asks about tuition
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Học phí một tín chỉ bao nhiêu tiền?");

            Assert.NotNull(result);
            Assert.Contains("450.000", result.AiMessage);
            Assert.Contains("101705201967", result.AiMessage);
            Assert.Contains("MB Bank", result.AiMessage);
        }

        [Fact]
        public async Task Test_Registration_Period_Status()
        {
            // User asks if registration is open
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Thời gian mở cổng đăng ký học phần khi nào?");

            Assert.NotNull(result);
            Assert.Contains("CỔNG ĐĂNG KÝ ĐANG MỞ", result.AiMessage);
        }

        [Fact]
        public async Task Test_Course_Recommendation_Requires_Confirmation()
        {
            // User asks for recommendations
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Gợi ý môn học kỳ này cho tôi");

            Assert.NotNull(result);
            Assert.NotEmpty(result.RecommendedSections);
            Assert.All(result.RecommendedSections, s => Assert.True(s.IsSelected));
            Assert.Contains("Xác nhận", result.AiMessage);
        }
    }

    #region Fake Repositories for Testing

    public class FakeStudentRepository : IStudentRepository
    {
        private readonly List<Student> _students = new List<Student>
        {
            new Student { StudentId = 1, StudentCode = "SV21001", FullName = "Nguyễn Văn Tuấn", Major = "Công nghệ Thông tin", AcademicYear = "K21", MaxCreditsPerSemester = 24 },
            new Student { StudentId = 2, StudentCode = "SV21002", FullName = "Trần Thị Mai", Major = "Hệ thống Thông tin", AcademicYear = "K21", MaxCreditsPerSemester = 20 },
            new Student { StudentId = 3, StudentCode = "SV21003", FullName = "Lê Hoàng Nam", Major = "Kỹ thuật Phần mềm", AcademicYear = "K21", MaxCreditsPerSemester = 24 }
        };

        private readonly List<CompletedCourse> _completed = new List<CompletedCourse>
        {
            new CompletedCourse { CompletedCourseId = 1, StudentId = 1, CourseId = 1, Grade = 8.0, Semester = "2024_HK1" },
            new CompletedCourse { CompletedCourseId = 2, StudentId = 1, CourseId = 2, Grade = 7.5, Semester = "2024_HK2" },
            new CompletedCourse { CompletedCourseId = 3, StudentId = 2, CourseId = 1, Grade = 6.5, Semester = "2025_HK1" }
        };

        public Task<IEnumerable<Student>> GetAllStudentsAsync() => Task.FromResult<IEnumerable<Student>>(_students);
        public Task<Student?> GetStudentByIdAsync(int studentId) => Task.FromResult(_students.FirstOrDefault(s => s.StudentId == studentId));
        public Task<Student?> GetStudentByCodeAsync(string studentCode) => Task.FromResult(_students.FirstOrDefault(s => s.StudentCode.Equals(studentCode, StringComparison.OrdinalIgnoreCase)));
        public Task AddStudentAsync(Student student) { _students.Add(student); return Task.CompletedTask; }
        public Task UpdateStudentAsync(Student student) => Task.CompletedTask;
        public Task<IEnumerable<CompletedCourse>> GetCompletedCoursesByStudentIdAsync(int studentId) =>
            Task.FromResult<IEnumerable<CompletedCourse>>(_completed.Where(c => c.StudentId == studentId));
    }

    public class FakeSectionRepository : ISectionRepository
    {
        private readonly List<Section> _sections;

        public FakeSectionRepository()
        {
            var webCourse = new Course { CourseId = 3, CourseCode = "WEB301", CourseName = "Lập trình Ứng dụng Web", Credits = 4 };
            var csdlCourse = new Course { CourseId = 5, CourseCode = "CSDL201", CourseName = "Cơ sở Dữ liệu", Credits = 3 };
            var atttCourse = new Course { CourseId = 7, CourseCode = "ATTT401", CourseName = "An toàn Thông tin", Credits = 3 };
            var toeicCourse = new Course { CourseId = 8, CourseCode = "TOEIC", CourseName = "Tiếng Anh chuyên ngành CNTT", Credits = 2 };
            var tin102Course = new Course { CourseId = 2, CourseCode = "TIN102", CourseName = "Lập trình Hướng đối tượng", Credits = 3 };
            var web302Course = new Course { CourseId = 9, CourseCode = "WEB302", CourseName = "Phát triển Web Nâng cao", Credits = 4 };

            _sections = new List<Section>
            {
                new Section
                {
                    SectionId = 1, SectionCode = "WEB301_01", CourseId = 3, Course = webCourse,
                    Instructor = "TS. Hà Ngọc Long", Room = "Lab 402", MaxCapacity = 50, CurrentEnrollment = 10, Semester = "2026_HK1",
                    Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 1, SectionId = 1, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 4, Room = "Lab 402" } }
                },
                new Section
                {
                    SectionId = 2, SectionCode = "CSDL201_01", CourseId = 5, Course = csdlCourse,
                    Instructor = "TS. Phạm Thanh Tùng", Room = "Phòng B204", MaxCapacity = 60, CurrentEnrollment = 15, Semester = "2026_HK1",
                    Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 2, SectionId = 2, DayOfWeek = 3, StartPeriod = 7, EndPeriod = 9, Room = "Phòng B204" } }
                },
                new Section
                {
                    SectionId = 3, SectionCode = "ATTT401_01", CourseId = 7, Course = atttCourse,
                    Instructor = "PGS.TS. Ngô Văn Dũng", Room = "Lab 501", MaxCapacity = 40, CurrentEnrollment = 8, Semester = "2026_HK1",
                    Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 3, SectionId = 3, DayOfWeek = 6, StartPeriod = 7, EndPeriod = 9, Room = "Lab 501" } }
                },
                new Section
                {
                    SectionId = 4, SectionCode = "TOEIC_01", CourseId = 8, Course = toeicCourse,
                    Instructor = "ThS. Lý Thị Mỹ Hạnh", Room = "Hội trường A", MaxCapacity = 80, CurrentEnrollment = 20, Semester = "2026_HK1",
                    Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 4, SectionId = 4, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Hội trường A" } }
                },
                new Section
                {
                    SectionId = 5, SectionCode = "TIN102_01", CourseId = 2, Course = tin102Course,
                    Instructor = "ThS. Vũ Mạnh Cường", Room = "Phòng A205", MaxCapacity = 60, CurrentEnrollment = 12, Semester = "2026_HK1",
                    Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 5, SectionId = 5, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A205" } }
                },
                new Section
                {
                    SectionId = 6, SectionCode = "WEB302_01", CourseId = 9, Course = web302Course,
                    Instructor = "TS. Hà Ngọc Long", Room = "Lab 402", MaxCapacity = 50, CurrentEnrollment = 0, Semester = "2026_HK2",
                    Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 6, SectionId = 6, DayOfWeek = 3, StartPeriod = 1, EndPeriod = 4, Room = "Lab 402" } }
                }
            };
        }

        public Task<IEnumerable<Section>> GetAllSectionsAsync() => Task.FromResult<IEnumerable<Section>>(_sections);
        public Task<IEnumerable<Section>> GetSectionsBySemesterAsync(string semester) =>
            Task.FromResult<IEnumerable<Section>>(_sections.Where(s => s.Semester == semester));
        public Task<IEnumerable<Section>> SearchSectionsAsync(string keyword, string semester) =>
            Task.FromResult<IEnumerable<Section>>(_sections.Where(s => s.Semester == semester && s.SectionCode.Contains(keyword)));
        public Task<Section?> GetSectionByIdAsync(int sectionId) => Task.FromResult(_sections.FirstOrDefault(s => s.SectionId == sectionId));
        public Task AddSectionAsync(Section section) { _sections.Add(section); return Task.CompletedTask; }
        public Task UpdateSectionAsync(Section section) => Task.CompletedTask;
        public Task DeleteSectionAsync(int sectionId) => Task.CompletedTask;
        public Task AddScheduleAsync(SectionSchedule schedule) => Task.CompletedTask;
        public Task DeleteScheduleAsync(int scheduleId) => Task.CompletedTask;
        public Task<IEnumerable<SectionSchedule>> GetSchedulesBySectionIdAsync(int sectionId) =>
            Task.FromResult<IEnumerable<SectionSchedule>>(_sections.FirstOrDefault(s => s.SectionId == sectionId)?.Schedules ?? new List<SectionSchedule>());
        public Task<bool> IncrementEnrollmentCountAsync(int sectionId) => Task.FromResult(true);
        public Task DecrementEnrollmentCountAsync(int sectionId) => Task.CompletedTask;
    }

    public class FakeEnrollmentRepository : IEnrollmentRepository
    {
        public Task<Enrollment?> GetEnrollmentByIdAsync(int enrollmentId) => Task.FromResult<Enrollment?>(null);
        public Task<IEnumerable<Enrollment>> GetEnrollmentsByStudentIdAsync(int studentId) => Task.FromResult<IEnumerable<Enrollment>>(new List<Enrollment>());
        public Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester) => Task.FromResult<IEnumerable<Enrollment>>(new List<Enrollment>());

        public Task<IEnumerable<Section>> GetRegisteredSectionsByStudentAsync(int studentId, string semester)
        {
            var webCourse = new Course { CourseId = 3, CourseCode = "WEB301", CourseName = "Lập trình Ứng dụng Web", Credits = 4 };
            var csdlCourse = new Course { CourseId = 5, CourseCode = "CSDL201", CourseName = "Cơ sở Dữ liệu", Credits = 3 };
            var atttCourse = new Course { CourseId = 7, CourseCode = "ATTT401", CourseName = "An toàn Thông tin", Credits = 3 };
            var toeicCourse = new Course { CourseId = 8, CourseCode = "TOEIC", CourseName = "Tiếng Anh chuyên ngành CNTT", Credits = 2 };
            var tin102Course = new Course { CourseId = 2, CourseCode = "TIN102", CourseName = "Lập trình Hướng đối tượng", Credits = 3 };

            if (studentId == 1) // SV21001 (Nguyen Van Tuan)
            {
                var list = new List<Section>
                {
                    new Section
                    {
                        SectionId = 1, SectionCode = "WEB301_01", CourseId = 3, Course = webCourse,
                        Instructor = "TS. Hà Ngọc Long", Room = "Lab 402", MaxCapacity = 50, CurrentEnrollment = 10, Semester = "2026_HK1",
                        Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 1, SectionId = 1, DayOfWeek = 2, StartPeriod = 1, EndPeriod = 4, Room = "Lab 402" } }
                    },
                    new Section
                    {
                        SectionId = 2, SectionCode = "CSDL201_01", CourseId = 5, Course = csdlCourse,
                        Instructor = "TS. Phạm Thanh Tùng", Room = "Phòng B204", MaxCapacity = 60, CurrentEnrollment = 15, Semester = "2026_HK1",
                        Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 2, SectionId = 2, DayOfWeek = 3, StartPeriod = 7, EndPeriod = 9, Room = "Phòng B204" } }
                    },
                    new Section
                    {
                        SectionId = 3, SectionCode = "ATTT401_01", CourseId = 7, Course = atttCourse,
                        Instructor = "PGS.TS. Ngô Văn Dũng", Room = "Lab 501", MaxCapacity = 40, CurrentEnrollment = 8, Semester = "2026_HK1",
                        Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 3, SectionId = 3, DayOfWeek = 6, StartPeriod = 7, EndPeriod = 9, Room = "Lab 501" } }
                    },
                    new Section
                    {
                        SectionId = 4, SectionCode = "TOEIC_01", CourseId = 8, Course = toeicCourse,
                        Instructor = "ThS. Lý Thị Mỹ Hạnh", Room = "Hội trường A", MaxCapacity = 80, CurrentEnrollment = 20, Semester = "2026_HK1",
                        Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 4, SectionId = 4, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Hội trường A" } }
                    }
                };
                return Task.FromResult<IEnumerable<Section>>(list);
            }
            else if (studentId == 2) // SV21002 (Tran Thi Mai)
            {
                var list = new List<Section>
                {
                    new Section
                    {
                        SectionId = 2, SectionCode = "CSDL201_01", CourseId = 5, Course = csdlCourse,
                        Instructor = "TS. Phạm Thanh Tùng", Room = "Phòng B204", MaxCapacity = 60, CurrentEnrollment = 15, Semester = "2026_HK1",
                        Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 2, SectionId = 2, DayOfWeek = 3, StartPeriod = 7, EndPeriod = 9, Room = "Phòng B204" } }
                    },
                    new Section
                    {
                        SectionId = 5, SectionCode = "TIN102_01", CourseId = 2, Course = tin102Course,
                        Instructor = "ThS. Vũ Mạnh Cường", Room = "Phòng A205", MaxCapacity = 60, CurrentEnrollment = 12, Semester = "2026_HK1",
                        Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 5, SectionId = 5, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Phòng A205" } }
                    },
                    new Section
                    {
                        SectionId = 4, SectionCode = "TOEIC_01", CourseId = 8, Course = toeicCourse,
                        Instructor = "ThS. Lý Thị Mỹ Hạnh", Room = "Hội trường A", MaxCapacity = 80, CurrentEnrollment = 20, Semester = "2026_HK1",
                        Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 4, SectionId = 4, DayOfWeek = 7, StartPeriod = 1, EndPeriod = 3, Room = "Hội trường A" } }
                    }
                };
                return Task.FromResult<IEnumerable<Section>>(list);
            }

            return Task.FromResult<IEnumerable<Section>>(new List<Section>());
        }

        public Task<int> CreateEnrollmentAsync(Enrollment enrollment) => Task.FromResult(1);
        public Task CancelEnrollmentAsync(int enrollmentId) => Task.CompletedTask;
        public Task<IEnumerable<Enrollment>> GetEnrollmentsBySectionIdAsync(int sectionId) => Task.FromResult<IEnumerable<Enrollment>>(new List<Enrollment>());
        public Task<IEnumerable<EnrollmentLine>> GetActiveEnrollmentLinesByStudentAsync(int studentId, string semester) => Task.FromResult<IEnumerable<EnrollmentLine>>(new List<EnrollmentLine>());
        public Task<bool> DropSectionForStudentAsync(int studentId, int sectionId, string? reason = null) => Task.FromResult(true);
        public Task<(bool IsSuccess, string? ErrorMessage)> AddSectionForStudentAsync(int studentId, int sectionId, string semester) => Task.FromResult((true, (string?)null));
        public Task<bool> UpdatePaymentStatusAsync(int enrollmentId, string paymentStatus, string paymentMethod, string transactionReference) => Task.FromResult(true);
    }

    public class FakeCourseRepository : ICourseRepository
    {
        private readonly List<Course> _courses = new List<Course>
        {
            new Course { CourseId = 1, CourseCode = "TIN101", CourseName = "Nhập môn Lập trình", Credits = 3 },
            new Course { CourseId = 2, CourseCode = "TIN102", CourseName = "Lập trình Hướng đối tượng", Credits = 3 },
            new Course { CourseId = 3, CourseCode = "WEB301", CourseName = "Lập trình Ứng dụng Web", Credits = 4 },
            new Course { CourseId = 4, CourseCode = "TIN201", CourseName = "Cấu trúc Dữ liệu & Giải thuật", Credits = 3 },
            new Course { CourseId = 5, CourseCode = "CSDL201", CourseName = "Cơ sở Dữ liệu", Credits = 3 },
            new Course { CourseId = 6, CourseCode = "KTPM302", CourseName = "Kỹ thuật Phần mềm", Credits = 3 },
            new Course { CourseId = 7, CourseCode = "ATTT401", CourseName = "An toàn Thông tin", Credits = 3 },
            new Course { CourseId = 8, CourseCode = "TOEIC", CourseName = "Tiếng Anh chuyên ngành CNTT", Credits = 2 },
            new Course { CourseId = 9, CourseCode = "WEB302", CourseName = "Phát triển Web Nâng cao", Credits = 4 }
        };

        private readonly List<Prerequisite> _prereqs = new List<Prerequisite>
        {
            new Prerequisite { PrerequisiteId = 1, CourseId = 3, PrerequisiteCourseId = 2 }, // WEB301 -> TIN102
            new Prerequisite { PrerequisiteId = 2, CourseId = 6, PrerequisiteCourseId = 2 }, // KTPM302 -> TIN102
            new Prerequisite { PrerequisiteId = 3, CourseId = 4, PrerequisiteCourseId = 1 }  // TIN201 -> TIN101
        };

        public Task<IEnumerable<Course>> GetAllCoursesAsync() => Task.FromResult<IEnumerable<Course>>(_courses);
        public Task<Course?> GetCourseByIdAsync(int courseId) => Task.FromResult(_courses.FirstOrDefault(c => c.CourseId == courseId));
        public Task<IEnumerable<Course>> SearchCoursesAsync(string keyword) => Task.FromResult<IEnumerable<Course>>(_courses.Where(c => c.CourseName.Contains(keyword)));
        public Task AddCourseAsync(Course course) => Task.CompletedTask;
        public Task UpdateCourseAsync(Course course) => Task.CompletedTask;
        public Task DeleteCourseAsync(int courseId) => Task.CompletedTask;
        public Task<IEnumerable<Prerequisite>> GetPrerequisitesByCourseIdAsync(int courseId) => Task.FromResult<IEnumerable<Prerequisite>>(_prereqs.Where(p => p.CourseId == courseId));
        public Task<IEnumerable<Prerequisite>> GetAllPrerequisitesAsync() => Task.FromResult<IEnumerable<Prerequisite>>(_prereqs);
        public Task AddPrerequisiteAsync(Prerequisite prerequisite) => Task.CompletedTask;
        public Task DeletePrerequisiteAsync(int prerequisiteId) => Task.CompletedTask;
    }

    #endregion
}
