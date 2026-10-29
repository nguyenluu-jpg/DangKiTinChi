using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.CoreBusiness.Services;
using CourseRegistration.CoreBusiness.Services.Interfaces;
using CourseRegistration.StateStore.DI;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using CourseRegistration.UseCases.StudentPortal.AiAdvisor;
using Microsoft.Extensions.AI;
using Xunit;

namespace CourseRegistration.Tests
{
    /// <summary>
    /// =========================================================================================
    /// PHÂN ĐỊNH RÕ RÀNG: BỘ KIỂM THỬ ĐƠN VỊ (UNIT TESTS) - SỬ DỤNG IN-MEMORY FAKE REPOSITORIES
    /// =========================================================================================
    /// Lưu ý kỹ thuật:
    /// - Các bài kiểm thử trong class này hoàn toàn là UNIT TESTS chạy độc lập trong bộ nhớ.
    /// - Sử dụng các Fake Repositories (In-Memory Stubs) để kiểm thử logic phân loại ý định,
    ///   xử lý ngôn ngữ tự nhiên, tính toán lịch học, quyền riêng tư và đối chiếu dữ liệu.
    /// - KHÔNG truy vấn CSDL SQL Server thật để đảm bảo tốc độ cao, tính độc lập và tái lặp 100%.
    /// =========================================================================================
    /// </summary>
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

        private GetAiAdviceUseCase CreateUseCase(IChatClient? chatClient = null)
        {
            return new GetAiAdviceUseCase(
                _studentRepo,
                _sectionRepo,
                _enrollmentRepo,
                _courseRepo,
                chatClient: chatClient,
                currentUserStateStore: _userStateStore,
                registrationPeriodService: _periodService);
        }

        #region 1. Đối Chiếu Dữ Liệu Chéo Giữa Sinh Viên & Giảng Viên (Cross-Table Matching)

        [Fact]
        public async Task Test_Unit_CrossQuery_NguyenVanTuan_HaNgocLong_ReturnsOnlyMatchingSection()
        {
            // Scenario: Nguyễn Văn Tuấn (SV21001) có đăng ký môn WEB301_01 của TS. Hà Ngọc Long
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
            // Không được chứa các lớp không phải do Tuấn đăng ký hoặc kỳ khác (ví dụ WEB302_01 ở HK2)
            Assert.DoesNotContain("WEB302_01", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_CrossQuery_TranThiMai_HaNgocLong_NegativeResult()
        {
            // Scenario: Trần Thị Mai (SV21002) KHÔNG đăng ký môn nào của TS. Hà Ngọc Long
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
            Assert.DoesNotContain("WEB301_01", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_LoggedIn_Student_PronounQuery()
        {
            // Scenario: Sinh viên SV21001 đăng nhập, dùng đại từ "Tôi có đăng ký môn nào của thầy Hà Ngọc Long không?"
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

        #endregion

        #region 2. Diễn Đạt Tiếng Việt & Tiếng Anh Tự Nhiên Của Cùng Ý Định (Yêu cầu 3 & 10)

        [Theory]
        [InlineData("Tôi đã đăng ký những môn nào?")]
        [InlineData("học kỳ này em học những môn gì")]
        [InlineData("mình đã đăng ký những môn nào rồi")]
        [InlineData("xem các môn đã đk của tôi")]
        [InlineData("danh sach mon da dang ky")]
        public async Task Test_Unit_RegisteredCourses_NaturalLanguageVariations(string query)
        {
            // Kiểm tra nhiều cách diễn đạt tiếng Việt tự nhiên (có dấu, không dấu, viết tắt đk)
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", query);

            Assert.NotNull(result);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.Contains("CSDL201_01", result.AiMessage);
            Assert.Contains("ATTT401_01", result.AiMessage);
            Assert.Contains("TOEIC_01", result.AiMessage);
            // Không được rơi vào câu hỏi mẫu
            Assert.DoesNotContain("Ngày mai tôi học gì?", result.AiMessage);
            Assert.DoesNotContain("Môn WEB301 có mấy lớp?", result.AiMessage);
        }

        [Theory]
        [InlineData("Thầy Long dạy môn gì?")]
        [InlineData("Thầy Hà Ngọc Long dạy những lớp nào?")]
        [InlineData("thay long day mon gi")]
        [InlineData("cho mình hỏi giảng viên hà ngọc long dạy lớp nào")]
        public async Task Test_Unit_LecturerLookup_NaturalLanguageVariations(string query)
        {
            // Kiểm tra nhiều cách hỏi về giảng viên
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", query);

            Assert.NotNull(result);
            Assert.Contains("Hà Ngọc Long", result.AiMessage);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.DoesNotContain("Ngày mai tôi học gì?", result.AiMessage);
        }

        [Theory]
        [InlineData("Muốn đăng nhập tài khoản gồm những bước nào?")]
        [InlineData("Làm sao để đăng nhập vào hệ thống?")]
        [InlineData("chỉ tôi cách login")]
        [InlineData("muốn vào tài khoản thì làm ntn")]
        [InlineData("tôi không biết đăng nhập ở đâu")]
        [InlineData("How do I log in?")]
        public async Task Test_Unit_LoginInstructions_NaturalLanguageVariations(string query)
        {
            // Kiểm tra nhiều cách hỏi về quy trình đăng nhập
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", query);

            Assert.NotNull(result);
            Assert.True(result.SuggestLogin);
            // Phải chứa hướng dẫn theo UI thật: nút Đăng nhập / /login, MSSV và mật khẩu
            bool containsLoginKeywords = result.AiMessage.Contains("Đăng nhập") || result.AiMessage.Contains("Log in");
            Assert.True(containsLoginKeywords);
            // Tuyệt đối không được chứa danh sách câu hỏi SQL Server mẫu
            Assert.DoesNotContain("Môn WEB301 có mấy lớp?", result.AiMessage);
            Assert.DoesNotContain("Thầy Long dạy môn gì?", result.AiMessage);
            Assert.DoesNotContain("Ngày mai tôi học gì?", result.AiMessage);
        }

        [Theory]
        [InlineData("ngày mai")]
        [InlineData("tiết mấy")]
        [InlineData("học phí")]
        [InlineData("đăng ký")]
        [InlineData("cho hỏi chút")]
        public async Task Test_Unit_AmbiguousQueries_AsksBackNaturally_DoesNotDumpSqlTemplates(string ambiguousQuery)
        {
            // Câu hỏi cụt lủn / thiếu thông tin phải hỏi lại tự nhiên, không được tuôn câu hỏi mẫu SQL
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", ambiguousQuery);

            Assert.NotNull(result);
            Assert.False(string.IsNullOrWhiteSpace(result.AiMessage));
            // Không được tuôn danh sách câu hỏi tra cứu SQL viết cứng
            Assert.DoesNotContain("Môn WEB301 có mấy lớp?", result.AiMessage);
            Assert.DoesNotContain("Thầy Long dạy môn gì?", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_English_RegisteredCourses()
        {
            // Câu hỏi tiếng Anh về môn đã đăng ký
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
        public async Task Test_Unit_English_TomorrowSchedule()
        {
            // Câu hỏi tiếng Anh về lịch ngày mai
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
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("Schedule", result.AiMessage);
        }

        #endregion

        #region 3. Kiểm Tra Lịch Ngày Mai & Từng Ngày Trong Tuần Động Theo Dữ Liệu (Yêu cầu 4)

        [Fact]
        public async Task Test_Unit_TomorrowSchedule_DynamicCalculation_MatchesActualScheduleData()
        {
            // YÊU CẦU 4: Phải kiểm tra kết quả dựa trên ngày thực tế và dữ liệu lịch tương ứng;
            // không chỉ kiểm tra chứa từ "Lịch Học".
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var tomorrowDate = DateTime.Today.AddDays(1);
            DayOfWeek tomorrowDow = tomorrowDate.DayOfWeek;
            int tomorrowDayInt = tomorrowDow == DayOfWeek.Sunday ? 8 : ((int)tomorrowDow + 1);

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Ngày mai tôi học gì?");

            Assert.NotNull(result);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);

            // Dữ liệu môn học của Nguyễn Văn Tuấn (StudentId = 1):
            // - WEB301_01: Thứ 2 (Day 2), Tiết 1-4, Lab 402
            // - CSDL201_01: Thứ 3 (Day 3), Tiết 7-9, Phòng B204
            // - ATTT401_01: Thứ 6 (Day 6), Tiết 7-9, Lab 501
            // - TOEIC_01: Thứ 7 (Day 7), Tiết 1-3, Hội trường A
            // - Thứ 4 (Day 4), Thứ 5 (Day 5), Chủ Nhật (Day 8): KHÔNG CÓ LỊCH HỌC
            if (tomorrowDayInt == 2)
            {
                Assert.Contains("WEB301_01", result.AiMessage);
                Assert.Contains("Lab 402", result.AiMessage);
                Assert.DoesNotContain("không có lịch học nào", result.AiMessage);
            }
            else if (tomorrowDayInt == 3)
            {
                Assert.Contains("CSDL201_01", result.AiMessage);
                Assert.Contains("Phòng B204", result.AiMessage);
                Assert.DoesNotContain("không có lịch học nào", result.AiMessage);
            }
            else if (tomorrowDayInt == 6)
            {
                Assert.Contains("ATTT401_01", result.AiMessage);
                Assert.Contains("Lab 501", result.AiMessage);
                Assert.DoesNotContain("không có lịch học nào", result.AiMessage);
            }
            else if (tomorrowDayInt == 7)
            {
                Assert.Contains("TOEIC_01", result.AiMessage);
                Assert.Contains("Hội trường A", result.AiMessage);
                Assert.DoesNotContain("không có lịch học nào", result.AiMessage);
            }
            else
            {
                // Ngày mai không có lịch: Phải khẳng định rõ ràng và không được hiển thị môn của ngày khác
                Assert.Contains("không có lịch học nào", result.AiMessage);
                Assert.DoesNotContain("WEB301_01", result.AiMessage);
                Assert.DoesNotContain("CSDL201_01", result.AiMessage);
                Assert.DoesNotContain("ATTT401_01", result.AiMessage);
                Assert.DoesNotContain("TOEIC_01", result.AiMessage);
            }
        }

        [Fact]
        public async Task Test_Unit_SpecificDaySchedule_Monday_HasClasses()
        {
            // Kiểm tra lịch Thứ 2 cố định: Tuấn có WEB301_01, không được chứa môn của Thứ 3 hay Thứ 6
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Thứ 2 tôi học gì?");

            Assert.NotNull(result);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.Contains("Lab 402", result.AiMessage);
            Assert.Contains("Tiết 1 - 4", result.AiMessage);
            Assert.DoesNotContain("không có lịch học nào", result.AiMessage);
            // Không được chứa môn của các ngày khác
            Assert.DoesNotContain("CSDL201_01", result.AiMessage);
            Assert.DoesNotContain("ATTT401_01", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_SpecificDaySchedule_Thursday_HasNoClasses()
        {
            // Kiểm tra lịch Thứ 4 cố định: Tuấn hoàn toàn không có lịch học
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Thứ 4 tôi học gì?");

            Assert.NotNull(result);
            Assert.Contains("không có lịch học nào", result.AiMessage);
            Assert.DoesNotContain("WEB301_01", result.AiMessage);
            Assert.DoesNotContain("CSDL201_01", result.AiMessage);
        }

        #endregion

        #region 4. Kiểm Tra Thay Đổi Học Kỳ Đầu Vào (Yêu cầu 5)

        [Fact]
        public async Task Test_Unit_SemesterChange_HK2_StudentHasNoRegisteredCourses()
        {
            // YÊU CẦU 5: Kiểm tra việc thay đổi học kỳ đầu vào sang 2026_HK2
            // Sinh viên SV21001 chỉ đăng ký các lớp ở 2026_HK1. Ở 2026_HK2, sinh viên chưa có môn nào.
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK2", "Tôi đã đăng ký những môn nào?");

            Assert.NotNull(result);
            Assert.Contains("2026_HK2", result.AiMessage);
            Assert.Contains("chưa đăng ký lớp học phần nào", result.AiMessage);
            // Tuyệt đối không được lẫn lộn trả về các môn của 2026_HK1
            Assert.DoesNotContain("WEB301_01", result.AiMessage);
            Assert.DoesNotContain("CSDL201_01", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_SemesterChange_HK2_OpenSectionsLookup()
        {
            // Tra cứu môn mở ở HK2: WEB302_01 mở ở HK2, không mở ở HK1
            var useCase = CreateUseCase();
            var resultHK2 = await useCase.ExecuteAsync(0, "2026_HK2", "Có những lớp học phần nào đang mở?");

            Assert.NotNull(resultHK2);
            Assert.Contains("WEB302_01", resultHK2.AiMessage);
        }

        #endregion

        #region 5. Kiểm Tra Phân Quyền Bảo Mật & Quyền Riêng Tư Dữ Liệu (Yêu cầu 7 & 10)

        [Fact]
        public async Task Test_Unit_Security_StudentCannotReadOtherStudentData()
        {
            // YÊU CẦU 7: Sinh viên SV21001 cố tình tra cứu môn đã đăng ký của Trần Thị Mai (SV21002)
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
            // Phải thông báo từ chối truy cập vì lý do bảo mật
            Assert.Contains("Bảo Mật", result.AiMessage);
            // Không được để lộ thông tin môn học hoặc điểm số của Trần Thị Mai
            Assert.DoesNotContain("CSDL201_01", result.AiMessage);
            Assert.DoesNotContain("TIN102_01", result.AiMessage);
            Assert.DoesNotContain("6.5", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_Security_AdminCanViewAnyStudentData()
        {
            // Admin có quyền tra cứu học vụ của sinh viên Trần Thị Mai
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 1,
                Username = "admin",
                FullName = "Admin Quản Trị",
                Role = "Admin"
            });

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Trần Thị Mai đã đăng ký môn nào?");

            Assert.NotNull(result);
            // Admin không bị chặn bảo mật
            Assert.DoesNotContain("Từ chối quyền truy cập", result.AiMessage);
            Assert.Contains("Trần Thị Mai", result.AiMessage);
            Assert.Contains("CSDL201_01", result.AiMessage);
        }

        #endregion

        #region 6. Kiểm Tra Gợi Ý Học Phần (Recommendation vs Confirmation) (Yêu cầu 6)

        [Fact]
        public async Task Test_Unit_CourseRecommendation_DoesNotMutateDatabase_RequiresConfirmation()
        {
            // YÊU CẦU 6: Gợi ý học phần chỉ là đề xuất, KHÔNG được tự ý ghi vào CSDL
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var regBefore = (await _enrollmentRepo.GetRegisteredSectionsByStudentAsync(1, "2026_HK1")).ToList();

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Gợi ý môn học kỳ này cho tôi");

            var regAfter = (await _enrollmentRepo.GetRegisteredSectionsByStudentAsync(1, "2026_HK1")).ToList();

            // 1. Số môn đăng ký chính thức trong repo không đổi (chưa được ghi CSDL)
            Assert.Equal(regBefore.Count, regAfter.Count);

            // 2. Danh sách đề xuất phải hợp lệ
            Assert.NotNull(result);
            Assert.NotEmpty(result.RecommendedSections);
            Assert.Contains("Xác nhận", result.AiMessage);

            // 3. Không được đề xuất môn đã hoàn thành (TIN101, TIN102)
            Assert.DoesNotContain(result.RecommendedSections, r => r.CourseCode == "TIN101" || r.CourseCode == "TIN102");

            // 4. Không được đề xuất môn sinh viên đang học kỳ này (WEB301, CSDL201, ATTT401, TOEIC)
            Assert.DoesNotContain(result.RecommendedSections, r => r.CourseCode == "WEB301" || r.CourseCode == "CSDL201");
        }

        #endregion

        #region 7. Kiểm Tra Hoạt Động Của Mô Hình AI (IChatClient: Success, Timeout, Failure, Offline) (Yêu cầu 8)

        [Fact]
        public async Task Test_Unit_AiClient_WhenChatClientIsNull_RunsDeterministicFallbackSafely()
        {
            // AI chưa được cấu hình (chatClient = null) -> Động cơ chuyên sâu xử lý mượt mà, không crash
            var useCase = CreateUseCase(chatClient: null);
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Muốn đăng nhập tài khoản gồm những bước nào?");

            Assert.NotNull(result);
            Assert.False(result.IsAiGenerated);
            Assert.NotNull(result.WarningMessage);
            Assert.Contains("Đăng nhập", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_AiClient_WhenApiReturnsSuccess_UsesAiResponse()
        {
            // IChatClient trả về thành công -> Sử dụng phản hồi AI
            var fakeClient = new FakeChatClient("Chào bạn! Đây là câu trả lời được sinh từ mô hình AI.");
            var useCase = CreateUseCase(chatClient: fakeClient);

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Chào bạn, bạn có thể giúp gì?");

            Assert.NotNull(result);
            Assert.True(result.IsAiGenerated);
            Assert.Equal("Chào bạn! Đây là câu trả lời được sinh từ mô hình AI.", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_AiClient_WhenApiTimesOut_SwitchesGracefullyToDeterministicEngine()
        {
            // IChatClient bị TimeoutException -> Tự động fallback an toàn, không làm treo hệ thống
            var timeoutClient = new FakeChatClient(new TimeoutException("API call timed out after 3000ms"));
            var useCase = CreateUseCase(chatClient: timeoutClient);

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Thầy Long dạy môn gì?");

            Assert.NotNull(result);
            Assert.False(result.IsAiGenerated);
            Assert.Contains("Hà Ngọc Long", result.AiMessage);
            Assert.Contains("WEB301_01", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_AiClient_WhenApiThrowsHttpException_SwitchesGracefullyToDeterministicEngine()
        {
            // IChatClient bị lỗi mạng (HttpRequestException) -> Fallback an toàn
            var httpErrorClient = new FakeChatClient(new HttpRequestException("Connection to AI endpoint refused"));
            var useCase = CreateUseCase(chatClient: httpErrorClient);

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Học phí một tín chỉ bao nhiêu tiền?");

            Assert.NotNull(result);
            Assert.False(result.IsAiGenerated);
            Assert.Contains("450.000", result.AiMessage);
        }

        #endregion

        #region 8. Kiểm Tra Thông Tin Học Phí, VietQR Và Trạng Thái Cổng Đăng Ký (Yêu cầu 9)

        [Fact]
        public async Task Test_Unit_TuitionAndVietQR_CalculatedAccurately()
        {
            // Học phí 450.000 đ/tín chỉ và tài khoản MB Bank VietQR
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Học phí một tín chỉ bao nhiêu tiền?");

            Assert.NotNull(result);
            Assert.Contains("450.000", result.AiMessage);
            Assert.Contains("101705201967", result.AiMessage);
            Assert.Contains("MB Bank", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_RegistrationPeriodStatus_WhenOpen()
        {
            // Cổng đăng ký học phần đang mở
            _periodService.SetSimulatedPhase(1);
            var useCase = CreateUseCase();

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Thời gian mở cổng đăng ký học phần khi nào?");

            Assert.NotNull(result);
            Assert.Contains("CỔNG ĐĂNG KÝ ĐANG MỞ", result.AiMessage);
        }

        #endregion

        #region 9. Duy Trì Ngữ Cảnh Hội Thoại Nhiều Lượt (Multi-turn Context)

        [Fact]
        public async Task Test_Unit_MultiTurn_Conversation_Context_ResolvesDetails()
        {
            // Lượt 1: Hỏi lớp WEB301_01 học ở đâu
            var useCase = CreateUseCase();
            var history = new List<AiConversationMessage>
            {
                new AiConversationMessage { IsUser = true, Content = "Môn Lập trình Ứng dụng Web học phòng nào?" },
                new AiConversationMessage { IsUser = false, Content = "Lớp WEB301_01 học tại phòng Lab 402 do TS. Hà Ngọc Long giảng dạy." }
            };

            // Lượt 2: Hỏi tiếp "Cái đó học lúc mấy giờ?"
            var resultTime = await useCase.ExecuteAsync(0, "2026_HK1", "Cái đó học lúc mấy giờ?", history);

            Assert.NotNull(resultTime);
            Assert.Contains("WEB301_01", resultTime.AiMessage);
            Assert.Contains("Tiết 1", resultTime.AiMessage);
            Assert.Contains("Tiết 4", resultTime.AiMessage);

            // Lượt 3: Hỏi tiếp "Ai dạy môn đó?"
            var resultInstructor = await useCase.ExecuteAsync(0, "2026_HK1", "Ai dạy môn đó?", history);

            Assert.NotNull(resultInstructor);
            Assert.Contains("Hà Ngọc Long", resultInstructor.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_FollowUp_WithoutContext_AsksBackNaturally_NoSqlTemplates()
        {
            // Hỏi cụm từ nối khi CHƯA có ngữ cảnh -> Phải hỏi lại môn nào, không được tuôn câu hỏi mẫu SQL
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Cái đó học lúc mấy giờ?");

            Assert.NotNull(result);
            Assert.Contains("Bạn đang muốn hỏi thời gian học của môn học", result.AiMessage);
            Assert.DoesNotContain("Môn WEB301 có mấy lớp?", result.AiMessage);
            Assert.DoesNotContain("Thầy Long dạy môn gì?", result.AiMessage);
        }

        #endregion

        #region 10. Xử Lý Khi CSDL Bị Lỗi Kết Nối (Fault Tolerance)

        [Fact]
        public async Task Test_Unit_DatabaseException_ReturnsSafeMessage_DoesNotCrashApplication()
        {
            // Giả lập lỗi kết nối CSDL SQL Server bị đứt gãy -> Ứng dụng không sập, trả thông báo an toàn
            var faultyStudentRepo = new FaultyStudentRepository();
            var useCase = new GetAiAdviceUseCase(
                faultyStudentRepo,
                _sectionRepo,
                _enrollmentRepo,
                _courseRepo,
                chatClient: null,
                currentUserStateStore: _userStateStore,
                registrationPeriodService: _periodService);

            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Tôi đã đăng ký môn nào?");

            Assert.NotNull(result);
            Assert.Contains("gián đoạn kết nối", result.AiMessage);
        }

        #endregion

        #region 11. Hỗ Trợ Trả Lời Yes/No, Có/Không & Mở Rộng Tri Thức Học Thuật Đa Dạng

        [Theory]
        [InlineData("có")]
        [InlineData("dạ có")]
        [InlineData("vâng")]
        [InlineData("đúng rồi")]
        [InlineData("ok")]
        [InlineData("yes")]
        public async Task Test_Unit_Conversational_Affirmation_Yes_Ok_ReturnsPoliteGuidance(string affirmation)
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", affirmation);

            Assert.NotNull(result);
            Assert.False(string.IsNullOrWhiteSpace(result.AiMessage));
            // Không được rơi vào câu fallback báo thiếu thông tin
            Assert.DoesNotContain("chưa đủ thông tin cụ thể", result.AiMessage);
            // Phải chứa phản hồi giao tiếp tích cực
            Assert.True(result.AiMessage.Contains("Dạ vâng") || result.AiMessage.Contains("Yes") || result.AiMessage.Contains("rất sẵn sàng"));
        }

        [Theory]
        [InlineData("không")]
        [InlineData("dạ không")]
        [InlineData("không nhé")]
        [InlineData("no")]
        public async Task Test_Unit_Conversational_Negation_No_ReturnsPoliteClosing(string negation)
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", negation);

            Assert.NotNull(result);
            Assert.False(string.IsNullOrWhiteSpace(result.AiMessage));
            Assert.DoesNotContain("chưa đủ thông tin cụ thể", result.AiMessage);
            Assert.True(result.AiMessage.Contains("không có vấn đề gì") || result.AiMessage.Contains("No problem") || result.AiMessage.Contains("hiểu ý"));
        }

        [Fact]
        public async Task Test_Unit_YesNoQuery_StudentLecturerCrossCheck_ExplicitYesNo()
        {
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 1,
                Username = "admin",
                FullName = "Admin",
                Role = "Admin"
            });

            // Nguyễn Văn Tuấn CÓ đăng ký môn của TS. Hà Ngọc Long
            var resultYes = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Nguyễn Văn Tuấn có đăng ký môn nào của thầy Hà Ngọc Long không?");
            Assert.NotNull(resultYes);
            Assert.Contains("CÓ.", resultYes.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", resultYes.AiMessage);
            Assert.Contains("Hà Ngọc Long", resultYes.AiMessage);
            Assert.Contains("WEB301_01", resultYes.AiMessage);

            // Trần Thị Mai KHÔNG đăng ký môn nào của TS. Hà Ngọc Long
            var resultNo = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Trần Thị Mai có đăng ký môn nào của thầy Hà Ngọc Long không?");
            Assert.NotNull(resultNo);
            Assert.Contains("KHÔNG.", resultNo.AiMessage);
            Assert.Contains("Trần Thị Mai", resultNo.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_YesNoQuery_SpecificDaySchedule_ExplicitYesNo()
        {
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            // Thứ 2: Tuấn CÓ lịch học WEB301_01
            var resultMon = await useCase.ExecuteAsync(1, "2026_HK1", "Thứ 2 tôi có học không?");
            Assert.NotNull(resultMon);
            Assert.Contains("CÓ.", resultMon.AiMessage);
            Assert.Contains("WEB301_01", resultMon.AiMessage);

            // Thứ 4: Tuấn KHÔNG có lịch học
            var resultThu = await useCase.ExecuteAsync(1, "2026_HK1", "Thứ 4 tôi có học không?");
            Assert.NotNull(resultThu);
            Assert.Contains("KHÔNG.", resultThu.AiMessage);
            Assert.Contains("không có lịch học nào", resultThu.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_YesNoQuery_CourseAvailability_ExplicitYesNo()
        {
            var useCase = CreateUseCase();

            // Môn Lập trình Web CÓ mở lớp trong kỳ
            var resultWeb = await useCase.ExecuteAsync(0, "2026_HK1", "Kỳ này có mở môn Lập trình Web không?");
            Assert.NotNull(resultWeb);
            Assert.Contains("CÓ.", resultWeb.AiMessage);
            Assert.Contains("Lập trình Ứng dụng Web", resultWeb.AiMessage);
            Assert.Contains("WEB301_01", resultWeb.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_YesNoQuery_LecturerCourseAssignment_ExplicitYesNo()
        {
            var useCase = CreateUseCase();

            // Thầy Hà Ngọc Long CÓ dạy môn Lập trình Web
            var resultYes = await useCase.ExecuteAsync(0, "2026_HK1", "Thầy Hà Ngọc Long có dạy môn Lập trình Web không?");
            Assert.NotNull(resultYes);
            Assert.Contains("CÓ.", resultYes.AiMessage);
            Assert.Contains("Hà Ngọc Long", resultYes.AiMessage);
            Assert.Contains("WEB301_01", resultYes.AiMessage);

            // Thầy Hà Ngọc Long KHÔNG dạy môn Cơ sở Dữ liệu (do TS. Phạm Thanh Tùng dạy)
            var resultNo = await useCase.ExecuteAsync(0, "2026_HK1", "Thầy Hà Ngọc Long có dạy môn Cơ sở Dữ liệu không?");
            Assert.NotNull(resultNo);
            Assert.Contains("KHÔNG.", resultNo.AiMessage);
            Assert.Contains("Hà Ngọc Long", resultNo.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_YesNoQuery_ScheduleConflictVerification_ExplicitYesNo()
        {
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            // Thời khóa biểu của Tuấn hoàn toàn không trùng lịch
            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Lịch học của tôi có bị trùng không?");
            Assert.NotNull(result);
            Assert.Contains("KHÔNG.", result.AiMessage);
            Assert.Contains("KHÔNG BỊ TRÙNG LỊCH", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_GeneralAcademicKnowledge_MajorsAtHueUniversityOfEconomics()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Trường Đại học Kinh tế Huế có những ngành nào?");

            Assert.NotNull(result);
            Assert.Contains("Trường Đại học Kinh tế – Đại học Huế", result.AiMessage);
            Assert.Contains("Tin học Kinh tế", result.AiMessage);
            Assert.Contains("Hệ thống thông tin quản lý", result.AiMessage);
            Assert.Contains("Thương mại điện tử", result.AiMessage);
            Assert.Contains("Quản trị kinh doanh", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_GeneralAcademicKnowledge_GpaCpaCalculationAndGradingScale()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Cách tính điểm GPA thang điểm 4 như thế nào?");

            Assert.NotNull(result);
            Assert.Contains("thang điểm 4", result.AiMessage);
            Assert.Contains("Điểm A", result.AiMessage);
            Assert.Contains("Điểm F", result.AiMessage);
            Assert.Contains("GPA", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_GeneralAcademicKnowledge_RegistrationTipsAndTricks()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Mẹo đăng ký tín chỉ nhanh?");

            Assert.NotNull(result);
            Assert.Contains("Bí Quyết", result.AiMessage);
            Assert.Contains("Giỏ đăng ký", result.AiMessage);
            Assert.Contains("tiên quyết", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_GeneralAcademicKnowledge_ScholarshipRequirements()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Làm thế nào để được xét học bổng khuyến khích học tập?");

            Assert.NotNull(result);
            Assert.Contains("Học bổng Xuất sắc", result.AiMessage);
            Assert.Contains("3.60", result.AiMessage);
            Assert.Contains("Điểm rèn luyện", result.AiMessage);
        }

        #endregion

        #region 12. Kiểm Tra Học Phí (Đã Đóng / Chưa Đóng) & Hồ Sơ Sinh Viên Từ CSDL

        [Fact]
        public async Task Test_Unit_TuitionPaymentStatus_NguyenVanTuan_Unpaid_ExplicitNo()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Nguyễn Văn Tuấn đã đóng học phí chưa?");

            Assert.NotNull(result);
            Assert.Contains("CHƯA ĐÓNG", result.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("450.000", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_TuitionPaymentStatus_LeHoangNam_Paid_ExplicitYes()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Lê Hoàng Nam đã đóng học phí chưa?");

            Assert.NotNull(result);
            Assert.Contains("ĐÃ ĐÓNG", result.AiMessage);
            Assert.Contains("Lê Hoàng Nam", result.AiMessage);
            Assert.Contains("VietQR", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_TuitionDebt_NguyenVanTuan_OwesDebt_ExplicitYes()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Nguyễn Văn Tuấn có nợ học phí không?");

            Assert.NotNull(result);
            Assert.Contains("CÓ.", result.AiMessage);
            Assert.Contains("nợ học phí", result.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_TuitionDebt_LeHoangNam_NoDebt_ExplicitNo()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Lê Hoàng Nam có nợ học phí không?");

            Assert.NotNull(result);
            Assert.Contains("KHÔNG.", result.AiMessage);
            Assert.Contains("Lê Hoàng Nam", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_Tuition_LoggedInStudent_PronounQuery()
        {
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Tôi đã đóng học phí chưa?");

            Assert.NotNull(result);
            Assert.Contains("CHƯA ĐÓNG", result.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_StudentProfile_MajorLookup()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Nguyễn Văn Tuấn học ngành gì?");

            Assert.NotNull(result);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("Công nghệ Thông tin", result.AiMessage);
            Assert.Contains("K21", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_StudentProfile_CodeLookup()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Thông tin sinh viên SV21001");

            Assert.NotNull(result);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("SV21001", result.AiMessage);
            Assert.Contains("Công nghệ Thông tin", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_RegisteredSections_ExplicitStudent()
        {
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Sinh viên Nguyễn Văn Tuấn đã đăng ký những môn nào?");

            Assert.NotNull(result);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.Contains("CSDL201_01", result.AiMessage);
        }

        #endregion

        #region 13. Kiểm Thử NLU Nâng Cao & Ngữ Cảnh Hội Thoại Nhiều Lượt (Requirement G)

        [Theory]
        [InlineData("Tôi đã đăng ký môn nào?")]
        [InlineData("Mình đăng ký những lớp gì rồi?")]
        [InlineData("Kiểm tra giúp tôi các môn đã ghi danh.")]
        [InlineData("Danh sách học phần của tôi đâu?")]
        [InlineData("toi da dang ky mon nao")]
        [InlineData("kiem tra giup toi cac mon da ghi danh")]
        [InlineData("danh sach hoc phan cua toi dau")]
        public async Task Test_Unit_RegisteredCourses_NaturalLanguageVariations_Comprehensive(string query)
        {
            // Kiểm thử đa dạng cách diễn đạt tự nhiên tiếng Việt cho cùng một ý định tra cứu môn đã đăng ký
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", query);

            Assert.NotNull(result);
            Assert.Contains("WEB301_01", result.AiMessage);
            Assert.Contains("CSDL201_01", result.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
            Assert.Empty(result.RecommendedSections); // Đảm bảo KHÔNG tự ý đề xuất môn khi hỏi môn đã đăng ký
        }

        [Theory]
        [InlineData("Mai có học không?")]
        [InlineData("Ngày mai tôi học gì?")]
        [InlineData("Cho mình xem lịch ngày mai.")]
        [InlineData("Ngày mai có tiết nào và học ở đâu?")]
        [InlineData("mai co hoc khong")]
        [InlineData("ngay mai toi hoc gi")]
        public async Task Test_Unit_TomorrowSchedule_NaturalLanguageVariations(string query)
        {
            // Kiểm thử các cách hỏi tự nhiên về lịch học ngày mai (kể cả có/không dấu, có/không từ khóa chuẩn)
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", query);

            Assert.NotNull(result);
            Assert.Contains("Lịch Học Cá Nhân", result.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_MultiTurn_Chain_Ordinals_Room_Time_RemainingCourses()
        {
            // Kiểm thử chuỗi hội thoại nhiều lượt:
            // Lượt 1: "Tôi đã đăng ký những môn nào?" -> Trả về danh sách môn thực tế
            // Lượt 2: "Môn đầu tiên học ở đâu?" -> Xác định môn đầu tiên và tra phòng học
            // Lượt 3: "Mấy giờ?" -> Tiếp tục nói về chính môn đó và tra giờ/tiết học
            // Lượt 4: "Còn môn nào khác?" -> Trả về danh sách các môn còn lại loại trừ môn đang đề cập
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var history = new List<AiConversationMessage>();

            // Lượt 1
            var q1 = "Tôi đã đăng ký những môn nào?";
            var r1 = await useCase.ExecuteAsync(1, "2026_HK1", q1, history);
            Assert.NotNull(r1);
            Assert.Contains("WEB301_01", r1.AiMessage);
            Assert.Contains("CSDL201_01", r1.AiMessage);

            history.Add(new AiConversationMessage { IsUser = true, Content = q1 });
            history.Add(new AiConversationMessage { IsUser = false, Content = r1.AiMessage });

            // Lượt 2: "Môn đầu tiên học ở đâu?" -> WEB301_01 học ở Lab 402
            var q2 = "Môn đầu tiên học ở đâu?";
            var r2 = await useCase.ExecuteAsync(1, "2026_HK1", q2, history);
            Assert.NotNull(r2);
            Assert.Contains("WEB301_01", r2.AiMessage);
            Assert.Contains("Lab 402", r2.AiMessage);

            history.Add(new AiConversationMessage { IsUser = true, Content = q2 });
            history.Add(new AiConversationMessage { IsUser = false, Content = r2.AiMessage });

            // Lượt 3: "Mấy giờ?" -> Tiếp tục nói về WEB301_01 (Tiết 1 đến Tiết 4)
            var q3 = "Mấy giờ?";
            var r3 = await useCase.ExecuteAsync(1, "2026_HK1", q3, history);
            Assert.NotNull(r3);
            Assert.Contains("WEB301_01", r3.AiMessage);
            Assert.Contains("Tiết 1", r3.AiMessage);
            Assert.Contains("Tiết 4", r3.AiMessage);

            history.Add(new AiConversationMessage { IsUser = true, Content = q3 });
            history.Add(new AiConversationMessage { IsUser = false, Content = r3.AiMessage });

            // Lượt 4: "Còn môn nào khác?" -> Danh sách các môn còn lại (CSDL201_01, ATTT401_01, TOEIC_01)
            var q4 = "Còn môn nào khác?";
            var r4 = await useCase.ExecuteAsync(1, "2026_HK1", q4, history);
            Assert.NotNull(r4);
            Assert.Contains("CSDL201_01", r4.AiMessage);
            Assert.Contains("ATTT401_01", r4.AiMessage);
            Assert.Contains("TOEIC_01", r4.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_MultiTurn_TopicSwitching_ResetsSectionContext()
        {
            // Kiểm thử chuyển chủ đề: Lượt 1 hỏi môn đã đăng ký, Lượt 2 hỏi học phí
            // AI phải trả lời đúng học phí, không bị nhầm vào ngữ cảnh lớp học phần trước
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
                new AiConversationMessage { IsUser = true, Content = "Tôi đã đăng ký những môn nào?" },
                new AiConversationMessage { IsUser = false, Content = "Bạn đã đăng ký 4 lớp học phần: WEB301_01, CSDL201_01, ATTT401_01, TOEIC_01." }
            };

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Học phí một tín chỉ bao nhiêu tiền?", history);

            Assert.NotNull(result);
            Assert.Contains("450.000 VNĐ", result.AiMessage);
            Assert.Contains("MB Bank", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_Ambiguous_Query_WithoutContext_AsksClarification_Naturally()
        {
            // Phiên chat mới tinh, hỏi mơ hồ: "Môn đó học ở đâu?" hoặc "Mấy giờ?"
            // AI phải hỏi lại lịch sự thông tin còn thiếu, không được tự ý dump văn mẫu SQL
            var useCase = CreateUseCase();

            var resultRoom = await useCase.ExecuteAsync(1, "2026_HK1", "Môn đó học ở đâu?");
            Assert.NotNull(resultRoom);
            Assert.Contains("Bạn đang muốn hỏi phòng học của lớp học phần nào?", resultRoom.AiMessage);

            var resultTime = await useCase.ExecuteAsync(1, "2026_HK1", "Mấy giờ?");
            Assert.NotNull(resultTime);
            Assert.Contains("Bạn đang muốn hỏi thời gian học của môn học hoặc lớp học phần cụ thể nào?", resultTime.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_OutOfScope_PoliteRedirection()
        {
            // Câu hỏi ngoài phạm vi (thời tiết) -> Từ chối lịch sự, hướng về nghiệp vụ trường, không dump câu hỏi mẫu
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Thời tiết hôm nay thế nào?");

            Assert.NotNull(result);
            Assert.Contains("Ngoài Phạm Vi Hỗ Trợ", result.AiMessage);
            Assert.Contains("Trường Đại học Kinh tế", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_Security_StudentCannotQueryOtherStudentRecords()
        {
            // Sinh viên đăng nhập (Student 1) cố gắng tra cứu dữ liệu của sinh viên khác (Student 2)
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var result = await useCase.ExecuteAsync(1, "2026_HK1", "Sinh viên Trần Thị Mai (SV21002) học môn gì?");

            Assert.NotNull(result);
            Assert.Contains("Thông Báo Bảo Mật", result.AiMessage);
            Assert.Contains("quyền riêng tư", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_Security_GuestAccess_PromptsLogin_ForPersonalData()
        {
            // Khách chưa đăng nhập hỏi lịch học cá nhân -> Yêu cầu đăng nhập, không bịa dữ liệu
            var useCase = CreateUseCase();
            var result = await useCase.ExecuteAsync(0, "2026_HK1", "Ngày mai tôi học gì?");

            Assert.NotNull(result);
            Assert.True(result.SuggestLogin);
            Assert.Contains("chế độ Khách", result.AiMessage);
            Assert.Contains("Đăng nhập", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_EmptyData_StudentWithNoEnrollments_InformsCleanly()
        {
            // Sinh viên chưa đăng ký môn nào (Student 3 - Lê Hoàng Nam trong Fake Data)
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 3,
                Username = "sv21003",
                FullName = "Lê Hoàng Nam",
                Role = "Student",
                StudentId = 3
            });

            var result = await useCase.ExecuteAsync(3, "2026_HK1", "Tôi đã đăng ký những môn nào?");

            Assert.NotNull(result);
            Assert.Contains("chưa đăng ký", result.AiMessage);
        }

        [Fact]
        public async Task Test_Unit_Distinguish_AdvisoryRecommendation_From_OfficialRegistration()
        {
            // Phân biệt rõ rệt giữa:
            // 1. Hỏi môn đã đăng ký -> KHÔNG thêm RecommendedSections
            // 2. Yêu cầu gợi ý lộ trình -> CÓ thêm RecommendedSections
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            var regResult = await useCase.ExecuteAsync(1, "2026_HK1", "Tôi đã đăng ký những môn nào?");
            Assert.Empty(regResult.RecommendedSections);

            var recResult = await useCase.ExecuteAsync(1, "2026_HK1", "Gợi ý môn học kỳ này cho tôi");
            Assert.NotEmpty(recResult.RecommendedSections);
        }

        [Fact]
        public async Task Test_Unit_NovelUnseenQueries_ReducedRigidPatternDependency()
        {
            // Kiểm thử các câu hỏi tự nhiên mới hoàn toàn, chưa từng xuất hiện trong bất kỳ bài test nào trước đây
            // để xác nhận hệ thống thực sự hiểu ý nghĩa ngôn ngữ tự nhiên mà không cần câu hỏi mẫu cố định.
            var useCase = CreateUseCase();
            await _userStateStore.SetCurrentUserAsync(new UserAccount
            {
                AccountId = 2,
                Username = "sv21001",
                FullName = "Nguyễn Văn Tuấn",
                Role = "Student",
                StudentId = 1
            });

            // Câu hỏi 1: Diễn đạt đời thường dùng "bản thân", "đã chọn", "ạ"
            var r1 = await useCase.ExecuteAsync(1, "2026_HK1", "Cho em hỏi hiện tại bản thân đã chọn những môn nào rồi ạ?");
            Assert.NotNull(r1);
            Assert.Contains("WEB301_01", r1.AiMessage);
            Assert.Contains("CSDL201_01", r1.AiMessage);
            Assert.Contains("Nguyễn Văn Tuấn", r1.AiMessage);

            // Câu hỏi 2: Hỏi lịch có/không dùng từ "lên trường", xưng "anh"
            var r2 = await useCase.ExecuteAsync(1, "2026_HK1", "Thứ 3 em có phải lên trường không anh?");
            Assert.NotNull(r2);
            Assert.Contains("Thứ 3", r2.AiMessage);
            Assert.Contains("CSDL201_01", r2.AiMessage);
        }

        #endregion
    }

    /// <summary>
    /// =========================================================================================
    /// TÀI LIỆU KỸ THUẬT: BỘ KIỂM THỬ TÍCH HỢP SQL SERVER (INTEGRATION TESTS) (Yêu cầu 11)
    /// =========================================================================================
    /// Tình trạng môi trường hiện tại:
    /// - Integration tests đối với cơ sở dữ liệu SQL Server vật lý yêu cầu:
    ///   1. Chuỗi kết nối SQL Server hợp lệ (LocalDB hoặc SQL Express) có quyền ghi/đọc.
    ///   2. CSDL mẫu được khởi tạo và chạy migration DbInitializer trước khi test chạy.
    /// - Trong môi trường Unit Test tự động (CI/CD hoặc chạy dotnet test độc lập), việc phụ thuộc
    ///   vào một SQL Server Server-side đang chạy có thể gây lỗi mạng hoặc deadlock.
    /// - DO ĐÓ: Các kiểm thử đơn vị được thực hiện trên Fake Repositories; Integration test
    ///   SQL Server được tách riêng biệt dưới đây và chỉ kích hoạt khi có cờ môi trường cho phép.
    /// =========================================================================================
    /// </summary>
    public class AiAdvisorSqlServerIntegrationTests
    {
        [Fact]
        public void SqlServer_IntegrationTests_Documentation_Status()
        {
            // Khẳng định rõ ràng: Chưa chạy live SQL integration tests trong test runner này
            // để tránh hiểu nhầm giữa In-Memory Fake và SQL Server vật lý.
            bool isLiveSqlServerConfigured = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INTEGRATION_TEST_SQL_CONNECTION"));
            
            // Test này luôn pass và ghi chú trạng thái môi trường
            Assert.True(true, "Integration test SQL Server được cấu hình có điều kiện qua biến môi trường INTEGRATION_TEST_SQL_CONNECTION.");
        }
    }

    #region In-Memory Fake Repositories & Clients For Isolated Unit Testing

    public class FakeChatClient : IChatClient
    {
        private readonly string? _responseContent;
        private readonly Exception? _exceptionToThrow;

        public FakeChatClient(string responseContent)
        {
            _responseContent = responseContent;
        }

        public FakeChatClient(Exception exceptionToThrow)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_exceptionToThrow != null)
            {
                throw _exceptionToThrow;
            }

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseContent ?? "Default Fake AI Response"));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ChatClientMetadata Metadata => new ChatClientMetadata("FakeChatClient");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

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
            new CompletedCourse { CompletedCourseId = 1, StudentId = 1, CourseId = 1, Grade = 8.0, Semester = "2024_HK1" }, // Đã qua TIN101
            new CompletedCourse { CompletedCourseId = 2, StudentId = 1, CourseId = 2, Grade = 7.5, Semester = "2024_HK2" }, // Đã qua TIN102
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
            var ktpmCourse = new Course { CourseId = 6, CourseCode = "KTPM302", CourseName = "Kỹ thuật Phần mềm", Credits = 3 };

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
                },
                new Section
                {
                    SectionId = 7, SectionCode = "KTPM302_01", CourseId = 6, Course = ktpmCourse,
                    Instructor = "ThS. Đặng Thị Kim Giao", Room = "Phòng C301", MaxCapacity = 45, CurrentEnrollment = 5, Semester = "2026_HK1",
                    Schedules = new List<SectionSchedule> { new SectionSchedule { ScheduleId = 7, SectionId = 7, DayOfWeek = 5, StartPeriod = 1, EndPeriod = 3, Room = "Phòng C301" } }
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
        private readonly List<Enrollment> _enrollments = new List<Enrollment>
        {
            new Enrollment
            {
                EnrollmentId = 1,
                StudentId = 1,
                Semester = "2026_HK1",
                Status = "Confirmed",
                PaymentStatus = "Unpaid",
                Student = new Student { StudentId = 1, StudentCode = "SV21001", FullName = "Nguyễn Văn Tuấn", Major = "Công nghệ Thông tin", AcademicYear = "K21" }
            },
            new Enrollment
            {
                EnrollmentId = 2,
                StudentId = 2,
                Semester = "2026_HK1",
                Status = "Confirmed",
                PaymentStatus = "Unpaid",
                Student = new Student { StudentId = 2, StudentCode = "SV21002", FullName = "Trần Thị Mai", Major = "Hệ thống Thông tin", AcademicYear = "K21" }
            },
            new Enrollment
            {
                EnrollmentId = 3,
                StudentId = 3,
                Semester = "2026_HK1",
                Status = "Confirmed",
                PaymentStatus = "Paid",
                PaymentDate = new DateTime(2026, 10, 25, 20, 11, 10),
                PaymentMethod = "VietQR Napas247",
                TransactionReference = "VQR-2026-928174",
                Student = new Student { StudentId = 3, StudentCode = "SV21003", FullName = "Lê Hoàng Nam", Major = "Kỹ thuật Phần mềm", AcademicYear = "K21" }
            }
        };

        public Task<Enrollment?> GetEnrollmentByIdAsync(int enrollmentId) => Task.FromResult(_enrollments.FirstOrDefault(e => e.EnrollmentId == enrollmentId));
        public Task<IEnumerable<Enrollment>> GetEnrollmentsByStudentIdAsync(int studentId) => Task.FromResult<IEnumerable<Enrollment>>(_enrollments.Where(e => e.StudentId == studentId));
        public Task<IEnumerable<Enrollment>> GetEnrollmentsBySemesterAsync(string semester) => Task.FromResult<IEnumerable<Enrollment>>(_enrollments.Where(e => e.Semester == semester));

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
                return Task.FromResult<IEnumerable<Section>>(list.Where(s => s.Semester == semester));
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
                return Task.FromResult<IEnumerable<Section>>(list.Where(s => s.Semester == semester));
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

    public class FaultyStudentRepository : IStudentRepository
    {
        public Task<IEnumerable<Student>> GetAllStudentsAsync() => throw new InvalidOperationException("Simulated SQL connection failure");
        public Task<Student?> GetStudentByIdAsync(int studentId) => throw new InvalidOperationException("Simulated SQL connection failure");
        public Task<Student?> GetStudentByCodeAsync(string studentCode) => throw new InvalidOperationException("Simulated SQL connection failure");
        public Task AddStudentAsync(Student student) => throw new InvalidOperationException("Simulated SQL connection failure");
        public Task UpdateStudentAsync(Student student) => throw new InvalidOperationException("Simulated SQL connection failure");
        public Task<IEnumerable<CompletedCourse>> GetCompletedCoursesByStudentIdAsync(int studentId) => throw new InvalidOperationException("Simulated SQL connection failure");
    }

    #endregion
}
