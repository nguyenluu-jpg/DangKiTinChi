using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CourseRegistration.CoreBusiness.Models;
using CourseRegistration.CoreBusiness.Services.Interfaces;
using CourseRegistration.UseCases.PluginInterfaces.DataStore;
using CourseRegistration.UseCases.PluginInterfaces.StateStore;
using Microsoft.Extensions.AI;

namespace CourseRegistration.UseCases.StudentPortal.AiAdvisor
{
    /// <summary>
    /// Implementation của UseCase tư vấn đăng ký học phần thông minh bằng AI
    /// Tuân thủ nghiêm ngặt 5 tiêu chuẩn của giảng viên (Mục 10):
    /// 1. API key đặt trong User Secrets hoặc biến môi trường, không commit lên git.
    /// 2. IChatClient được inject vào một UseCase riêng (GetAiAdviceUseCase), không gọi trực tiếp từ file .razor.
    /// 3. Có xử lý khi API lỗi hoặc hết hạn mức: ứng dụng vẫn chạy, hiển thị thông báo.
    /// 4. AI chỉ gợi ý; dữ liệu do AI tạo phải được người dùng xác nhận trước khi ghi vào CSDL.
    /// 5. Có thể dùng Ollama chạy local để không tốn phí hoặc OpenAI.
    /// </summary>
    public class GetAiAdviceUseCase : IGetAiAdviceUseCase
    {
        private readonly IChatClient? _chatClient;
        private readonly IStudentRepository _studentRepository;
        private readonly ISectionRepository _sectionRepository;
        private readonly IEnrollmentRepository _enrollmentRepository;
        private readonly ICourseRepository _courseRepository;
        private readonly ICurrentUserStateStore? _currentUserStateStore;
        private readonly IRegistrationPeriodService? _registrationPeriodService;

        public GetAiAdviceUseCase(
            IStudentRepository studentRepository,
            ISectionRepository sectionRepository,
            IEnrollmentRepository enrollmentRepository,
            ICourseRepository courseRepository,
            IChatClient? chatClient = null,
            ICurrentUserStateStore? currentUserStateStore = null,
            IRegistrationPeriodService? registrationPeriodService = null)
        {
            _studentRepository = studentRepository;
            _sectionRepository = sectionRepository;
            _enrollmentRepository = enrollmentRepository;
            _courseRepository = courseRepository;
            _chatClient = chatClient;
            _currentUserStateStore = currentUserStateStore;
            _registrationPeriodService = registrationPeriodService;
        }

        public async Task<AiCourseAdvice> ExecuteAsync(
            int studentId,
            string semester,
            string? studentGoalPrompt = null,
            List<AiConversationMessage>? conversationHistory = null)
        {
            var advice = new AiCourseAdvice
            {
                StudentId = studentId,
                Semester = semester
            };

            try
            {
                // 1. Xác thực danh tính sinh viên từ CurrentUserStateStore và ánh xạ chính xác
                string currentRole = "Guest";
                int authenticatedStudentId = 0;
                UserAccount? currentUser = null;

                if (_currentUserStateStore != null)
                {
                    currentRole = await _currentUserStateStore.GetCurrentRoleAsync();
                    authenticatedStudentId = await _currentUserStateStore.GetCurrentStudentIdAsync();
                    currentUser = await _currentUserStateStore.GetCurrentUserAsync();
                }

                // Nếu caller truyền studentId <= 0 nhưng đang đăng nhập, lấy studentId từ phiên
                if (studentId <= 0 && authenticatedStudentId > 0)
                {
                    studentId = authenticatedStudentId;
                    advice.StudentId = studentId;
                }

                // 2. Trích xuất toàn bộ dữ liệu CSDL SQL Server thời gian thực
                var allCourses = (await _courseRepository.GetAllCoursesAsync()).ToList();
                var allSections = (await _sectionRepository.GetSectionsBySemesterAsync(semester)).ToList();
                var allSectionsAcrossSemesters = (await _sectionRepository.GetAllSectionsAsync()).ToList();
                var allPrerequisites = (await _courseRepository.GetAllPrerequisitesAsync()).ToList();
                var allStudents = (await _studentRepository.GetAllStudentsAsync()).ToList();

                Student? student = null;
                var completedCourses = new List<CompletedCourse>();
                var registeredSections = new List<Section>();
                var passedCourseIds = new HashSet<int>();
                var registeredCourseIds = new HashSet<int>();
                int remainingCreditsAllowed = 18;

                if (studentId > 0)
                {
                    student = allStudents.FirstOrDefault(s => s.StudentId == studentId)
                              ?? await _studentRepository.GetStudentByIdAsync(studentId);

                    if (student != null)
                    {
                        advice.StudentCode = student.StudentCode;
                        advice.StudentName = student.FullName;

                        completedCourses = (await _studentRepository.GetCompletedCoursesByStudentIdAsync(studentId)).ToList();
                        passedCourseIds = completedCourses.Where(c => c.Grade >= 5.0).Select(c => c.CourseId).ToHashSet();

                        registeredSections = (await _enrollmentRepository.GetRegisteredSectionsByStudentAsync(studentId, semester)).ToList();
                        registeredCourseIds = registeredSections.Select(r => r.CourseId).ToHashSet();

                        int currentCredits = registeredSections.Sum(r => r.Course?.Credits ?? 0);
                        remainingCreditsAllowed = Math.Max(0, student.MaxCreditsPerSemester - currentCredits);
                    }
                }

                string userPrompt = string.IsNullOrWhiteSpace(studentGoalPrompt) ? string.Empty : studentGoalPrompt.Trim();
                bool isExplicitRec = IsExplicitRecommendationRequest(userPrompt);

                // 3. Lọc danh sách lớp ứng viên hợp lệ cho đề xuất học phần (chỉ khi có yêu cầu gợi ý rõ ràng)
                var candidateSections = new List<Section>();
                foreach (var sec in allSections)
                {
                    if (sec.Course == null) continue;
                    if (passedCourseIds.Contains(sec.CourseId)) continue;
                    if (registeredCourseIds.Contains(sec.CourseId)) continue;

                    var prereqCourseIds = allPrerequisites
                        .Where(p => p.CourseId == sec.CourseId)
                        .Select(p => p.PrerequisiteCourseId)
                        .ToList();
                    bool prereqSatisfied = prereqCourseIds.All(pid => passedCourseIds.Contains(pid));
                    if (!prereqSatisfied) continue;

                    if (sec.CurrentEnrollment >= sec.MaxCapacity) continue;

                    candidateSections.Add(sec);
                }

                // 4. THỬ NGHIỆM GỌI MÔ HÌNH AI (IChatClient - OpenAI / Ollama Local)
                bool aiSuccess = false;
                if (_chatClient != null && !string.IsNullOrWhiteSpace(userPrompt))
                {
                    try
                    {
                        // Phân quyền System Prompt chuyên biệt theo 3 vai trò: Khách, Sinh viên, Quản trị
                        string systemPrompt;
                        if (currentRole == "Admin")
                        {
                            systemPrompt = BuildAdminSystemPrompt(allSectionsAcrossSemesters, allCourses, allStudents, semester);
                        }
                        else if (currentRole == "Student" && student != null)
                        {
                            systemPrompt = BuildStudentSystemPrompt(
                                student,
                                completedCourses,
                                registeredSections,
                                remainingCreditsAllowed,
                                allSectionsAcrossSemesters,
                                allCourses,
                                allPrerequisites,
                                semester);
                        }
                        else
                        {
                            systemPrompt = BuildGuestSystemPrompt(allCourses, allSectionsAcrossSemesters, semester);
                        }

                        var messages = new List<ChatMessage>
                        {
                            new ChatMessage(ChatRole.System, systemPrompt)
                        };

                        if (conversationHistory != null && conversationHistory.Any())
                        {
                            foreach (var hist in conversationHistory.TakeLast(6))
                            {
                                messages.Add(new ChatMessage(hist.IsUser ? ChatRole.User : ChatRole.Assistant, hist.Content));
                            }
                        }

                        messages.Add(new ChatMessage(ChatRole.User, userPrompt));

                        // Đặt timeout 3 giây để đảm bảo trải nghiệm siêu nhanh nếu API chậm / offline
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var response = await _chatClient.GetResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, cts.Token);
                        var responseText = response.Text?.Trim() ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(responseText))
                        {
                            advice.AiMessage = responseText;
                            advice.IsAiGenerated = true;
                            advice.ModelUsed = "Trí Tuệ Nhân Tạo AI (IChatClient - LLM)";
                            aiSuccess = true;

                            // CHỈ bổ sung vào RecommendedSections khi người dùng YÊU CẦU GỢI Ý MÔN RÕ RÀNG
                            if (isExplicitRec && student != null)
                            {
                                foreach (var sec in candidateSections)
                                {
                                    if (responseText.Contains(sec.SectionCode, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!advice.RecommendedSections.Any(r => r.SectionId == sec.SectionId))
                                        {
                                            advice.RecommendedSections.Add(new AiRecommendedSection
                                            {
                                                SectionId = sec.SectionId,
                                                SectionCode = sec.SectionCode,
                                                CourseId = sec.CourseId,
                                                CourseCode = sec.Course?.CourseCode ?? string.Empty,
                                                CourseName = sec.Course?.CourseName ?? string.Empty,
                                                Credits = sec.Course?.Credits ?? 0,
                                                Instructor = sec.Instructor,
                                                Room = sec.Room,
                                                ScheduleSummary = FormatSchedule(sec),
                                                RecommendationReason = $"Được AI đề xuất dựa trên nguyện vọng '{userPrompt}'.",
                                                IsSelected = true
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Ngoại lệ API / Hết hạn mức / Mất kết nối / Timeout -> Tự động chuyển fallback an toàn
                        // Tuyệt đối không log thông tin nhạy cảm hoặc API key
                        Console.WriteLine($"[AiAdvisor] IChatClient fallback: {ex.GetType().Name} - {ex.Message}");
                        aiSuccess = false;
                    }
                }

                // 5. ĐỘNG CƠ CỐ VẤN TRI THỨC SQL SERVER CHUYÊN SÂU (FALLBACK / CORE ENGINE KHI OFFLINE / KHÔNG CÓ API KEY)
                // Đảm bảo hiểu câu hỏi tiếng Việt & tiếng Anh, xác định đúng ý định, truy vấn chính xác SQL Server!
                if (!aiSuccess)
                {
                    advice.IsAiGenerated = false;
                    advice.ModelUsed = "Động Cơ Tri Thức SQL Server Chuyên Sâu";
                    advice.WarningMessage = "💡 Cố vấn AI đang hoạt động ở chế độ Động cơ Tri thức Học thuật Chuyên sâu (truy vấn trực tiếp dữ liệu thực tế từ SQL Server). Kết quả được trích xuất chính xác theo thời gian thực.";

                    ExecuteAcademicAdvisorEngine(
                        advice,
                        userPrompt,
                        student,
                        currentRole,
                        authenticatedStudentId,
                        allCourses,
                        allSections,
                        allPrerequisites,
                        allStudents,
                        candidateSections,
                        completedCourses,
                        registeredSections,
                        remainingCreditsAllowed,
                        semester,
                        conversationHistory,
                        allSectionsAcrossSemesters,
                        _registrationPeriodService,
                        _enrollmentRepository);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AiAdvisor] Critical exception in ExecuteAsync: {ex.GetType().Name} - {ex.Message}");
                advice.IsAiGenerated = false;
                advice.ModelUsed = "Hệ Thống An Toàn";
                advice.AiMessage = "⚠️ Hiện tại hệ thống đang gặp gián đoạn kết nối tới cơ sở dữ liệu học vụ. Cố vấn AI chưa thể truy vấn dữ liệu học vụ vào lúc này. Vui lòng thử lại sau giây lát hoặc liên hệ Ban Quản Trị Hệ Thống.";
                advice.WarningMessage = "Lỗi kết nối cơ sở dữ liệu.";
            }

            return advice;
        }

        /// <summary>
        /// Động cơ Phân tích Tri thức & Xử lý Ngôn ngữ Tự nhiên Học thuật:
        /// Hiểu tiếng Việt & tiếng Anh, nhận diện thực thể, phân quyền sinh viên, đối chiếu chính xác dữ liệu SQL Server.
        /// </summary>
        private static void ExecuteAcademicAdvisorEngine(
            AiCourseAdvice advice,
            string userPrompt,
            Student? currentStudent,
            string currentRole,
            int authenticatedStudentId,
            List<Course> allCourses,
            List<Section> allSections,
            List<Prerequisite> allPrerequisites,
            List<Student> allStudents,
            List<Section> candidateSections,
            List<CompletedCourse> completedCourses,
            List<Section> registeredSections,
            int remainingCreditsAllowed,
            string semester,
            List<AiConversationMessage>? conversationHistory,
            List<Section>? allSectionsAcrossSemesters,
            IRegistrationPeriodService? registrationPeriodService,
            IEnrollmentRepository enrollmentRepository)
        {
            var sectionsPool = allSectionsAcrossSemesters != null && allSectionsAcrossSemesters.Any()
                ? allSectionsAcrossSemesters
                : allSections;

            // Câu hỏi trống: Hiển thị lời chào cá nhân hóa
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                if (currentStudent != null)
                {
                    advice.AiMessage = $"### 👋 Chào bạn {currentStudent.FullName} (MSSV: `{currentStudent.StudentCode}`)!\n\n" +
                                       $"Tôi là **Cố vấn Học thuật AI** của bạn trong học kỳ **{semester}**. Bạn có thể hỏi tôi bằng tiếng Việt hoặc tiếng Anh:\n" +
                                       $"- *Lịch học cá nhân:* 'Thứ 2 tôi học gì?', 'Ngày mai tôi học gì?', 'Thời khóa biểu của tôi' (hoặc 'Do I have any classes tomorrow?', 'What is my schedule this week?').\n" +
                                       $"- *Đối chiếu giảng viên:* 'Tôi có đăng ký môn nào của thầy Hà Ngọc Long không?'.\n" +
                                       $"- *Tra cứu toàn trường:* Môn học, lớp học phần, giảng viên, phòng học, sĩ số lớp.\n" +
                                       $"- *Học bạ & Tiên quyết:* Điểm số tích lũy, các môn đã hoàn thành, điều kiện tiên quyết.\n" +
                                       $"- *Quy chế & Học phí:* Rút môn học, hạn mức 10-24 tín chỉ, đơn giá 450.000 đ/TC và VietQR.\n" +
                                       $"- *Tư vấn lộ trình:* 'Gợi ý môn học kỳ này cho tôi' (hoặc 'Recommend courses for me').";
                }
                else
                {
                    advice.AiMessage = $"### 👋 Chào bạn! Tôi là Cố Vấn Học Thuật AI.\n\n" +
                                       $"Tôi có thể hỗ trợ bạn tra cứu mọi thông tin học vụ trong CSDL trường (môn học, giảng viên, phòng học, lịch học, học phí, quy chế tín chỉ) bằng tiếng Việt hoặc tiếng Anh. Hãy **Đăng nhập** để tra cứu thời khóa biểu và học bạ cá nhân của riêng bạn!";
                }
                return;
            }

            string qRaw = userPrompt.Trim();
            string qLower = qRaw.ToLowerInvariant();
            string qNorm = RemoveDiacritics(qLower);

            // Xác định ngôn ngữ câu hỏi (English vs Vietnamese)
            bool isEnglish = DetectIsEnglish(qLower, qNorm);

            // Duy trì ngữ cảnh hội thoại từ các lượt hỏi trước
            var context = ExtractConversationContext(conversationHistory, allCourses, sectionsPool, allStudents);

            // =========================================================================
            // PHÂN LOẠI Ý ĐỊNH ĐẦU TIÊN (TOP-LEVEL INTENT CLASSIFICATION)
            // Xử lý đúng mục đích các câu hỏi giao tiếp, hướng dẫn sử dụng, thao tác giao diện,
            // chức năng hệ thống hoặc ngoài phạm vi TRƯỚC KHI truy vấn CSDL SQL Server!
            // Tuyệt đối không ép người dùng theo mẫu câu và không mặc định tra cứu CSDL!
            // =========================================================================

            // 1. Chào hỏi (Greeting)
            if (IsGreetingIntent(qNorm, qLower))
            {
                if (isEnglish)
                {
                    advice.AiMessage = currentStudent != null
                        ? $"### 👋 Hello **{currentStudent.FullName}** (`{currentStudent.StudentCode}`)!\n\nI am your **AI Academic Advisor** for semester **{semester}** at Hue University of Economics.\n\nHow can I help you today with your courses, schedule, tuition, or academic regulations?"
                        : $"### 👋 Hello! I am the **AI Academic Advisor** for Hue University of Economics.\n\nI can help you look up courses, schedules, lecturers, tuition, and guide you through the registration system. How can I assist you today?";
                }
                else
                {
                    advice.AiMessage = currentStudent != null
                        ? $"### 👋 Chào bạn **{currentStudent.FullName}** (`{currentStudent.StudentCode}`)!\n\nTôi là **Cố vấn Học thuật AI** của bạn trong học kỳ **{semester}** tại Trường Đại học Kinh tế Huế.\n\nTôi có thể hỗ trợ bạn tra cứu thời khóa biểu cá nhân, môn học, giảng viên, phòng học, học phí VietQR và tư vấn lộ trình học tập. Bạn cần tôi hỗ trợ thông tin gì hôm nay?"
                        : $"### 👋 Chào bạn! Tôi là **Cố Vấn Học Thuật AI** của Trường Đại học Kinh tế Huế.\n\nTôi có thể hỗ trợ bạn tra cứu danh mục môn học, lịch học, thông tin giảng viên, mức học phí và hướng dẫn sử dụng hệ thống đăng ký tín chỉ. Bạn cần tôi hỗ trợ thông tin gì hôm nay?";
                }
                return;
            }

            // 2. Cảm ơn (Thanks)
            if (IsThanksIntent(qNorm, qLower))
            {
                advice.AiMessage = isEnglish
                    ? "✨ **You are very welcome!**\n\nIf you have any further questions about your courses, timetable, or academic regulations, feel free to ask anytime. Wishing you a great and productive semester!"
                    : "✨ **Rất vui được hỗ trợ bạn!**\n\nNếu có thêm bất kỳ thắc mắc nào về môn học, thời khóa biểu hay quy chế đào tạo, bạn cứ thoải mái hỏi tôi nhé. Chúc bạn có một học kỳ học tập thật tốt và thuận lợi!";
                return;
            }

            // 3. Tạm biệt (Farewell)
            if (IsFarewellIntent(qNorm, qLower))
            {
                advice.AiMessage = isEnglish
                    ? "👋 **Goodbye!**\n\nHave a wonderful day and best of luck with your studies. See you next time whenever you need academic assistance!"
                    : "👋 **Tạm biệt bạn!**\n\nChúc bạn một ngày tràn đầy năng lượng và học tập thật hiệu quả. Hẹn gặp lại bạn khi cần hỗ trợ học vụ nhé!";
                return;
            }

            // 4. Hướng dẫn Đăng nhập vào Hệ thống (Login Guide)
            if (IsLoginInstructionIntent(qNorm, qLower))
            {
                if (isEnglish)
                {
                    advice.AiMessage =
                        "### 🔐 How to Log In to the Course Registration Portal:\n\n" +
                        "To log in to your account, please follow these **3 simple steps**:\n\n" +
                        "1. **Step 1 - Open the Login dialog:**\n" +
                        "   - Click the blue **\"Log in\"** button at the top-right corner of the navigation bar, or navigate to `/login`.\n\n" +
                        "2. **Step 2 - Enter your credentials:**\n" +
                        "   - **Student ID / Username:** Enter your Student Code (e.g., `SV21001`, `SV21002`) or Administrator account (`admin`).\n" +
                        "   - **Password:** Enter your account password (default for students is `sv123`, for admins is `admin123`). Click the eye icon if you want to view the entered password.\n\n" +
                        "3. **Step 3 - Authenticate:**\n" +
                        "   - Click the blue **\"Log In\"** button. Upon successful verification, the system will load your personalized timetable, registered courses, and credit limit.";
                }
                else
                {
                    advice.AiMessage =
                        "### 🔐 Hướng Dẫn Các Bước Đăng Nhập Hệ Thống:\n\n" +
                        "Để đăng nhập vào cổng Đăng ký học phần, bạn thực hiện theo **3 bước đơn giản** sau:\n\n" +
                        "1. **Bước 1 - Mở hộp thoại Đăng nhập:**\n" +
                        "   - Nhấn vào nút **\"Đăng nhập\"** màu xanh ở góc trên bên phải thanh điều hướng (Navbar), hoặc truy cập đường dẫn `/login`.\n\n" +
                        "2. **Bước 2 - Nhập thông tin tài khoản:**\n" +
                        "   - **Tài khoản / MSSV:** Nhập Mã số sinh viên của bạn (ví dụ: `SV21001`, `SV21002`, `SV21003`) hoặc tài khoản cán bộ quản trị (`admin`).\n" +
                        "   - **Mã truy cập (Mật khẩu):** Nhập mật khẩu của bạn (mật khẩu mặc định cho sinh viên là `sv123`, cho cán bộ quản trị là `admin123`). Bạn có thể bấm biểu tượng con mắt để xem lại mật khẩu đã gõ.\n\n" +
                        "3. **Bước 3 - Xác thực và hoàn tất:**\n" +
                        "   - Bấm nút **\"Đăng Nhập\"** (màu xanh). Hệ thống sẽ xác thực và tự động tải thông tin sinh viên, thời khóa biểu và hạn mức tín chỉ cá nhân của bạn.";
                }

                if (currentStudent == null)
                {
                    advice.SuggestLogin = true;
                }
                return;
            }

            // 5. Hướng dẫn Đăng xuất, Đổi mật khẩu & Đăng ký tài khoản (Logout & Account Operations)
            if (IsLogoutOrAccountIntent(qNorm, qLower, out string accountAction))
            {
                if (accountAction == "Logout")
                {
                    advice.AiMessage = isEnglish
                        ? "### 🚪 How to Log Out of the System:\n\n" +
                          "1. Click on your **User Avatar / Full Name** at the top-right corner of the navigation bar.\n" +
                          "2. In the dropdown menu, scroll to the bottom and click the red **\"Log out\"** option (box with an exit arrow).\n" +
                          "3. Your session will be safely cleared, and you will return to Guest mode."
                        : "### 🚪 Hướng Dẫn Đăng Xuất Khỏi Hệ Thống:\n\n" +
                          "Để đăng xuất tài khoản an toàn, bạn thực hiện như sau:\n\n" +
                          "1. Nhấn vào **Tên / Ảnh đại diện (Avatar)** của bạn ở góc trên bên phải thanh điều hướng (Navbar).\n" +
                          "2. Trong menu thả xuống, kéo xuống dưới cùng và chọn mục màu đỏ **\"Đăng xuất\"** (biểu tượng hộp có mũi tên).\n" +
                          "3. Phiên làm việc của bạn sẽ được kết thúc an toàn và giao diện trở về trạng thái Khách.";
                }
                else if (accountAction == "ChangePassword")
                {
                    advice.AiMessage = isEnglish
                        ? "### 🔑 How to Change Your Password:\n\n" +
                          "1. Click on your **User Avatar / Full Name** at the top-right corner of the navigation bar.\n" +
                          "2. Select **\"Change Password\"** (or visit `/change-password`).\n" +
                          "3. Enter your current password, new password, confirm new password, and click **\"Save Changes\"**."
                        : "### 🔑 Hướng Dẫn Đổi Mật Khẩu Tài Khoản:\n\n" +
                          "1. Bấm vào **Tên / Ảnh đại diện tài khoản** ở góc trên bên phải thanh điều hướng (Navbar).\n" +
                          "2. Chọn mục **\"Đổi mật khẩu\"** (hoặc truy cập trực tiếp `/change-password`).\n" +
                          "3. Nhập mật khẩu hiện tại, nhập mật khẩu mới và xác nhận mật khẩu mới, sau đó bấm nút **\"Xác nhận đổi mật khẩu\"**.";
                }
                else
                {
                    advice.AiMessage = isEnglish
                        ? "### 📝 Account Registration Information:\n\n" +
                          "Student and faculty accounts are officially provisioned by the **Academic Affairs Office of Hue University of Economics** upon enrollment (using your Student Code, default password `sv123`).\n\n" +
                          "The portal does not open self-registration to ensure academic integrity and student record privacy. If you do not have an account or lost access, please contact the Academic Affairs Office for assistance."
                        : "### 📝 Thông Tin Về Đăng Ký Tài Khoản:\n\n" +
                          "Tài khoản sinh viên và cán bộ do **Phòng Đào Tạo Trường Đại học Kinh tế Huế** khởi tạo và cấp mặc định theo Mã số sinh viên (MSSV) ngay khi nhập học (mật khẩu ban đầu là `sv123`).\n\n" +
                          "Hệ thống không mở tính năng tự do đăng ký tài khoản mới để đảm bảo an ninh và tính xác thực tuyệt đối của học bạ. Nếu bạn chưa có tài khoản hoặc quên thông tin đăng nhập, vui lòng liên hệ Văn phòng Đào tạo để được cấp lại.";
                }
                return;
            }

            // 6. Giới thiệu chức năng hệ thống (System Feature Guide)
            if (IsSystemFeatureGuideIntent(qNorm, qLower))
            {
                advice.AiMessage = isEnglish
                    ? "### 🌐 Course Registration Portal Overview & Main Features:\n\n" +
                      "This system is designed for students and faculty of Hue University of Economics with 5 primary modules:\n\n" +
                      "1. 🔍 **Course & Section Search (`/sections`):** Browse the course catalog, filter by department, inspect class schedules, lecturers, rooms, and remaining capacity.\n" +
                      "2. 🛒 **Registration Cart (`/cart`):** Add desired classes to your staging cart, validate prerequisites and schedule conflicts before final enrollment.\n" +
                      "3. 🗓️ **Personal Timetable (`/schedule`):** View your enrolled classes in weekly calendar view or daily list format.\n" +
                      "4. 💳 **Tuition Payment (`/tuition`):** Check fee calculation (450,000 VNĐ/credit) and pay seamlessly via instant VietQR Napas 24/7 (MB Bank).\n" +
                      "5. 🤖 **AI Academic Advisor (`/ai-advisor`):** 24/7 intelligent advisor supporting bilingual natural language inquiries, timetable cross-checks, and personalized course planning."
                    : "### 🌐 Giới Thiệu Chức Năng Của Hệ Thống Đăng Ký Học Phần:\n\n" +
                      "Hệ thống cung cấp đầy đủ các tiện ích phục vụ sinh viên và cán bộ Trường Đại học Kinh tế Huế với 5 phân hệ chính:\n\n" +
                      "1. 🔍 **Tra cứu & Tìm kiếm học phần (`/sections`):** Xem toàn bộ danh mục môn học, lọc theo khoa/ngành, kiểm tra sĩ số, phòng học, giảng viên và thời khóa biểu trực quan.\n" +
                      "2. 🛒 **Giỏ đăng ký học phần (`/cart`):** Chọn trước các lớp học phần dự kiến, hệ thống tự động kiểm tra trùng lịch và điều kiện tiên quyết trước khi bấm Xác nhận đăng ký vào CSDL.\n" +
                      "3. 🗓️ **Thời khóa biểu cá nhân (`/schedule`):** Xem lịch học của riêng bạn theo dạng tuần hoặc danh sách chi tiết theo ngày, ca học, phòng học.\n" +
                      "4. 💳 **Thanh toán học phí trực tuyến (`/tuition`):** Tra cứu công nợ theo số tín chỉ (đơn giá 450.000 đ/TC) và thanh toán tự động qua mã VietQR Napas 24/7 (MB Bank).\n" +
                      "5. 🤖 **Cố vấn Học thuật AI (`/ai-advisor`):** Trợ lý ảo thông minh 24/7 hỗ trợ giải đáp quy chế tín chỉ, đối chiếu lịch học và gợi ý lộ trình học tập tối ưu.";
                return;
            }

            // 7. Câu hỏi ngoài phạm vi hỗ trợ học vụ (Out-of-Scope Intent)
            if (IsOutOfScopeIntent(qNorm, qLower))
            {
                advice.AiMessage = isEnglish
                    ? "### ℹ️ Topic Outside Academic Advisor Scope\n\n" +
                      "I apologize, but as the **AI Academic Advisor** for Hue University of Economics, my expertise is dedicated to course registration, class timetables, tuition fees, and university training regulations.\n\n" +
                      "I cannot answer questions on general non-academic topics such as weather, entertainment, or lifestyle. If you need any assistance regarding courses, schedules, or using this portal, please feel free to ask!"
                    : "### ℹ️ Câu Hỏi Nằm Ngoài Phạm Vi Hỗ Trợ Học Vụ\n\n" +
                      "Xin lỗi bạn, tôi là **Cố vấn Học thuật AI** chuyên trách thông tin đào tạo, thời khóa biểu, đăng ký học phần và quy chế học vụ của Trường Đại học Kinh tế Huế.\n\n" +
                      "Tôi không phụ trách và không có dữ liệu cho các chủ đề ngoài phạm vi học tập như thời tiết, thể thao, tin tức đời sống hay giải trí. Nếu bạn cần hỗ trợ về môn học, lịch học, giảng viên, học phí hay hướng dẫn đăng ký tín chỉ, hãy đặt câu hỏi nhé!";
                return;
            }

            // 8. Người dùng chủ động yêu cầu gợi ý mẫu câu hỏi (Explicit Sample Questions Request)
            if (IsExplicitSampleQuestionRequest(qNorm, qLower))
            {
                advice.AiMessage = isEnglish
                    ? "### 💡 Helpful Inquiries You Can Ask Me:\n\n" +
                      "- **Personal Timetable:** *\"Do I have any classes tomorrow?\"*, *\"What is my schedule this week?\"*\n" +
                      "- **Registered Courses:** *\"What courses have I registered for?\"*\n" +
                      "- **Lecturer Cross-Check:** *\"Did I register for any course of lecturer Ha Ngoc Long?\"*\n" +
                      "- **Course & Section Lookup:** *\"Who teaches Web Programming?\"*, *\"What classes are open for Database?\"*\n" +
                      "- **Lecturer Lookup:** *\"Which classes does Dr. Ha Ngoc Long teach?\"*\n" +
                      "- **Tuition & Regulations:** *\"How much is tuition fee per credit?\"*, *\"What is the credit limit per semester?\"*\n" +
                      "- **Portal Guides:** *\"How do I log in?\"*, *\"How do I log out?\"*, *\"What features does this portal offer?\"*"
                    : "### 💡 Các Chủ Đề Bạn Có Thể Hỏi Cố Vấn AI:\n\n" +
                      "- **Thời khóa biểu cá nhân:** *\"Ngày mai tôi học gì?\"*, *\"Thứ 2 tôi học gì?\"*, *\"Lịch học tuần này của tôi\"*\n" +
                      "- **Môn học đã đăng ký:** *\"Tôi đã đăng ký những môn nào?\"*\n" +
                      "- **Đối chiếu sinh viên & giảng viên:** *\"Tôi có đăng ký môn nào của thầy Hà Ngọc Long không?\"*\n" +
                      "- **Tra cứu môn học:** *\"Môn Lập trình Web do ai dạy và có mấy lớp?\"*, *\"Môn Cơ sở dữ liệu học phòng nào?\"*\n" +
                      "- **Tra cứu giảng viên:** *\"Thầy Long dạy môn gì?\"*\n" +
                      "- **Học phí & Quy chế:** *\"Học phí 1 tín chỉ bao nhiêu tiền?\"*, *\"Mỗi kỳ được đăng ký tối đa bao nhiêu tín chỉ?\"*\n" +
                      "- **Hướng dẫn hệ thống:** *\"Muốn đăng nhập tài khoản gồm những bước nào?\"*, *\"Làm sao để đăng xuất?\"*, *\"Hệ thống có những chức năng gì?\"*";
                return;
            }

            // =========================================================================
            // BƯỚC 1: TRÍCH XUẤT THỰC THỂ (ENTITY EXTRACTION)
            // =========================================================================
            var (matchedInstructorName, matchedInstructorSections, duplicateInstructorNames) = FindMatchingInstructor(qNorm, qLower, sectionsPool);
            var matchedCourse = FindMatchingCourse(qNorm, qLower, allCourses);
            var matchedSection = FindMatchingSection(qLower, sectionsPool);
            var matchedExplicitStudent = FindMatchingStudent(qNorm, qLower, allStudents);
            var queriedDay = DetectTargetDay(qNorm, qLower);
            bool isWeekQuery = DetectIsWeekQuery(qNorm, qLower);
            string? matchedRoom = FindMatchingRoom(qNorm, qLower, sectionsPool);

            // Nhận diện đại từ nhân xưng chỉ bản thân người hỏi (tôi, em, mình, I, me, my)
            bool hasPersonalPronoun = DetectHasPersonalPronoun(qNorm, qLower);

            // =========================================================================
            // BƯỚC 2: PHÂN GIẢI DANH TÍNH SINH VIÊN & XÁC THỰC QUYỀN TRUY CẬP (SECURITY)
            // =========================================================================
            Student? targetStudent = null;
            bool isTargetingOtherStudent = false;

            if (matchedExplicitStudent != null)
            {
                // Có nhắc đích danh một sinh viên trong câu hỏi
                targetStudent = matchedExplicitStudent;
                if (currentStudent != null && matchedExplicitStudent.StudentId != currentStudent.StudentId)
                {
                    isTargetingOtherStudent = true;
                }
                else if (currentStudent == null && currentRole != "Admin")
                {
                    isTargetingOtherStudent = true;
                }
            }
            else if (hasPersonalPronoun || queriedDay.HasValue || isWeekQuery || IsPersonalRegistrationQuery(qNorm, qLower))
            {
                // Sử dụng đại từ nhân xưng hoặc hỏi lịch cá nhân -> Gán về sinh viên đang đăng nhập
                targetStudent = currentStudent;
            }
            else if (context.LastReferencedStudent != null)
            {
                // Kế thừa sinh viên từ ngữ cảnh hội thoại trước
                targetStudent = context.LastReferencedStudent;
                if (currentStudent != null && targetStudent.StudentId != currentStudent.StudentId)
                {
                    isTargetingOtherStudent = true;
                }
            }

            // KIỂM TRA BẢO MẬT: Sinh viên thường KHÔNG được tra cứu dữ liệu cá nhân của sinh viên khác!
            if (isTargetingOtherStudent && currentRole != "Admin")
            {
                if (isEnglish)
                {
                    advice.AiMessage = $"🔒 **Privacy & Access Control Alert:**\n\n" +
                                       $"You are logged in as **{(currentStudent?.FullName ?? "Guest")}**. Due to academic privacy policies, students are only authorized to view their own personal records. Querying private enrollment or grade data of other students is restricted to Academic Affairs Administrators.";
                }
                else
                {
                    advice.AiMessage = $"🔒 **Thông Báo Bảo Mật Thông Tin Học Vụ:**\n\n" +
                                       $"Bạn hiện đang đăng nhập với tư cách **{(currentStudent != null ? $"{currentStudent.FullName} (`{currentStudent.StudentCode}`)" : "Khách")}**. Nhằm bảo vệ quyền riêng tư học bạ và kết quả học tập cá nhân, sinh viên chỉ được phép tra cứu dữ liệu của chính mình. Quyền tra cứu dữ liệu sinh viên khác chỉ dành cho Cán bộ Phòng Đào Tạo (Admin).";
                }
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 1: ĐỐI CHIẾU SINH VIÊN VỚI GIẢNG VIÊN (STUDENT + INSTRUCTOR)
            // Ví dụ: "Sinh viên Nguyễn Văn Tuấn có đăng ký môn nào của thầy Hà Ngọc Long không?"
            // Hoặc: "Tôi có học môn nào của thầy Hà Ngọc Long không?"
            // Hoặc: "Does Nguyen Van Tuan take any course taught by Ha Ngoc Long?"
            // =========================================================================
            bool isCrossStudentInstructorQuery =
                (targetStudent != null || hasPersonalPronoun || qNorm.Contains("co dang ky") || qNorm.Contains("co hoc") || qNorm.Contains("registered with") || qNorm.Contains("enrolled in") || qNorm.Contains("classes with") || qNorm.Contains("lop cua thay") || qNorm.Contains("mon cua thay")) &&
                !string.IsNullOrEmpty(matchedInstructorName) &&
                (qNorm.Contains("co dang ky") || qNorm.Contains("co hoc") || qNorm.Contains("dang ky mon") || qNorm.Contains("hoc mon") || qNorm.Contains("lop nao") || qNorm.Contains("mon nao") || qNorm.Contains("enrolled") || qNorm.Contains("registered") || qNorm.Contains("taking") || qNorm.Contains("any class") || qNorm.Contains("any course"));

            if (isCrossStudentInstructorQuery)
            {
                // Nếu người hỏi là Khách chưa đăng nhập mà dùng đại từ cá nhân
                if (targetStudent == null)
                {
                    advice.AiMessage = isEnglish
                        ? "👋 You are currently in Guest mode. Please click **Log in** to view your personalized enrollment matching."
                        : "👋 Bạn hiện đang ở chế độ Khách (chưa đăng nhập). Vui lòng bấm nút **Đăng nhập** bên dưới để Cố vấn AI đối chiếu danh sách môn học của bạn nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                // Lấy các lớp sinh viên đã đăng ký trong học kỳ
                var studentRegisteredSections = targetStudent.StudentId == currentStudent?.StudentId
                    ? registeredSections
                    : GetRegisteredSectionsForStudent(targetStudent.StudentId, semester, enrollmentRepository);

                // Lọc những lớp do đúng giảng viên này phụ trách
                var matchedCrossSections = studentRegisteredSections
                    .Where(s => IsInstructorMatch(s.Instructor, matchedInstructorName))
                    .ToList();

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 👨‍🏫 Lecturer & Student Course Enrollment Cross-Check ({semester}):");
                    sb.AppendLine($"- Student: **{targetStudent.FullName}** (ID: `{targetStudent.StudentCode}` - {targetStudent.Major})");
                    sb.AppendLine($"- Lecturer: **{matchedInstructorName}**");
                    sb.AppendLine();

                    if (matchedCrossSections.Any())
                    {
                        sb.AppendLine($"✅ Student **{targetStudent.FullName}** is currently enrolled in **{matchedCrossSections.Count} class(es)** taught by **{matchedInstructorName}**:");
                        sb.AppendLine();

                        foreach (var s in matchedCrossSections)
                        {
                            sb.AppendLine($"- **{s.Course?.CourseName}** (Class Code: `{s.SectionCode}` — **{s.Course?.Credits} Credits**)");
                            sb.AppendLine($"  + **Schedule:** {FormatScheduleDetailed(s, true)}");
                            sb.AppendLine($"  + **Room:** {s.Room ?? "TBD"}");
                            sb.AppendLine($"  + **Status:** Confirmed registration");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"❌ Student **{targetStudent.FullName}** has **NOT registered for any courses/classes** taught by **{matchedInstructorName}** in semester {semester}.");
                        if (studentRegisteredSections.Any())
                        {
                            sb.AppendLine();
                            sb.AppendLine($"💡 The student is currently taking {studentRegisteredSections.Count} other class(es): {string.Join(", ", studentRegisteredSections.Select(s => $"`{s.SectionCode}` ({s.Course?.CourseName} - {s.Instructor})"))}.");
                        }
                    }
                }
                else
                {
                    sb.AppendLine($"### 👨‍🏫 Kết Quả Đối Chiếu Học Phần Sinh Viên & Giảng Viên (Học kỳ {semester}):");
                    sb.AppendLine($"- Sinh viên: **{targetStudent.FullName}** (MSSV: `{targetStudent.StudentCode}` — Ngành {targetStudent.Major})");
                    sb.AppendLine($"- Giảng viên đối chiếu: **{matchedInstructorName}**");
                    sb.AppendLine();

                    if (matchedCrossSections.Any())
                    {
                        sb.AppendLine($"✅ Sinh viên **{targetStudent.FullName}** hiện **CÓ ĐĂNG KÝ {matchedCrossSections.Count} lớp học phần** do giảng viên **{matchedInstructorName}** phụ trách:");
                        sb.AppendLine();

                        foreach (var s in matchedCrossSections)
                        {
                            sb.AppendLine($"- **{s.Course?.CourseName}** (Mã lớp: `{s.SectionCode}` — **{s.Course?.Credits} Tín chỉ**):");
                            sb.AppendLine($"  + **Thời khóa biểu:** **{FormatScheduleDetailed(s, false)}**");
                            sb.AppendLine($"  + **Phòng học:** **{s.Room ?? "Chưa xếp phòng"}**");
                            sb.AppendLine($"  + **Trạng thái phiếu:** Đã xác nhận ghi danh hợp lệ (`Confirmed`)");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"❌ Sinh viên **{targetStudent.FullName}** **KHÔNG ĐĂNG KÝ môn học/lớp học phần nào** do **{matchedInstructorName}** phụ trách trong học kỳ {semester}.");
                        if (studentRegisteredSections.Any())
                        {
                            sb.AppendLine();
                            sb.AppendLine($"💡 Hiện tại sinh viên đang theo học {studentRegisteredSections.Count} lớp khác: {string.Join(", ", studentRegisteredSections.Select(s => $"`{s.SectionCode}` ({s.Course?.CourseName} - GV {s.Instructor})"))}.");
                        }
                    }
                }

                advice.AiMessage = sb.ToString();
                return; // Đã trả lời đúng trọng tâm câu hỏi đối chiếu, KHÔNG tự ý đề xuất môn!
            }

            // =========================================================================
            // TRƯỜNG HỢP 2: CÂU HỎI TIẾP NỐI THEO NGỮ CẢNH (FOLLOW-UP QUESTIONS)
            // Ví dụ:
            // Lượt 1: "Tôi đã đăng ký môn nào?"
            // Lượt 2: "Môn nào có lịch ngày mai?"
            // Lượt 3: "Ai dạy môn đó?"
            // =========================================================================
            bool isFollowUpQuery = IsFollowUpQuestion(qNorm, qLower);

            if (isFollowUpQuery)
            {
                if (context.HasContext)
                {
                    // Ca 2.1: Hỏi môn nào có lịch ngày mai / thứ cụ thể trong số các môn đã nói trước
                    if (queriedDay.HasValue && (context.LastMentionedSections.Any() || registeredSections.Any()))
                    {
                        int day = queriedDay.Value;
                        string dayNameVi = day == 8 ? "Chủ Nhật" : $"Thứ {day}";
                        string dayNameEn = GetDayNameEnglish(day);

                        var poolToCheck = context.LastMentionedSections.Any()
                            ? context.LastMentionedSections
                            : registeredSections;

                        var daySections = poolToCheck.Where(s =>
                            s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == day)).ToList();

                        var sb = new StringBuilder();
                        if (isEnglish)
                        {
                            sb.AppendLine($"### 🗓️ Follow-up: Class Schedule for **{dayNameEn}**:");
                            if (daySections.Any())
                            {
                                sb.AppendLine($"Among your enrolled courses, you have **{daySections.Count} class(es)** scheduled for **{dayNameEn}**:");
                                sb.AppendLine();
                                foreach (var s in daySections)
                                {
                                    var schs = s.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                                    string periods = string.Join(", ", schs.Select(sch => $"Periods {sch.StartPeriod}-{sch.EndPeriod} (Room: {sch.Room})"));
                                    sb.AppendLine($"- **{s.Course?.CourseName}** (`{s.SectionCode}` - {s.Course?.Credits} Credits):");
                                    sb.AppendLine($"  + **Periods & Room:** {periods}");
                                    sb.AppendLine($"  + **Instructor:** **{s.Instructor}**");
                                }
                            }
                            else
                            {
                                sb.AppendLine($"✅ Among your enrolled courses, you have **no scheduled classes on {dayNameEn}**.");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"### 🗓️ Lịch Học Ngày **{dayNameVi}** Trong Số Các Học Phần Của Bạn:");
                            if (daySections.Any())
                            {
                                sb.AppendLine($"Trong số các môn bạn đã đăng ký, vào **{dayNameVi}** bạn có **{daySections.Count} lớp học phần** diễn ra:");
                                sb.AppendLine();
                                foreach (var s in daySections)
                                {
                                    var schs = s.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                                    string periods = string.Join(", ", schs.Select(sch => $"Tiết {sch.StartPeriod} - {sch.EndPeriod} (Phòng {sch.Room})"));
                                    sb.AppendLine($"- **{s.Course?.CourseName}** (Mã lớp: `{s.SectionCode}` - **{s.Course?.Credits} Tín chỉ**):");
                                    sb.AppendLine($"  + **Ca học:** **{periods}**");
                                    sb.AppendLine($"  + **Giảng viên:** **{s.Instructor}**");
                                }
                            }
                            else
                            {
                                sb.AppendLine($"✅ Trong số các môn bạn đã đăng ký, vào **{dayNameVi}** bạn **không có môn nào có lịch học**.");
                            }
                        }

                        advice.AiMessage = sb.ToString();
                        return;
                    }

                    // Ca 2.2: "Ai dạy môn đó?", "Who teaches it?"
                    bool asksWhoTeaches = qNorm.Contains("ai day") || qNorm.Contains("thay nao") || qNorm.Contains("co nao") ||
                                          qLower.Contains("who teaches") || qLower.Contains("who is the teacher") || qLower.Contains("who is the instructor");

                    if (asksWhoTeaches && (context.LastReferencedSection != null || context.LastReferencedCourse != null || context.LastMentionedSections.Any()))
                    {
                        var targetSec = context.LastReferencedSection
                                        ?? (context.LastMentionedSections.Count == 1 ? context.LastMentionedSections.First() : null);

                        if (targetSec != null)
                        {
                            advice.AiMessage = isEnglish
                                ? $"### 👨‍🏫 Instructor Information for `{targetSec.SectionCode}`:\n\n" +
                                  $"- Course: **{targetSec.Course?.CourseName}** (`{targetSec.Course?.CourseCode}` - {targetSec.Course?.Credits} Credits)\n" +
                                  $"- Instructor: **{targetSec.Instructor}**\n" +
                                  $"- Schedule: **{FormatScheduleDetailed(targetSec, true)}**\n" +
                                  $"- Room: **{targetSec.Room ?? "TBD"}**"
                                : $"### 👨‍🏫 Thông Tin Giảng Viên Lớp `{targetSec.SectionCode}`:\n\n" +
                                  $"- Môn học: **{targetSec.Course?.CourseName}** (`{targetSec.Course?.CourseCode}` — **{targetSec.Course?.Credits} Tín chỉ**)\n" +
                                  $"- Giảng viên phụ trách: **{targetSec.Instructor}**\n" +
                                  $"- Thời khóa biểu: **{FormatScheduleDetailed(targetSec, false)}**\n" +
                                  $"- Phòng học: **{targetSec.Room ?? "Chưa xếp phòng"}**";
                            return;
                        }
                        else if (context.LastMentionedSections.Count > 1)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine(isEnglish
                                ? "### 👨‍🏫 Instructors of the previously mentioned courses:"
                                : "### 👨‍🏫 Giảng Viên Phụ Trách Các Môn Vừa Đề Cập:");
                            sb.AppendLine();
                            foreach (var s in context.LastMentionedSections)
                            {
                                sb.AppendLine($"- **{s.Course?.CourseName}** (`{s.SectionCode}`): Giảng viên **{s.Instructor}** (Phòng: {s.Room})");
                            }
                            advice.AiMessage = sb.ToString();
                            return;
                        }
                    }

                    // Ca 2.3: "Cái đó học lúc mấy giờ?", "Môn đó học ca mấy?", "What time does that class start?"
                    bool asksTimeOfClass = qNorm.Contains("luc may gio") || qNorm.Contains("may gio") || qNorm.Contains("ca may") ||
                                           qNorm.Contains("tiet may") || qLower.Contains("what time") || qLower.Contains("when is it") || qLower.Contains("which period");

                    if (asksTimeOfClass && (context.LastReferencedSection != null || context.LastReferencedCourse != null || context.LastMentionedSections.Any()))
                    {
                        var targetSec = context.LastReferencedSection
                                        ?? (context.LastMentionedSections.Count == 1 ? context.LastMentionedSections.First() : null);

                        if (targetSec != null)
                        {
                            advice.AiMessage = isEnglish
                                ? $"### ⏰ Timetable for `{targetSec.SectionCode}` ({targetSec.Course?.CourseName}):\n\n" +
                                  $"- Schedule: **{FormatScheduleDetailed(targetSec, true)}**\n" +
                                  $"- Classroom: **{targetSec.Room ?? "TBD"}**\n" +
                                  $"- Lecturer: **{targetSec.Instructor}**"
                                : $"### ⏰ Thời Gian Học Của Lớp `{targetSec.SectionCode}` ({targetSec.Course?.CourseName}):\n\n" +
                                  $"- Thời khóa biểu: **{FormatScheduleDetailed(targetSec, false)}**\n" +
                                  $"- Phòng học: **{targetSec.Room ?? "Chưa xếp phòng"}**\n" +
                                  $"- Giảng viên: **{targetSec.Instructor}**";
                            return;
                        }
                        else if (context.LastMentionedSections.Count > 1)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine(isEnglish
                                ? "### ⏰ Timetable for the previously mentioned courses:"
                                : "### ⏰ Thời Khóa Biểu Các Môn Vừa Đề Cập:");
                            sb.AppendLine();
                            foreach (var s in context.LastMentionedSections)
                            {
                                sb.AppendLine($"- **{s.Course?.CourseName}** (`{s.SectionCode}`): **{FormatScheduleDetailed(s, isEnglish)}** (Phòng: {s.Room})");
                            }
                            advice.AiMessage = sb.ToString();
                            return;
                        }
                    }

                    // Ca 2.4: "Học ở đâu?", "Phòng nào vậy?", "Which room?"
                    bool asksRoomOfClass = qNorm.Contains("o dau") || qNorm.Contains("phong nao") || qLower.Contains("which room") || qLower.Contains("where is it");
                    if (asksRoomOfClass && (context.LastReferencedSection != null || context.LastReferencedCourse != null || context.LastMentionedSections.Any()))
                    {
                        var targetSec = context.LastReferencedSection
                                        ?? (context.LastMentionedSections.Count == 1 ? context.LastMentionedSections.First() : null);

                        if (targetSec != null)
                        {
                            advice.AiMessage = isEnglish
                                ? $"### 🏫 Room Information for `{targetSec.SectionCode}` ({targetSec.Course?.CourseName}):\n\n- Room: **{targetSec.Room ?? "TBD"}**\n- Schedule: **{FormatScheduleDetailed(targetSec, true)}**\n- Lecturer: **{targetSec.Instructor}**"
                                : $"### 🏫 Phòng Học Của Lớp `{targetSec.SectionCode}` ({targetSec.Course?.CourseName}):\n\n- Phòng học: **{targetSec.Room ?? "Chưa xếp phòng"}**\n- Lịch học: **{FormatScheduleDetailed(targetSec, false)}**\n- Giảng viên: **{targetSec.Instructor}**";
                            return;
                        }
                    }
                }
                else
                {
                    // KHÔNG CÓ NGỮ CẢNH TRƯỚC ĐÓ:
                    // Hỏi lại phần thông tin còn thiếu một cách tự nhiên, TUYỆT ĐỐI KHÔNG dump danh sách câu hỏi mẫu SQL!
                    bool asksTime = qNorm.Contains("luc may gio") || qNorm.Contains("may gio") || qNorm.Contains("ca may") ||
                                    qNorm.Contains("tiet may") || qLower.Contains("what time") || qLower.Contains("when is it");
                    bool asksInstructor = qNorm.Contains("ai day") || qNorm.Contains("thay nao") || qNorm.Contains("co nao") ||
                                          qLower.Contains("who teaches");
                    bool asksRoom = qNorm.Contains("phong nao") || qNorm.Contains("o dau") || qLower.Contains("which room") || qLower.Contains("where");

                    if (asksTime)
                    {
                        advice.AiMessage = isEnglish
                            ? "Which course or class are you asking the schedule for? Please specify the course name (e.g., *Web Programming*) or class code (e.g., `WEB301_01`) so I can check the exact timetable for you!"
                            : "Bạn đang muốn hỏi thời gian học của môn học hoặc lớp học phần cụ thể nào? Vui lòng cho tôi biết tên môn (ví dụ: *Lập trình Web*) hoặc mã lớp (ví dụ: `WEB301_01`) để tôi kiểm tra thời khóa biểu chính xác cho bạn nhé!";
                    }
                    else if (asksInstructor)
                    {
                        advice.AiMessage = isEnglish
                            ? "Which course or class are you asking about? Please provide the course name or class code so I can tell you who teaches it!"
                            : "Bạn đang muốn hỏi giảng viên phụ trách của môn học hoặc lớp học phần nào? Vui lòng cho tôi biết tên môn để tôi kiểm tra thầy/cô phụ trách nhé!";
                    }
                    else if (asksRoom)
                    {
                        advice.AiMessage = isEnglish
                            ? "Which class are you asking the room for? Please provide the class code or course name so I can check the classroom for you!"
                            : "Bạn đang muốn hỏi phòng học của lớp học phần nào? Vui lòng cho tôi biết mã lớp hoặc tên môn để tôi kiểm tra phòng học chính xác nhé!";
                    }
                    else
                    {
                        advice.AiMessage = isEnglish
                            ? $"I did not find previous context for your question: *\"{qRaw}\"*. Could you please specify the course name or class code you are referring to?"
                            : $"Tôi chưa rõ đối tượng bạn đang đề cập trong câu: *\"{qRaw}\"*. Bạn có thể nói rõ tên môn học hoặc mã lớp học phần cụ thể để tôi hỗ trợ tra cứu không?";
                    }
                    return;
                }
            }

            // =========================================================================
            // TRƯỜNG HỢP 3: LỊCH HỌC / THỜI KHÓA BIỂU CÁ NHÂN CỦA SINH VIÊN
            // Ví dụ: "Ngày mai tôi học gì?", "Thứ 2 tôi học gì?", "Do I have any classes tomorrow?"
            // =========================================================================
            bool isPersonalSchedule =
                (hasPersonalPronoun || qNorm.Contains("lich cua toi") || qNorm.Contains("thoi khoa bieu") || qNorm.Contains("my schedule") || qNorm.Contains("my classes") || (queriedDay.HasValue && hasPersonalPronoun)) &&
                !qNorm.Contains("diem") && !qNorm.Contains("hoc ba") && !qNorm.Contains("goi y") && !qNorm.Contains("tu van") && !qNorm.Contains("recommend");

            if (isPersonalSchedule)
            {
                if (currentStudent == null)
                {
                    advice.AiMessage = isEnglish
                        ? "👋 **Hello! You are currently in Guest mode (not logged in).**\n\nTo view your personalized schedule and enrolled courses, please log in with your student account."
                        : "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\nĐể Cố vấn AI tra cứu thời khóa biểu cá nhân của bạn, vui lòng bấm nút **Đăng nhập** bên dưới nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                // Sub-case 3.1: Hỏi về một ngày cụ thể (Hôm nay / Ngày mai / Thứ 2 - CN)
                if (queriedDay.HasValue)
                {
                    int day = queriedDay.Value;
                    string dayNameVi = day == 8 ? "Chủ Nhật" : $"Thứ {day}";
                    string dayNameEn = GetDayNameEnglish(day);

                    var daySections = registeredSections.Where(s =>
                        s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == day)).ToList();

                    var sb = new StringBuilder();
                    if (isEnglish)
                    {
                        sb.AppendLine($"### 🗓️ Personal Class Schedule for {dayNameEn} (Semester {semester}):");
                        sb.AppendLine($"Student: **{currentStudent.FullName}** (ID: `{currentStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (daySections.Any())
                        {
                            sb.AppendLine($"On **{dayNameEn}**, you have **{daySections.Count} registered class(es)**:");
                            sb.AppendLine();

                            foreach (var s in daySections)
                            {
                                var schs = s.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                                string periods = string.Join(", ", schs.Select(sch => $"Periods {sch.StartPeriod} - {sch.EndPeriod} (Room: {sch.Room})"));

                                sb.AppendLine($"- **{s.Course?.CourseName}** (Class: `{s.SectionCode}` — **{s.Course?.Credits} Credits**):");
                                sb.AppendLine($"  + Session: **{periods}**");
                                sb.AppendLine($"  + Instructor: **{s.Instructor}**");
                                sb.AppendLine($"  + Room: **{s.Room}**");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"✅ On **{dayNameEn}**, you have **no scheduled classes** among your registered courses.");
                            sb.AppendLine($"💡 You can use this time for self-study or review!");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"### 🗓️ Lịch Học Cá Nhân Vào {dayNameVi} (Học kỳ {semester}):");
                        sb.AppendLine($"Sinh viên: **{currentStudent.FullName}** (MSSV: `{currentStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (daySections.Any())
                        {
                            sb.AppendLine($"Vào **{dayNameVi}**, bạn có **{daySections.Count} lớp học phần** đã đăng ký:");
                            sb.AppendLine();

                            foreach (var s in daySections)
                            {
                                var schs = s.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                                string periods = string.Join(", ", schs.Select(sch => $"Tiết {sch.StartPeriod} - {sch.EndPeriod} (Phòng: {sch.Room})"));

                                sb.AppendLine($"- **{s.Course?.CourseName}** (Mã lớp: `{s.SectionCode}` — **{s.Course?.Credits} Tín chỉ**):");
                                sb.AppendLine($"  + Ca học: **{periods}**");
                                sb.AppendLine($"  + Giảng viên: **{s.Instructor}**");
                                sb.AppendLine($"  + Phòng học: **{s.Room}**");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"✅ Vào **{dayNameVi}**, bạn **không có lịch học nào** trong số các lớp học phần đã đăng ký.");
                            sb.AppendLine($"💡 Bạn có thể dùng thời gian này để tự học, ôn bài hoặc nghỉ ngơi!");
                        }
                    }

                    advice.AiMessage = sb.ToString();
                    return; // KHÔNG tự ý đề xuất môn
                }

                // Sub-case 3.2: Hỏi lịch học cả tuần (This week / Tuần này)
                if (isWeekQuery)
                {
                    var sb = new StringBuilder();
                    if (isEnglish)
                    {
                        sb.AppendLine($"### 🗓️ Weekly Schedule for Student {currentStudent.FullName} (`{currentStudent.StudentCode}`) — Semester {semester}:");
                        sb.AppendLine();
                        for (int d = 2; d <= 8; d++)
                        {
                            string dName = GetDayNameEnglish(d);
                            var dSecs = registeredSections.Where(s => s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == d)).ToList();
                            if (dSecs.Any())
                            {
                                sb.AppendLine($"**{dName}:**");
                                foreach (var s in dSecs)
                                {
                                    var sch = s.Schedules!.First(x => x.DayOfWeek == d);
                                    sb.AppendLine($"- `{s.SectionCode}`: **{s.Course?.CourseName}** (Periods {sch.StartPeriod}-{sch.EndPeriod}, Room {s.Room}, GV {s.Instructor})");
                                }
                            }
                            else
                            {
                                sb.AppendLine($"**{dName}:** *No classes*");
                            }
                        }
                    }
                    else
                    {
                        sb.AppendLine($"### 🗓️ Thời Khóa Biểu Tuần Của Sinh Viên: **{currentStudent.FullName}** (`{currentStudent.StudentCode}`) — Học kỳ {semester}:");
                        sb.AppendLine();
                        for (int d = 2; d <= 8; d++)
                        {
                            string dName = d == 8 ? "Chủ Nhật" : $"Thứ {d}";
                            var dSecs = registeredSections.Where(s => s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == d)).ToList();
                            if (dSecs.Any())
                            {
                                sb.AppendLine($"📅 **{dName}:**");
                                foreach (var s in dSecs)
                                {
                                    var sch = s.Schedules!.First(x => x.DayOfWeek == d);
                                    sb.AppendLine($"- `{s.SectionCode}` — **{s.Course?.CourseName}** (Tiết {sch.StartPeriod}-{sch.EndPeriod}, Phòng {s.Room}, GV {s.Instructor})");
                                }
                            }
                            else
                            {
                                sb.AppendLine($"📅 **{dName}:** *Không có lịch học*");
                            }
                        }
                    }
                    advice.AiMessage = sb.ToString();
                    return;
                }

                // Sub-case 3.3: Hỏi toàn bộ thời khóa biểu / danh sách môn đã đăng ký
                {
                    var sb = new StringBuilder();
                    if (isEnglish)
                    {
                        sb.AppendLine($"### 📋 Your Registered Courses for Semester {semester}:");
                        sb.AppendLine($"Student: **{currentStudent.FullName}** (ID: `{currentStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (registeredSections.Any())
                        {
                            int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                            sb.AppendLine($"You have successfully enrolled in **{registeredSections.Count} classes** (Total: **{totalRegCredits} Credits**):");
                            sb.AppendLine();

                            foreach (var s in registeredSections)
                            {
                                sb.AppendLine($"- **Class `{s.SectionCode}`** — **{s.Course?.CourseName}** (`{s.Course?.CourseCode}` - **{s.Course?.Credits} Credits**):");
                                sb.AppendLine($"  + Instructor: **{s.Instructor}** | Room: **{s.Room}**");
                                sb.AppendLine($"  + Schedule: **{FormatScheduleDetailed(s, true)}**");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"⚠️ You have not registered for any classes in semester **{semester}**.");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"### 📋 Danh Sách Học Phần Đã Đăng Ký Học Kỳ {semester}:");
                        sb.AppendLine($"Sinh viên: **{currentStudent.FullName}** (MSSV: `{currentStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (registeredSections.Any())
                        {
                            int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                            sb.AppendLine($"Bạn đã đăng ký thành công **{registeredSections.Count} lớp học phần** (Tổng số: **{totalRegCredits} Tín chỉ**):");
                            sb.AppendLine();

                            foreach (var s in registeredSections)
                            {
                                sb.AppendLine($"- **Lớp `{s.SectionCode}`** — **{s.Course?.CourseName}** (`{s.Course?.CourseCode}` - **{s.Course?.Credits} TC**):");
                                sb.AppendLine($"  + Giảng viên: **{s.Instructor}** | Phòng: **{s.Room}**");
                                sb.AppendLine($"  + Thời khóa biểu: **{FormatScheduleDetailed(s, false)}**");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"⚠️ Bạn **chưa đăng ký lớp học phần nào** trong học kỳ **{semester}**.");
                            sb.AppendLine($"💡 Bạn có thể vào mục **Đăng Ký Học Phần** hoặc hỏi tôi: *\"Gợi ý môn học kỳ này\"* để nhận đề xuất lộ trình tối ưu.");
                        }
                    }

                    advice.AiMessage = sb.ToString();
                    return; // KHÔNG tự ý đề xuất môn
                }
            }

            // =========================================================================
            // TRƯỜNG HỢP 4: HỌC BẠ / ĐIỂM SỐ / ĐIỀU KIỆN TIÊN QUYẾT CỦA SINH VIÊN
            // =========================================================================
            bool asksGrades = qNorm.Contains("diem cua em") || qNorm.Contains("diem cua toi") || qNorm.Contains("hoc ba") ||
                              qNorm.Contains("mon da hoc") || qNorm.Contains("mon da qua") || qNorm.Contains("mon tich luy") ||
                              qNorm.Contains("ket qua hoc tap") || qNorm.Contains("diem f") || qNorm.Contains("mon rot") ||
                              qLower.Contains("my grade") || qLower.Contains("transcript") || qLower.Contains("completed course");

            if (asksGrades)
            {
                if (currentStudent == null)
                {
                    advice.AiMessage = isEnglish
                        ? "👋 Please log in with your student account to view your academic transcripts and completed courses."
                        : "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\nĐể xem học bạ, điểm số tích lũy cá nhân, vui lòng bấm nút **Đăng nhập** nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 📊 Academic Record & Transcripts: {currentStudent.FullName} (`{currentStudent.StudentCode}`)");
                    sb.AppendLine($"- **Major:** {currentStudent.Major} | **Cohort:** {currentStudent.AcademicYear}");
                    sb.AppendLine($"- **Max credits allowance:** **{currentStudent.MaxCreditsPerSemester} Credits/semester**");
                    sb.AppendLine();

                    if (completedCourses.Any())
                    {
                        sb.AppendLine($"You have completed **{completedCourses.Count} course(s)**:");
                        sb.AppendLine();
                        foreach (var c in completedCourses)
                        {
                            string status = c.Grade >= 5.0 ? "✅ Passed" : "❌ Failed";
                            sb.AppendLine($"- **{c.Course?.CourseCode}** — {c.Course?.CourseName} ({c.Course?.Credits}TC): **Grade {c.Grade:0.0}** ({status} - Semester {c.Semester})");
                        }
                    }
                    else
                    {
                        sb.AppendLine("- *No completed course records found in system.*");
                    }
                }
                else
                {
                    sb.AppendLine($"### 📊 Kết Quả Tích Lũy & Học Bạ Của Sinh Viên: {currentStudent.FullName} (`{currentStudent.StudentCode}`)");
                    sb.AppendLine($"- **Chuyên ngành:** {currentStudent.Major} | **Khóa học:** {currentStudent.AcademicYear}");
                    sb.AppendLine($"- **Hạn mức số tín chỉ tối đa:** **{currentStudent.MaxCreditsPerSemester} Tín chỉ / kỳ**");
                    sb.AppendLine();

                    if (completedCourses.Any())
                    {
                        sb.AppendLine($"Bạn đã hoàn thành **{completedCourses.Count} học phần** nền tảng:");
                        sb.AppendLine();
                        foreach (var c in completedCourses)
                        {
                            string status = c.Grade >= 5.0 ? "✅ Đạt" : "❌ Chưa đạt";
                            sb.AppendLine($"- **{c.Course?.CourseCode}** — {c.Course?.CourseName} ({c.Course?.Credits}TC): **{c.Grade:0.0} điểm** ({status} - Kỳ {c.Semester})");
                        }
                    }
                    else
                    {
                        sb.AppendLine("- *Bạn chưa có dữ liệu môn học đã hoàn thành trong hệ thống.*");
                    }
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 5: YÊU CẦU GỢI Ý / TƯ VẤN LỘ TRÌNH ĐĂNG KÝ HỌC PHẦN (CHỈ KHI YÊU CẦU)
            // =========================================================================
            bool isAdvisoryRequest = IsExplicitRecommendationRequest(qNorm);

            if (isAdvisoryRequest)
            {
                if (currentStudent == null)
                {
                    advice.AiMessage = isEnglish
                        ? "👋 Please log in with your student account so the AI Advisor can analyze your transcript, check prerequisites, and recommend an optimal course plan for you!"
                        : "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\nĐể Cố vấn AI có thể phân tích học bạ, kiểm tra điều kiện tiên quyết và đề xuất lộ trình môn học chính xác cho riêng bạn, vui lòng bấm nút **Đăng nhập** nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                GenerateCourseRecommendations(
                    advice,
                    currentStudent,
                    candidateSections,
                    remainingCreditsAllowed,
                    qNorm,
                    semester,
                    isEnglish);
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 6: HỌC PHÍ & VIETQR
            // =========================================================================
            bool isTuition = qNorm.Contains("hoc phi") || qNorm.Contains("bao nhieu tien") || qNorm.Contains("dong tien") ||
                             qNorm.Contains("nop tien") || qNorm.Contains("ngan hang") || qNorm.Contains("stk") ||
                             qNorm.Contains("so tai khoan") || qNorm.Contains("vietqr") || qNorm.Contains("mb bank") ||
                             qNorm.Contains("chuyen khoan") || qLower.Contains("tuition") || qLower.Contains("fee") ||
                             qLower.Contains("payment") || qLower.Contains("how much is");

            if (isTuition)
            {
                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine("### 💰 Tuition Fee Regulations & Online VietQR Payment 2026 - 2027:");
                    sb.AppendLine();
                    sb.AppendLine("- **Credit unit price:** **450,000 VNĐ / credit**.");
                    sb.AppendLine("- **Examples:**");
                    sb.AppendLine("  + 2-credit course: **900,000 VNĐ**");
                    sb.AppendLine("  + 3-credit course: **1,350,000 VNĐ**");
                    sb.AppendLine("  + 4-credit course: **1,800,000 VNĐ**");
                    sb.AppendLine("  + Standard semester (14 credits): **6,300,000 VNĐ**");
                    sb.AppendLine();

                    if (currentStudent != null && registeredSections.Any())
                    {
                        int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                        long totalTuition = (long)totalRegCredits * 450000;
                        sb.AppendLine($"💳 **Your personalized tuition ({currentStudent.FullName} - {currentStudent.StudentCode}):**");
                        sb.AppendLine($"- Registered credits: **{totalRegCredits} Credits**");
                        sb.AppendLine($"- Total semester tuition: **{totalTuition:N0} VNĐ**");
                        sb.AppendLine();
                    }

                    sb.AppendLine("- **University bank account details:**");
                    sb.AppendLine("  + Account number: **101705201967**");
                    sb.AppendLine("  + Bank: **MB Bank (Military Commercial Joint Stock Bank)**");
                    sb.AppendLine("  + Beneficiary: **TRUONG DAI HOC KINH TE HUE**");
                    sb.AppendLine("- **VietQR Online Payment:** System automatically creates dynamic VietQR code with content `[MSSV] HP 2026_HK1`, real-time 24/7 reconciliation.");
                }
                else
                {
                    sb.AppendLine("### 💰 Quy Định Mức Thu Học Phí & Thanh Toán Năm Học 2026 - 2027:");
                    sb.AppendLine();
                    sb.AppendLine("- **Đơn giá tín chỉ:** **450.000 VNĐ / tín chỉ**.");
                    sb.AppendLine("- **Ví dụ cụ thể:**");
                    sb.AppendLine("  + Môn 2 tín chỉ: **900.000 VNĐ**");
                    sb.AppendLine("  + Môn 3 tín chỉ: **1.350.000 VNĐ**");
                    sb.AppendLine("  + Môn 4 tín chỉ: **1.800.000 VNĐ**");
                    sb.AppendLine("  + Học kỳ tiêu chuẩn (14 tín chỉ): **6.300.000 VNĐ**");
                    sb.AppendLine();

                    if (currentStudent != null && registeredSections.Any())
                    {
                        int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                        long totalTuition = (long)totalRegCredits * 450000;
                        sb.AppendLine($"💳 **Học phí cá nhân của bạn ({currentStudent.FullName} - {currentStudent.StudentCode}):**");
                        sb.AppendLine($"- Tổng số tín chỉ đã đăng ký: **{totalRegCredits} Tín chỉ**");
                        sb.AppendLine($"- Tổng học phí học kỳ {semester}: **{totalTuition:N0} VNĐ**");
                        sb.AppendLine();
                    }

                    sb.AppendLine("- **Thông tin tài khoản thu học phí nhà trường:**");
                    sb.AppendLine("  + Số tài khoản: **101705201967**");
                    sb.AppendLine("  + Ngân hàng: **MB Bank (Ngân hàng TMCP Quân Đội)**");
                    sb.AppendLine("  + Đơn vị thụ hưởng: **TRƯỜNG ĐẠI HỌC KINH TẾ HUẾ**");
                    sb.AppendLine();
                    sb.AppendLine("- **Thanh toán thông minh qua VietQR:** Hệ thống tự động tạo mã VietQR Napas247 điền sẵn số tiền và nội dung `[MSSV] HP 2026_HK1`, gạch nợ tức thì 24/7.");
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 7: THỜI GIAN MỞ / ĐÓNG CỔNG ĐĂNG KÝ HỌC PHẦN
            // =========================================================================
            bool isPeriodQuery = qNorm.Contains("mo cong") || qNorm.Contains("dong cong") || qNorm.Contains("thoi gian mo") ||
                                 qNorm.Contains("han dang ky") || qNorm.Contains("khi nao mo") || qNorm.Contains("khi nao dong") ||
                                 qNorm.Contains("dot dang ky") || qLower.Contains("registration period") || qLower.Contains("registration open") ||
                                 qLower.Contains("deadline");

            if (isPeriodQuery)
            {
                bool isOpen = registrationPeriodService?.IsRegistrationOpen(semester) ?? true;
                var activePhase = registrationPeriodService?.GetActivePhase(semester);

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 📅 Course Registration Period Status ({semester}):\n");
                    sb.AppendLine($"- **Status:** {(isOpen ? "🟢 OPEN FOR REGISTRATION" : "🔴 CLOSED")}");
                    if (activePhase != null)
                    {
                        sb.AppendLine($"- **Active Phase:** {activePhase.PhaseName}");
                        sb.AppendLine($"- **Timeframe:** {activePhase.StartDate:dd/MM/yyyy} to {activePhase.EndDate:dd/MM/yyyy}");
                        sb.AppendLine($"- **Description:** {activePhase.Description}");
                    }
                    else
                    {
                        sb.AppendLine($"- **Schedule:** Semester {semester} is currently open for official registration.");
                    }
                }
                else
                {
                    sb.AppendLine($"### 📅 Tình Trạng Thời Gian Mở Cổng Đăng Ký Học Phần ({semester}):\n");
                    sb.AppendLine($"- **Trạng thái cổng:** {(isOpen ? "🟢 CỔNG ĐĂNG KÝ ĐANG MỞ" : "🔴 CỔNG ĐĂNG KÝ ĐÃ ĐÓNG")}");
                    if (activePhase != null)
                    {
                        sb.AppendLine($"- **Giai đoạn hiện tại:** **{activePhase.PhaseName}**");
                        sb.AppendLine($"- **Thời gian:** Từ ngày **{activePhase.StartDate:dd/MM/yyyy}** đến ngày **{activePhase.EndDate:dd/MM/yyyy}**");
                        sb.AppendLine($"- **Chi tiết:** {activePhase.Description}");
                    }
                    else
                    {
                        sb.AppendLine($"- **Thời gian:** Học kỳ {semester} hiện đang trong thời gian tiếp nhận phiếu đăng ký trực tuyến.");
                    }
                    sb.AppendLine($"- **Lưu ý:** Sau khi đóng cổng trực tuyến, mọi yêu cầu rút môn hoặc bổ sung học phần phải nộp đơn về **Phòng Đào Tạo**.");
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 8: QUY CHẾ RÚT MÔN & HẠN MỨC TÍN CHỈ
            // =========================================================================
            bool isWithdraw = qNorm.Contains("rut mon") || qNorm.Contains("huy mon") || qNorm.Contains("bo mon") ||
                              qNorm.Contains("xin rut") || qNorm.Contains("huy hoc phan") ||
                              qNorm.Contains("han muc tin chi") || qNorm.Contains("toi da bao nhieu") ||
                              qNorm.Contains("toi thieu bao nhieu") || qLower.Contains("withdraw") ||
                              qLower.Contains("drop course") || qLower.Contains("credit limit");

            if (isWithdraw)
            {
                advice.AiMessage = isEnglish
                    ? "### 📋 Course Withdrawal & Credit Limit Regulations:\n\n" +
                      "- **During online registration window:** You can freely drop or switch classes in your 'Registration Cart' or 'Enrollment Results' without penalty or failing grade.\n" +
                      "- **After online registration closes:** You must submit a written withdrawal petition (endorsed by your Academic Advisor) to the **Academic Affairs Office** for processing.\n" +
                      "- **Credit Limits per Semester:**\n" +
                      "  + Minimum: **10 Credits** (students cannot withdraw below 10 credits).\n" +
                      "  + Maximum: **24 Credits** (depending on academic standing).\n" +
                      "- **F Grade Policy:** If a course receives an F grade (< 5.0), it will be recorded and the student must retake the course in subsequent semesters."
                    : "### 📋 Quy Chế Rút Môn & Hủy Học Phần Dành Cho Sinh Viên:\n\n" +
                      "- **Trong thời gian đăng ký trực tuyến:** Bạn hoàn toàn có thể vào mục **'Giỏ đăng ký'** hoặc **'Xem kết quả đăng ký'** để xóa hoặc đổi sang lớp khác mà không bị tính phí hay ghi điểm F vào bảng điểm.\n" +
                      "- **Sau khi kết thúc thời hạn trực tuyến:** Cổng trực tuyến sẽ đóng. Để rút học phần, bạn cần làm đơn xin rút môn (có ý kiến của Cố vấn học tập) và nộp về **Phòng Đào Tạo** để Cán bộ duyệt rút môn trên hệ thống.\n" +
                      "- **Hạn mức số tín chỉ trong kỳ:**\n" +
                      "  + Tối thiểu: **10 Tín chỉ** (sinh viên không được rút xuống dưới 10 TC).\n" +
                      "  + Tối đa: **24 Tín chỉ** (tùy theo xếp loại học lực kỳ trước).\n" +
                      "- **Xử lý điểm F:** Nếu môn học bị điểm F (dưới 5.0), điểm F sẽ lưu vào học bạ và sinh viên phải đăng ký học lại môn đó ở các học kỳ tiếp theo.";
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 9: TRA CỨU MÃ LỚP HỌC PHẦN CỤ THỂ (SectionCode)
            // =========================================================================
            if (matchedSection != null)
            {
                var sec = matchedSection;
                int remainingSlots = Math.Max(0, sec.MaxCapacity - sec.CurrentEnrollment);

                advice.AiMessage = isEnglish
                    ? $"### 📋 Section Details: `{sec.SectionCode}`\n\n" +
                      $"- **Course:** **{sec.Course?.CourseName}** (`{sec.Course?.CourseCode}` - **{sec.Course?.Credits} Credits**)\n" +
                      $"- **Instructor:** **{sec.Instructor}**\n" +
                      $"- **Room:** **{sec.Room}**\n" +
                      $"- **Schedule:** **{FormatScheduleDetailed(sec, true)}**\n" +
                      $"- **Semester:** **{sec.Semester}**\n" +
                      $"- **Enrollment Capacity:** **{sec.CurrentEnrollment}/{sec.MaxCapacity} students** ({remainingSlots} slots remaining).\n"
                    : $"### 📋 Thông Tin Chi Tiết Lớp Học Phần: `{sec.SectionCode}`\n\n" +
                      $"- **Học phần:** **{sec.Course?.CourseName}** (Mã môn: `{sec.Course?.CourseCode}` - **{sec.Course?.Credits} Tín chỉ**)\n" +
                      $"- **Giảng viên phụ trách:** **{sec.Instructor}**\n" +
                      $"- **Phòng học:** **{sec.Room}**\n" +
                      $"- **Thời khóa biểu:** **{FormatScheduleDetailed(sec, false)}**\n" +
                      $"- **Học kỳ:** **{sec.Semester}**\n" +
                      $"- **Tình trạng sĩ số:** **{sec.CurrentEnrollment}/{sec.MaxCapacity} sinh viên** (Còn trống **{remainingSlots} chỗ**).\n\n" +
                      $"💡 *Lưu ý: Nếu có nhu cầu đăng ký, bạn có thể tìm mã lớp này tại mục 'Đăng Ký Học Phần' để thêm vào giỏ.*";
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 10: TRA CỨU MÔN HỌC TOÀN TRƯỜNG (Course)
            // =========================================================================
            if (matchedCourse != null)
            {
                var courseSections = sectionsPool.Where(s => s.CourseId == matchedCourse.CourseId).ToList();
                var prereqs = allPrerequisites.Where(p => p.CourseId == matchedCourse.CourseId).ToList();
                var prereqCourses = allCourses.Where(c => prereqs.Any(p => p.PrerequisiteCourseId == c.CourseId)).ToList();

                string prereqText = prereqCourses.Any()
                    ? string.Join(", ", prereqCourses.Select(c => $"**{c.CourseName} ({c.CourseCode})**"))
                    : (isEnglish ? "None (free to register)" : "Không yêu cầu (sinh viên có thể đăng ký tự do)");

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 📘 Course Information: {matchedCourse.CourseName} (`{matchedCourse.CourseCode}`)");
                    sb.AppendLine();
                    sb.AppendLine($"- **Credits:** **{matchedCourse.Credits} Credits** (Estimated tuition: **{(matchedCourse.Credits * 450000):N0} VNĐ**)");
                    sb.AppendLine($"- **Description:** {matchedCourse.Description}");
                    sb.AppendLine($"- **Prerequisites:** {prereqText}");
                    sb.AppendLine();

                    if (currentStudent != null)
                    {
                        var studentPassedPrereq = prereqCourses.All(p => completedCourses.Any(c => c.CourseId == p.CourseId && c.Grade >= 5.0));
                        if (prereqCourses.Any())
                        {
                            sb.AppendLine(studentPassedPrereq
                                ? $"✅ **Prerequisite check ({currentStudent.FullName}):** You have satisfied all prerequisites. You are eligible to register!"
                                : $"⚠️ **Prerequisite warning ({currentStudent.FullName}):** You have NOT completed prerequisite ({prereqText}). Registration will be blocked.");
                            sb.AppendLine();
                        }
                    }

                    if (courseSections.Any())
                    {
                        sb.AppendLine($"🏫 **Available sections ({courseSections.Count} classes):**");
                        sb.AppendLine();
                        foreach (var s in courseSections)
                        {
                            int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                            sb.AppendLine($"- **Class `{s.SectionCode}`** (Semester: {s.Semester})");
                            sb.AppendLine($"  + Instructor: **{s.Instructor}** | Room: **{s.Room}**");
                            sb.AppendLine($"  + Schedule: **{FormatScheduleDetailed(s, true)}**");
                            sb.AppendLine($"  + Capacity: **{s.CurrentEnrollment}/{s.MaxCapacity}** ({remain} slots left)");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"- *No open sections currently scheduled for this course in semester {semester}.*");
                    }
                }
                else
                {
                    sb.AppendLine($"### 📘 Thông Tin Học Phần: {matchedCourse.CourseName} (`{matchedCourse.CourseCode}`)");
                    sb.AppendLine();
                    sb.AppendLine($"- **Số tín chỉ:** **{matchedCourse.Credits} Tín chỉ** (Học phí tạm tính: **{(matchedCourse.Credits * 450000):N0} VNĐ**)");
                    sb.AppendLine($"- **Mô tả môn học:** {matchedCourse.Description}");
                    sb.AppendLine($"- **Học phần tiên quyết:** {prereqText}");
                    sb.AppendLine();

                    if (currentStudent != null)
                    {
                        var studentPassedPrereq = prereqCourses.All(p => completedCourses.Any(c => c.CourseId == p.CourseId && c.Grade >= 5.0));
                        if (prereqCourses.Any())
                        {
                            sb.AppendLine(studentPassedPrereq
                                ? $"✅ **Kiểm tra học bạ ({currentStudent.FullName}):** Bạn **ĐÃ HOÀN THÀNH** học phần tiên quyết cho môn này. Bạn hoàn toàn đủ điều kiện ghi danh!"
                                : $"⚠️ **Cảnh báo học bạ ({currentStudent.FullName}):** Bạn **CHƯA TÍCH LŨY ĐỦ** học phần tiên quyết ({prereqText}). Hệ thống sẽ chặn nếu cố tình ghi danh.");
                            sb.AppendLine();
                        }
                    }

                    if (courseSections.Any())
                    {
                        sb.AppendLine($"🏫 **Danh sách {courseSections.Count} lớp học phần đang mở trong kỳ:**");
                        sb.AppendLine();
                        foreach (var s in courseSections)
                        {
                            int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                            sb.AppendLine($"- **Mã lớp:** `{s.SectionCode}` (Học kỳ: **{s.Semester}**)");
                            sb.AppendLine($"  + Giảng viên phụ trách: **{s.Instructor}**");
                            sb.AppendLine($"  + Lịch học & Phòng: **{FormatScheduleDetailed(s, false)}** | Phòng: **{s.Room}**");
                            sb.AppendLine($"  + Sĩ số hiện tại: **{s.CurrentEnrollment}/{s.MaxCapacity}** (Còn trống: **{remain} chỗ**)");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"- *Hiện tại trong học kỳ {semester} chưa xếp lịch mở lớp học phần cho môn này.*");
                    }
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 11: TRA CỨU GIẢNG VIÊN TOÀN TRƯỜNG (Instructor)
            // =========================================================================
            if (duplicateInstructorNames != null && duplicateInstructorNames.Any())
            {
                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 👨‍🏫 Multiple Lecturers Found:");
                    sb.AppendLine($"Found **{duplicateInstructorNames.Count} lecturers** matching your query: {string.Join(", ", duplicateInstructorNames.Select(n => $"**{n}**"))}.");
                    sb.AppendLine();
                    sb.AppendLine("Please clarify which lecturer you would like to look up!");
                }
                else
                {
                    sb.AppendLine($"### 👨‍🏫 Tìm Thấy Nhiều Giảng Viên Trùng Khớp:");
                    sb.AppendLine($"Hệ thống tìm thấy **{duplicateInstructorNames.Count} giảng viên** có tên tương tự: {string.Join(", ", duplicateInstructorNames.Select(n => $"**{n}**"))}.");
                    sb.AppendLine();
                    sb.AppendLine("Bạn đang muốn tra cứu thông tin giảng dạy của thầy/cô cụ thể nào?");
                }
                advice.AiMessage = sb.ToString();
                return;
            }

            if (!string.IsNullOrEmpty(matchedInstructorName) && matchedInstructorSections.Any())
            {
                var sb = new StringBuilder();
                int totalEnrolled = matchedInstructorSections.Sum(s => s.CurrentEnrollment);
                int totalCap = matchedInstructorSections.Sum(s => s.MaxCapacity);

                if (isEnglish)
                {
                    sb.AppendLine($"### 👨‍🏫 Lecturer Information & Assigned Courses: **{matchedInstructorName}**");
                    sb.AppendLine();
                    sb.AppendLine($"Lecturer **{matchedInstructorName}** currently teaches **{matchedInstructorSections.Count} class(es)** (Total enrollment: **{totalEnrolled}/{totalCap} students**):");
                    sb.AppendLine();

                    var courseGroups = matchedInstructorSections.GroupBy(s => s.CourseId);
                    foreach (var cg in courseGroups)
                    {
                        var course = cg.First().Course;
                        sb.AppendLine($"📘 **Course: {course?.CourseName}** (`{course?.CourseCode}` - **{course?.Credits} Credits**)");
                        foreach (var s in cg)
                        {
                            int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                            sb.AppendLine($"- **Class Code:** `{s.SectionCode}` (Semester: **{s.Semester}**)");
                            sb.AppendLine($"  + **Schedule:** {FormatScheduleDetailed(s, true)}");
                            sb.AppendLine($"  + **Room:** {s.Room ?? "TBD"}");
                            sb.AppendLine($"  + **Enrollment:** {s.CurrentEnrollment}/{s.MaxCapacity} ({remain} slots remaining)");
                        }
                        sb.AppendLine();
                    }
                }
                else
                {
                    sb.AppendLine($"### 👨‍🏫 Thông Tin Giảng Viên & Môn Học Phụ Trách: **{matchedInstructorName}**");
                    sb.AppendLine();
                    sb.AppendLine($"Cố vấn AI đã truy vấn CSDL SQL Server thời gian thực. Giảng viên **{matchedInstructorName}** hiện phụ trách **{matchedInstructorSections.Count} lớp học phần** (Tổng sĩ số: **{totalEnrolled}/{totalCap} sinh viên**):");
                    sb.AppendLine();

                    var courseGroups = matchedInstructorSections.GroupBy(s => s.CourseId);
                    foreach (var cg in courseGroups)
                    {
                        var course = cg.First().Course;
                        sb.AppendLine($"📘 **Môn học: {course?.CourseName}** (Mã môn: `{course?.CourseCode}` — **{course?.Credits} Tín chỉ**)");
                        foreach (var s in cg)
                        {
                            int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                            sb.AppendLine($"- **Mã lớp học phần:** `{s.SectionCode}` (Học kỳ: **{s.Semester}**)");
                            sb.AppendLine($"  + **Lịch học:** **{FormatScheduleDetailed(s, false)}**");
                            sb.AppendLine($"  + **Phòng học:** **{s.Room ?? "Chưa xếp phòng"}**");
                            sb.AppendLine($"  + **Sĩ số hiện tại:** **{s.CurrentEnrollment}/{s.MaxCapacity} sinh viên** (Còn trống **{remain} chỗ**)");
                        }
                        sb.AppendLine();
                    }
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // Trường hợp 11.1: Hỏi về giảng viên nhưng không tìm thấy trong CSDL
            bool isInstructorInquiry =
                qNorm.Contains("thay ") || qNorm.Contains("co ") || qNorm.Contains("giang vien") ||
                qNorm.Contains("ai day") || qNorm.Contains("ai giang day") || qNorm.Contains("ai phu trach") ||
                qLower.Contains("lecturer") || qLower.Contains("teacher") || qLower.Contains("who teaches");

            if (isInstructorInquiry && string.IsNullOrEmpty(matchedInstructorName))
            {
                var activeInstructors = sectionsPool
                    .Where(s => !string.IsNullOrEmpty(s.Instructor))
                    .GroupBy(s => s.Instructor)
                    .OrderBy(g => g.Key)
                    .ToList();

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 🔍 Lecturer Lookup Notice:\n\nCould not find any lecturer matching query *\"{qRaw}\"* in SQL Server database.\n");
                    sb.AppendLine("👨‍🏫 **Currently assigned lecturers in the university system:**");
                    foreach (var g in activeInstructors)
                    {
                        var coursesTaught = string.Join(", ", g.Select(s => s.Course?.CourseName).Where(n => !string.IsNullOrEmpty(n)).Distinct());
                        sb.AppendLine($"- **{g.Key}**: teaches *{coursesTaught}*");
                    }
                }
                else
                {
                    sb.AppendLine($"### 🔍 Thông Báo Tra Cứu Giảng Viên:\n\nCố vấn AI đã tra cứu trực tiếp trong CSDL SQL Server nhưng **không tìm thấy giảng viên** nào khớp với nội dung: *\"{qRaw}\"*.\n");
                    sb.AppendLine("👨‍🏫 **Danh sách các giảng viên hiện đang có phân công giảng dạy trong hệ thống CSDL nhà trường:**");
                    foreach (var g in activeInstructors)
                    {
                        var coursesTaught = string.Join(", ", g.Select(s => s.Course?.CourseName).Where(n => !string.IsNullOrEmpty(n)).Distinct());
                        sb.AppendLine($"- **{g.Key}**: phụ trách môn *{coursesTaught}*");
                    }
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 12: TRA CỨU LỊCH TOÀN TRƯỜNG THEO NGÀY (Whole School Schedule)
            // =========================================================================
            if (queriedDay.HasValue)
            {
                int day = queriedDay.Value;
                string dayNameVi = day == 8 ? "Chủ Nhật" : $"Thứ {day}";
                string dayNameEn = GetDayNameEnglish(day);

                var sectionsOnDay = allSections.Where(s =>
                    s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == day)).ToList();

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 🗓️ All University Sections Scheduled on {dayNameEn} ({semester}):\n");
                    if (sectionsOnDay.Any())
                    {
                        sb.AppendLine($"There are **{sectionsOnDay.Count} sections** scheduled on **{dayNameEn}**:\n");
                        foreach (var sec in sectionsOnDay)
                        {
                            var schs = sec.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                            string periods = string.Join(", ", schs.Select(sch => $"Periods {sch.StartPeriod}-{sch.EndPeriod} (Room: {sch.Room})"));
                            int remain = Math.Max(0, sec.MaxCapacity - sec.CurrentEnrollment);
                            sb.AppendLine($"- **Class `{sec.SectionCode}`** — **{sec.Course?.CourseName}** ({sec.Course?.Credits}TC):");
                            sb.AppendLine($"  + Instructor: **{sec.Instructor}** | Room: **{sec.Room}**");
                            sb.AppendLine($"  + Session: **{periods}** | Slots left: **{remain}**");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"- No classes scheduled on {dayNameEn} in semester {semester}.");
                    }
                }
                else
                {
                    sb.AppendLine($"### 🗓️ Danh Sách Lớp Học Phần Toàn Trường Diễn Ra Vào {dayNameVi} ({semester}):\n");
                    if (sectionsOnDay.Any())
                    {
                        sb.AppendLine($"Có **{sectionsOnDay.Count} lớp học phần** diễn ra vào **{dayNameVi}**:\n");
                        foreach (var sec in sectionsOnDay)
                        {
                            var schs = sec.Schedules!.Where(sch => sch.DayOfWeek == day).ToList();
                            string periods = string.Join(", ", schs.Select(sch => $"Tiết {sch.StartPeriod} - {sch.EndPeriod} (Phòng {sch.Room})"));
                            int remain = Math.Max(0, sec.MaxCapacity - sec.CurrentEnrollment);
                            sb.AppendLine($"- **Lớp `{sec.SectionCode}`** — **{sec.Course?.CourseName}** (`{sec.Course?.CourseCode}` - {sec.Course?.Credits}TC):");
                            sb.AppendLine($"  + Giảng viên: **{sec.Instructor}**");
                            sb.AppendLine($"  + Ca học: **{periods}**");
                            sb.AppendLine($"  + Sĩ số: **{sec.CurrentEnrollment}/{sec.MaxCapacity}** (Còn trống: **{remain} chỗ**)");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"- Không có lớp học phần nào được xếp lịch vào {dayNameVi} trong học kỳ {semester}.");
                    }
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 13: TRA CỨU PHÒNG HỌC (Room)
            // =========================================================================
            if (!string.IsNullOrEmpty(matchedRoom))
            {
                var sectionsInRoom = sectionsPool.Where(s =>
                    (!string.IsNullOrEmpty(s.Room) && s.Room.Equals(matchedRoom, StringComparison.OrdinalIgnoreCase)) ||
                    (s.Schedules != null && s.Schedules.Any(sch => !string.IsNullOrEmpty(sch.Room) && sch.Room.Equals(matchedRoom, StringComparison.OrdinalIgnoreCase)))
                ).ToList();

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 🏫 Room Information: **{matchedRoom}**\n");
                    if (sectionsInRoom.Any())
                    {
                        sb.AppendLine($"Room **{matchedRoom}** is allocated for **{sectionsInRoom.Count} classes** in semester **{semester}**:\n");
                        foreach (var s in sectionsInRoom)
                        {
                            sb.AppendLine($"- **Class `{s.SectionCode}`** — **{s.Course?.CourseName}** ({s.Course?.Credits}TC):");
                            sb.AppendLine($"  + Instructor: **{s.Instructor}**");
                            sb.AppendLine($"  + Schedule: **{FormatScheduleDetailed(s, true)}**");
                            sb.AppendLine($"  + Enrollment: **{s.CurrentEnrollment}/{s.MaxCapacity} students**");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"- No classes currently scheduled in room {matchedRoom}.");
                    }
                }
                else
                {
                    sb.AppendLine($"### 🏫 Thông Tin Phòng Học: **{matchedRoom}**\n");
                    if (sectionsInRoom.Any())
                    {
                        sb.AppendLine($"Phòng **{matchedRoom}** được bố trí cho **{sectionsInRoom.Count} lớp học phần** trong học kỳ **{semester}**:\n");
                        foreach (var s in sectionsInRoom)
                        {
                            sb.AppendLine($"- **Lớp `{s.SectionCode}`** — **{s.Course?.CourseName}** ({s.Course?.Credits}TC):");
                            sb.AppendLine($"  + Giảng viên: **{s.Instructor}**");
                            sb.AppendLine($"  + Thời khóa biểu: **{FormatScheduleDetailed(s, false)}**");
                            sb.AppendLine($"  + Sĩ số: **{s.CurrentEnrollment}/{s.MaxCapacity} sinh viên**");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"- Chưa có lớp học phần nào được xếp lịch tại phòng {matchedRoom} trong học kỳ này.");
                    }
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 15: CÂU HỎI CHƯA ĐỦ THÔNG TIN HOẶC CHƯA HIỂU RÕ Ý ĐỊNH (FALLBACK)
            // Tuyệt đối không dump danh sách câu hỏi SQL Server mẫu!
            // Phản hồi tự nhiên, lịch sự và hỏi lại thông tin cần thiết.
            // =========================================================================
            if (isEnglish)
            {
                advice.AiMessage = $"I received your question: *\"{qRaw}\"*, but I don't have enough specific details to provide an accurate answer.\n\n" +
                                   $"Could you please clarify what you need assistance with? (For example: portal navigation, course details, your personal timetable, lecturer info, or tuition fees).";
            }
            else
            {
                string greetingPrefix = currentStudent != null
                    ? $"Chào bạn **{currentStudent.FullName}**! "
                    : "Chào bạn! ";

                advice.AiMessage = $"{greetingPrefix}Tôi đã nhận được câu hỏi: *\"{qRaw}\"*, nhưng chưa đủ thông tin cụ thể để phản hồi chính xác.\n\n" +
                                   $"Bạn có thể diễn đạt rõ hơn bạn đang cần hỗ trợ điều gì không (ví dụ: hướng dẫn thao tác trên website, thông tin môn học, thời khóa biểu cá nhân, thông tin giảng viên hay học phí)?";
            }
        }

        /// <summary>
        /// Thuật toán tạo gợi ý học phần tối ưu không trùng lịch và tuân thủ tiên quyết.
        /// CHỈ được gọi khi sinh viên yêu cầu gợi ý rõ ràng!
        /// </summary>
        private static void GenerateCourseRecommendations(
            AiCourseAdvice advice,
            Student student,
            List<Section> candidateSections,
            int remainingCreditsAllowed,
            string qNorm,
            string semester,
            bool isEnglish)
        {
            int targetCredits = 14;
            if (qNorm.Contains("toi da") || qNorm.Contains("nhieu") || qNorm.Contains("max"))
            {
                targetCredits = remainingCreditsAllowed;
            }
            else if (qNorm.Contains("it") || qNorm.Contains("nhe") || qNorm.Contains("vua suc") || qNorm.Contains("light") || qNorm.Contains("fewer"))
            {
                targetCredits = Math.Min(9, remainingCreditsAllowed);
            }
            else
            {
                targetCredits = Math.Min(14, remainingCreditsAllowed);
            }

            bool avoidMondayMorning = qNorm.Contains("tranh sang thu 2") || qNorm.Contains("tranh tiet som") || qNorm.Contains("tranh thu 2") || qNorm.Contains("avoid monday");

            var scored = new List<(Section Section, double Score, string Reason)>();
            foreach (var sec in candidateSections)
            {
                if (sec.Course == null) continue;
                double score = 10.0;
                var reasons = new List<string>();

                var courseNameNorm = RemoveDiacritics(sec.Course.CourseName.ToLowerInvariant());

                if (qNorm.Contains("web") && courseNameNorm.Contains("web"))
                {
                    score += 50;
                    reasons.Add(isEnglish ? "Matches your Web Programming interest" : "Khớp mục tiêu chuyên sâu Lập trình Web");
                }
                if (qNorm.Contains("csdl") && courseNameNorm.Contains("du lieu"))
                {
                    score += 45;
                    reasons.Add(isEnglish ? "Matches Database goal" : "Khớp nguyện vọng Cơ sở dữ liệu");
                }
                if (qNorm.Contains("an toan") && courseNameNorm.Contains("an toan"))
                {
                    score += 50;
                    reasons.Add(isEnglish ? "Matches Security specialization" : "Khớp chuyên ngành An toàn thông tin");
                }

                if (avoidMondayMorning && sec.Schedules != null && sec.Schedules.Any(sch => sch.DayOfWeek == 2 && sch.StartPeriod <= 3))
                {
                    score -= 100;
                }
                else if (avoidMondayMorning)
                {
                    reasons.Add(isEnglish ? "Avoids Monday morning" : "Tránh sáng Thứ 2");
                }

                int slotsLeft = sec.MaxCapacity - sec.CurrentEnrollment;
                reasons.Add(isEnglish ? $"Prerequisites met, {slotsLeft} slots available" : $"Đạt chuẩn tiên quyết, lớp còn {slotsLeft} chỗ trống");
                scored.Add((sec, score, string.Join("; ", reasons)));
            }

            var sorted = scored.OrderByDescending(x => x.Score).ToList();
            int accumulated = 0;
            var selected = new List<Section>();

            foreach (var item in sorted)
            {
                var sec = item.Section;
                int cr = sec.Course?.Credits ?? 0;
                if (accumulated + cr > remainingCreditsAllowed) continue;

                bool conflict = selected.Any(already => HasScheduleConflict(sec, already));
                if (conflict) continue;

                selected.Add(sec);
                accumulated += cr;

                advice.RecommendedSections.Add(new AiRecommendedSection
                {
                    SectionId = sec.SectionId,
                    SectionCode = sec.SectionCode,
                    CourseId = sec.CourseId,
                    CourseCode = sec.Course?.CourseCode ?? string.Empty,
                    CourseName = sec.Course?.CourseName ?? string.Empty,
                    Credits = cr,
                    Instructor = sec.Instructor,
                    Room = sec.Room,
                    ScheduleSummary = FormatSchedule(sec),
                    RecommendationReason = item.Reason,
                    IsSelected = true
                });

                if (accumulated >= targetCredits) break;
            }

            if (isEnglish)
            {
                advice.AiMessage = $"### 🎯 Optimal Course Registration Recommendation:\n\n" +
                                   $"Hello **{student.FullName}** (`{student.StudentCode}` - Major: {student.Major})!\n\n" +
                                   $"- Based on your completed courses and academic goals, the AI Advisor recommends **{advice.RecommendedSections.Count} course(s) ({advice.TotalCredits} Credits)** for semester {semester}.\n" +
                                   $"- **Academic Safety Guarantee:** 100% of recommended courses meet all prerequisites and **have zero schedule conflicts**.\n" +
                                   $"- **User Confirmation Required:** This recommendation is an advisory suggestion. Please review the items below, select desired courses, and click **'Confirm & Add to Cart'** to proceed.";
            }
            else
            {
                advice.AiMessage = $"### 🎯 Đề Xuất Lộ Trình Học Tập Tối Ưu Cho Sinh Viên:\n\n" +
                                   $"Chào bạn **{student.FullName}** (`{student.StudentCode}` - Ngành {student.Major})!\n\n" +
                                   $"- Dựa trên hồ sơ học bạ và nguyện vọng của bạn, Cố vấn AI đề xuất gói học phần gồm **{advice.RecommendedSections.Count} môn ({advice.TotalCredits} Tín chỉ)** trong học kỳ {semester}.\n" +
                                   $"- **Cam kết an toàn học vụ:** 100% môn đề xuất đã thỏa mãn điều kiện tiên quyết và **tuyệt đối không bị trùng lịch học**.\n" +
                                   $"- **Xác nhận người dùng:** Dữ liệu do AI đề xuất dưới đây là gợi ý. Bạn hãy rà soát danh sách bên dưới, chọn các môn mong muốn và bấm nút **'Xác Nhận & Thêm Vào Giỏ Đăng Ký'** để lưu vào giỏ hàng.";
            }
        }

        #region Helper Extraction & Matching Methods

        private static bool DetectIsEnglish(string qLower, string qNorm)
        {
            string[] enKeywords = new[]
            {
                "what ", "which ", "who ", "how ", "when ", "where ", "do i ", "does ", "can i ", "could i ",
                "my schedule", "my courses", "my classes", "registered for", "enrolled in", "enrolled courses",
                "tomorrow", "today", "this week", "who teaches", "lecturer", "instructor", "teacher",
                "prerequisite", "credits", "tuition", "drop course", "withdraw", "recommend"
            };

            return enKeywords.Any(k => qLower.Contains(k));
        }

        private static bool DetectHasPersonalPronoun(string qNorm, string qLower)
        {
            return qNorm.Contains("cua toi") || qNorm.Contains("cua em") || qNorm.Contains("cua minh") ||
                   qNorm.Contains("toi hoc") || qNorm.Contains("em hoc") || qNorm.Contains("minh hoc") ||
                   qNorm.Contains("toi co") || qNorm.Contains("em co") || qNorm.Contains("minh co") ||
                   qNorm.Contains("cho em") || qNorm.Contains("cho toi") || qNorm.Contains("toi da") ||
                   qNorm.Contains("em da") || qNorm.Contains("minh da") ||
                   qLower.Contains("do i ") || qLower.Contains("can i ") || qLower.Contains("my ") ||
                   qLower.Contains("i have") || qLower.Contains("i enrolled") || qLower.Contains("i registered");
        }

        private static bool IsPersonalRegistrationQuery(string qNorm, string qLower)
        {
            return qNorm.Contains("thoi khoa bieu") || qNorm.Contains("lich hoc cua") ||
                   qNorm.Contains("mon da dang ky") || qNorm.Contains("phieu dang ky") ||
                   qNorm.Contains("ket qua dang ky") || qLower.Contains("enrolled courses") ||
                   qLower.Contains("registered courses") || qLower.Contains("my schedule");
        }

        private static bool IsFollowUpQuestion(string qNorm, string qLower)
        {
            return qNorm.Contains("mon do") || qNorm.Contains("mon nay") ||
                   qNorm.Contains("lop do") || qNorm.Contains("lop nay") ||
                   qNorm.Contains("thay do") || qNorm.Contains("co do") ||
                   qNorm.Contains("the con") || qNorm.Contains("mon nao co lich") ||
                   qNorm.Contains("cai do") || qNorm.Contains("cai nay") ||
                   qNorm.Contains("luc may gio") || qNorm.Contains("may gio") || qNorm.Contains("ca may") ||
                   qNorm.Contains("tiet may") ||
                   qNorm.Contains("ai day") || qNorm.Contains("thay nao day") ||
                   qNorm.Contains("phong nao") || qNorm.Contains("o dau") ||
                   qLower.Contains("who teaches it") || qLower.Contains("who teaches that") ||
                   qLower.Contains("which of those") || qLower.Contains("which room") ||
                   qLower.Contains("what time") || qLower.Contains("when is it") ||
                   qLower.Contains("what about");
        }

        private static bool IsGreetingIntent(string qNorm, string qLower)
        {
            bool hasAcademicOrSystemIntent =
                qNorm.Contains("mon") || qNorm.Contains("lop") || qNorm.Contains("lich") ||
                qNorm.Contains("thay") || qNorm.Contains("co ") || qNorm.Contains("giang vien") ||
                qNorm.Contains("hoc phi") || qNorm.Contains("dang ky") || qNorm.Contains("dang nhap") ||
                qNorm.Contains("dang xuat") || qLower.Contains("schedule") || qLower.Contains("course") ||
                qLower.Contains("login") || qLower.Contains("logout");

            if (hasAcademicOrSystemIntent) return false;

            return qNorm == "chao" || qNorm == "chao ban" || qNorm == "xin chao" || qNorm.StartsWith("chao ") ||
                   qNorm.StartsWith("xin chao") || qNorm == "hello" || qNorm == "hi" || qNorm.StartsWith("hi ") ||
                   qNorm == "alo" || qNorm.StartsWith("alo ") || qNorm.Contains("ban la ai") || qLower.Contains("who are you") ||
                   qNorm == "good morning" || qNorm == "good afternoon" || qNorm == "good evening";
        }

        private static bool IsThanksIntent(string qNorm, string qLower)
        {
            return qNorm.Contains("cam on") || qNorm.Contains("cam on ban") || qNorm.Contains("cam on co van") ||
                   qNorm.Contains("tks") || qNorm.Contains("thank you") || qNorm.Contains("thanks") ||
                   qNorm == "thank" || qNorm == "thanks a lot";
        }

        private static bool IsFarewellIntent(string qNorm, string qLower)
        {
            return qNorm.Contains("tam biet") || qNorm.Contains("hen gap lai") || qNorm == "bye" ||
                   qNorm == "goodbye" || qNorm == "bye bye" || qLower.Contains("see you") ||
                   qLower.Contains("have a good day");
        }

        private static bool IsLoginInstructionIntent(string qNorm, string qLower)
        {
            bool asksLoginVi = qNorm.Contains("dang nhap") &&
                (qNorm.Contains("buoc") || qNorm.Contains("nhung buoc") || qNorm.Contains("cac buoc") ||
                 qNorm.Contains("nhu the nao") || qNorm.Contains("the nao") || qNorm.Contains("lam sao") ||
                 qNorm.Contains("cach") || qNorm.Contains("huong dan") || qNorm.Contains("o dau") ||
                 qNorm.Contains("muon") || qNorm.Contains("khong biet") || qNorm.Contains("cho nao") ||
                 qNorm.Contains("vao he thong"));

            bool asksSystemAccessVi = (qNorm.Contains("vao he thong") || qNorm.Contains("truy cap he thong")) &&
                (qNorm.Contains("lam sao") || qNorm.Contains("the nao") || qNorm.Contains("cach") || qNorm.Contains("huong dan") || qNorm.Contains("buoc"));

            bool asksLoginEn = qLower.Contains("how do i log in") || qLower.Contains("how to log in") ||
                               qLower.Contains("how to login") || qLower.Contains("how can i log in") ||
                               qLower.Contains("how can i login") || qLower.Contains("where do i log in") ||
                               qLower.Contains("where to log in") || qLower.Contains("login steps") ||
                               qLower.Contains("steps to log in") || qLower.Contains("how do i sign in") ||
                               qLower.Contains("how to sign in") || qLower.Contains("access the system");

            return asksLoginVi || asksSystemAccessVi || asksLoginEn;
        }

        private static bool IsLogoutOrAccountIntent(string qNorm, string qLower, out string actionType)
        {
            actionType = string.Empty;

            if (qNorm.Contains("dang xuat") || qNorm.Contains("thoat tai khoan") || qNorm.Contains("thoat khoi he thong") ||
                qLower.Contains("log out") || qLower.Contains("logout") || qLower.Contains("sign out"))
            {
                actionType = "Logout";
                return true;
            }

            if (qNorm.Contains("doi mat khau") || qNorm.Contains("thay mat khau") || qNorm.Contains("quen mat khau") ||
                qLower.Contains("change password") || qLower.Contains("reset password"))
            {
                actionType = "ChangePassword";
                return true;
            }

            if (qNorm.Contains("dang ky tai khoan") || qNorm.Contains("tao tai khoan") || qNorm.Contains("mo tai khoan") ||
                qNorm.Contains("cap tai khoan") || qLower.Contains("register account") || qLower.Contains("create account"))
            {
                actionType = "RegisterAccount";
                return true;
            }

            return false;
        }

        private static bool IsSystemFeatureGuideIntent(string qNorm, string qLower)
        {
            bool asksFeaturesVi = (qNorm.Contains("chuc nang") || qNorm.Contains("tinh nang") || qNorm.Contains("lam duoc gi") || qNorm.Contains("dung de lam gi")) &&
                                  (qNorm.Contains("he thong") || qNorm.Contains("website") || qNorm.Contains("trang web") || qNorm.Contains("web nay") || qNorm.Contains("nay"));

            bool asksProcessVi = (qNorm.Contains("quy trinh dang ky") || qNorm.Contains("cac buoc dang ky hoc phan") || qNorm.Contains("huong dan dang ky hoc phan")) &&
                                 !qNorm.Contains("mon nay") && !qNorm.Contains("lop nay");

            bool asksFeaturesEn = qLower.Contains("what features") || qLower.Contains("system features") ||
                                  qLower.Contains("what can this website do") || qLower.Contains("what does this system do") ||
                                  qLower.Contains("registration process") || qLower.Contains("how does registration work");

            return asksFeaturesVi || asksProcessVi || asksFeaturesEn;
        }

        private static bool IsOutOfScopeIntent(string qNorm, string qLower)
        {
            // Nếu có liên quan đến học vụ / trường học thì KHÔNG phải out-of-scope
            if (qNorm.Contains("mon") || qNorm.Contains("lop") || qNorm.Contains("tin chi") ||
                qNorm.Contains("hoc phi") || qNorm.Contains("giang vien") || qNorm.Contains("thay") ||
                qNorm.Contains("co ") || qNorm.Contains("phong") || qNorm.Contains("lich") ||
                qNorm.Contains("thoi khoa bieu") || qNorm.Contains("dang ky") || qNorm.Contains("diem") ||
                qNorm.Contains("tien quyet") || qNorm.Contains("sinh vien") || qNorm.Contains("mssv") ||
                qNorm.Contains("vietqr") || qNorm.Contains("mb bank") || qNorm.Contains("hoc ky") ||
                qLower.Contains("course") || qLower.Contains("class") || qLower.Contains("credit") ||
                qLower.Contains("tuition") || qLower.Contains("lecturer") || qLower.Contains("schedule") ||
                qLower.Contains("student") || qLower.Contains("semester") || qLower.Contains("prerequisite"))
            {
                return false;
            }

            // Thời tiết:
            bool isWeather = qNorm.Contains("thoi tiet") || qNorm.Contains("troi mua") || qNorm.Contains("troi nang") ||
                             qNorm.Contains("nhiet do") || qLower.Contains("weather") || qLower.Contains("forecast") ||
                             qLower.Contains("rain") || qLower.Contains("temperature");

            // Thể thao:
            bool isSports = qNorm.Contains("bong da") || qNorm.Contains("world cup") || qNorm.Contains("ngoai hang anh") ||
                            qNorm.Contains("champion league") || qNorm.Contains("messi") || qNorm.Contains("ronaldo") ||
                            qLower.Contains("football") || qLower.Contains("soccer");

            // Nấu ăn:
            bool isCooking = qNorm.Contains("nau an") || qNorm.Contains("cong thuc") || qNorm.Contains("pho bo") ||
                             qNorm.Contains("bun bo") || qLower.Contains("recipe") || qLower.Contains("how to cook");

            // Giải trí ngoài lề / thơ / chuyện cười:
            bool isEntertainment = qNorm.Contains("chuyen cuoi") || qNorm.Contains("ke chuyen") || qNorm.Contains("hat mot bai") ||
                                   qNorm.Contains("tho tinh") || qLower.Contains("tell me a joke") || qLower.Contains("sing a song") ||
                                   qLower.Contains("write a poem");

            // Kinh tế ngoài lề / tin tức tổng hợp:
            bool isGeneralNews = qNorm.Contains("gia vang") || qNorm.Contains("chung khoan") || qNorm.Contains("bitcoin") ||
                                 qNorm.Contains("crypto") || qNorm.Contains("tong thong") || qLower.Contains("stock price") ||
                                 qLower.Contains("gold price") || qLower.Contains("capital of france");

            return isWeather || isSports || isCooking || isEntertainment || isGeneralNews;
        }

        private static bool IsExplicitSampleQuestionRequest(string qNorm, string qLower)
        {
            return qNorm.Contains("goi y cau hoi") || qNorm.Contains("mau cau hoi") ||
                   qNorm.Contains("toi co the hoi gi") || qNorm.Contains("hoi duoc nhung gi") ||
                   qNorm.Contains("ban tra loi duoc nhung gi") || qLower.Contains("sample questions") ||
                   qLower.Contains("what can i ask") || qLower.Contains("example questions");
        }

        private static Student? FindMatchingStudent(string qNorm, string qLower, List<Student> allStudents)
        {
            // 1. Khớp theo MSSV (e.g. SV21001, SV21002)
            var mssvMatch = Regex.Match(qLower, @"\b(sv\d{5})\b");
            if (mssvMatch.Success)
            {
                string code = mssvMatch.Groups[1].Value.ToUpperInvariant();
                var s = allStudents.FirstOrDefault(st => st.StudentCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (s != null) return s;
            }

            // 2. Khớp theo họ tên đầy đủ
            foreach (var stu in allStudents)
            {
                string stuNorm = RemoveDiacritics(stu.FullName.ToLowerInvariant());
                if (!string.IsNullOrEmpty(stuNorm) && qNorm.Contains(stuNorm))
                {
                    return stu;
                }
            }

            return null;
        }

        private static (string Name, List<Section> Sections, List<string> DuplicateNames) FindMatchingInstructor(string qNorm, string qLower, List<Section> allSections)
        {
            var instructors = allSections
                .Where(s => !string.IsNullOrEmpty(s.Instructor))
                .Select(s => s.Instructor)
                .Distinct()
                .ToList();

            var matches = new List<(int Score, string Name, List<Section> Sections)>();

            foreach (var inst in instructors)
            {
                string cleanName = CleanAcademicTitle(inst);
                string cleanNorm = RemoveDiacritics(cleanName.ToLowerInvariant());
                string rawNorm = RemoveDiacritics(inst.ToLowerInvariant());

                int score = 0;

                // 1. Khớp nguyên họ tên đầy đủ đã bỏ học vị (ví dụ: "ha ngoc long", "ngo bao chau", "le hoang son")
                if (!string.IsNullOrEmpty(cleanNorm) && qNorm.Contains(cleanNorm))
                {
                    score = 100;
                }
                // 2. Khớp nguyên chuỗi gốc cả học vị (ví dụ: "ts ha ngoc long", "pgs.ts. le hoang son")
                else if (qNorm.Contains(rawNorm))
                {
                    score = 100;
                }
                else
                {
                    var words = cleanNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var lastName = words.LastOrDefault(); // "long", "truc", "son", "tuan", "tung", "chau", "cuong", "dung", "hanh"
                    var middleAndLast = words.Length >= 2 ? $"{words[words.Length - 2]} {lastName}" : string.Empty;

                    // 3. Khớp tên đệm + tên chính (ví dụ: "ngoc long", "thanh truc", "hoang son")
                    if (!string.IsNullOrEmpty(middleAndLast) && qNorm.Contains(middleAndLast))
                    {
                        score = 80;
                    }
                    // 4. Khớp tên chính kèm chức danh / từ khóa giảng viên
                    else if (!string.IsNullOrEmpty(lastName) && lastName.Length >= 2)
                    {
                        if (qNorm.Contains("thay " + lastName) || qNorm.Contains("co " + lastName) ||
                            qNorm.Contains("gv " + lastName) || qNorm.Contains("giang vien " + lastName) ||
                            qLower.Contains("dr " + lastName) || qLower.Contains("dr. " + lastName) ||
                            qLower.Contains("prof " + lastName) || qLower.Contains("prof. " + lastName) ||
                            qLower.Contains("mr " + lastName) || qLower.Contains("mr. " + lastName) ||
                            qLower.Contains("ms " + lastName) || qLower.Contains("ms. " + lastName) ||
                            qNorm.Contains(lastName + " day") || qNorm.Contains(lastName + " phu trach") ||
                            qLower.Contains(lastName + " teach"))
                        {
                            score = 70;
                        }
                    }
                }

                if (score > 0)
                {
                    var matchedSecs = allSections.Where(s => s.Instructor == inst).ToList();
                    matches.Add((score, inst, matchedSecs));
                }
            }

            if (matches.Any())
            {
                var topScore = matches.Max(m => m.Score);
                var topMatches = matches.Where(m => m.Score == topScore).ToList();
                var distinctNames = topMatches.Select(m => m.Name).Distinct().ToList();

                if (distinctNames.Count > 1)
                {
                    return (string.Empty, new List<Section>(), distinctNames);
                }

                var best = topMatches.First();
                return (best.Name, best.Sections, new List<string>());
            }

            return (string.Empty, new List<Section>(), new List<string>());
        }

        private static bool IsInstructorMatch(string sectionInstructor, string targetInstructorName)
        {
            if (string.IsNullOrEmpty(sectionInstructor) || string.IsNullOrEmpty(targetInstructorName)) return false;

            if (sectionInstructor.Equals(targetInstructorName, StringComparison.OrdinalIgnoreCase)) return true;

            string sNorm = RemoveDiacritics(CleanAcademicTitle(sectionInstructor).ToLowerInvariant());
            string tNorm = RemoveDiacritics(CleanAcademicTitle(targetInstructorName).ToLowerInvariant());

            if (sNorm.Contains(tNorm) || tNorm.Contains(sNorm)) return true;

            return false;
        }

        private static Course? FindMatchingCourse(string qNorm, string qLower, List<Course> allCourses)
        {
            // 1. Khớp mã môn học (WEB301, CSDL201, TIN101, TIN102, TIN201, KTPM302, ATTT401, TOEIC, AI401, WEB302, CSDL202, ATTT402)
            foreach (var c in allCourses)
            {
                string codeLower = c.CourseCode.ToLowerInvariant();
                if (Regex.IsMatch(qLower, $@"\b{Regex.Escape(codeLower)}\b"))
                {
                    return c;
                }
            }

            // 2. Khớp tên môn học
            foreach (var c in allCourses)
            {
                string nameNorm = RemoveDiacritics(c.CourseName.ToLowerInvariant());
                if (qNorm.Contains(nameNorm))
                {
                    return c;
                }
            }

            // 3. Từ khóa rút gọn tiếng Việt & tiếng Anh
            if (qNorm.Contains("lap trinh web") || qNorm.Contains("web programming") || qNorm.Contains("web app") || (qNorm.Contains("web") && !qNorm.Contains("web302")))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "WEB301") ?? allCourses.FirstOrDefault(c => c.CourseCode == "WEB302");
            }
            if (qNorm.Contains("co so du lieu") || qNorm.Contains("csdl") || qLower.Contains("database"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "CSDL201");
            }
            if (qNorm.Contains("cau truc du lieu") || qNorm.Contains("giai thuat") || qLower.Contains("data structure") || qLower.Contains("algorithms"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TIN201");
            }
            if (qNorm.Contains("huong doi tuong") || qLower.Contains("oop") || qLower.Contains("object oriented"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TIN102");
            }
            if (qNorm.Contains("nhap mon lap trinh") || qNorm.Contains("tin101") || qLower.Contains("intro to programming"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TIN101");
            }
            if (qNorm.Contains("an toan") || qNorm.Contains("bao mat") || qNorm.Contains("attt") || qLower.Contains("security") || qLower.Contains("cyber"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "ATTT401");
            }
            if (qNorm.Contains("kiem thu") || qNorm.Contains("ktpm") || qNorm.Contains("ky thuat phan mem") || qLower.Contains("software engineering"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "KTPM302");
            }
            if (qNorm.Contains("tieng anh") || qNorm.Contains("toeic") || qNorm.Contains("anh van") || qLower.Contains("english"))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "TOEIC");
            }
            if (qNorm.Contains("tri tue nhan tao") || qNorm.Contains("hoc may") || qLower.Contains("machine learning") || qNorm.Contains("ai401") || (qNorm.Contains("ai") && qNorm.Contains("mon")))
            {
                return allCourses.FirstOrDefault(c => c.CourseCode == "AI401");
            }

            return null;
        }

        private static Section? FindMatchingSection(string qLower, List<Section> allSections)
        {
            var match = Regex.Match(qLower, @"\b(web\d{3}_\d{2}|tin\d{3}_\d{2}|csdl\d{3}_\d{2}|ktpm\d{3}_\d{2}|attt\d{3}_\d{2}|toeic_\d{2}|ai\d{3}_\d{2})\b");
            if (match.Success)
            {
                string secCode = match.Groups[1].Value;
                return allSections.FirstOrDefault(s => s.SectionCode.Equals(secCode, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static int? DetectTargetDay(string qNorm, string qLower)
        {
            if (qNorm.Contains("hom nay") || qLower.Contains("today"))
            {
                DayOfWeek dow = DateTime.Today.DayOfWeek;
                return dow == DayOfWeek.Sunday ? 8 : ((int)dow + 1);
            }

            if (qNorm.Contains("ngay mai") || qNorm.Contains("mai") || qLower.Contains("tomorrow"))
            {
                DayOfWeek dow = DateTime.Today.AddDays(1).DayOfWeek;
                return dow == DayOfWeek.Sunday ? 8 : ((int)dow + 1);
            }

            if (qNorm.Contains("thu 2") || qNorm.Contains("thu hai") || qLower.Contains("monday")) return 2;
            if (qNorm.Contains("thu 3") || qNorm.Contains("thu ba") || qLower.Contains("tuesday")) return 3;
            if (qNorm.Contains("thu 4") || qNorm.Contains("thu tu") || qLower.Contains("wednesday")) return 4;
            if (qNorm.Contains("thu 5") || qNorm.Contains("thu nam") || qLower.Contains("thursday")) return 5;
            if (qNorm.Contains("thu 6") || qNorm.Contains("thu sau") || qLower.Contains("friday")) return 6;
            if (qNorm.Contains("thu 7") || qNorm.Contains("thu bay") || qLower.Contains("saturday")) return 7;
            if (qNorm.Contains("chu nhat") || qNorm.Contains("cn") || qLower.Contains("sunday")) return 8;

            return null;
        }

        private static bool DetectIsWeekQuery(string qNorm, string qLower)
        {
            return qNorm.Contains("tuan nay") || qNorm.Contains("ca tuan") || qNorm.Contains("trong tuan") ||
                   qLower.Contains("this week") || qLower.Contains("weekly schedule") || qLower.Contains("weekly timetable");
        }

        private static string? FindMatchingRoom(string qNorm, string qLower, List<Section> allSections)
        {
            var rooms = allSections
                .Where(s => !string.IsNullOrEmpty(s.Room))
                .Select(s => s.Room)
                .Distinct()
                .ToList();

            foreach (var r in rooms)
            {
                string rNorm = RemoveDiacritics(r.ToLowerInvariant());
                if (qNorm.Contains(rNorm) || qLower.Contains(r.ToLowerInvariant())) return r;
            }

            if (qNorm.Contains("lab 402") || qNorm.Contains("402")) return "Lab 402";
            if (qNorm.Contains("lab 403") || qNorm.Contains("403")) return "Lab 403";
            if (qNorm.Contains("lab 501") || qNorm.Contains("501")) return "Lab 501";
            if (qNorm.Contains("lab 502") || qNorm.Contains("502")) return "Lab 502";
            if (qNorm.Contains("a101")) return "Phòng A101";
            if (qNorm.Contains("a102")) return "Phòng A102";
            if (qNorm.Contains("a205")) return "Phòng A205";
            if (qNorm.Contains("b204")) return "Phòng B204";
            if (qNorm.Contains("b301")) return "Phòng B301";
            if (qNorm.Contains("c301")) return "Phòng C301";
            if (qNorm.Contains("c302")) return "Phòng C302";
            if (qNorm.Contains("hoi truong a")) return "Hội trường A";
            if (qNorm.Contains("hoi truong b")) return "Hội trường B";

            return null;
        }

        private static string CleanAcademicTitle(string instructorName)
        {
            if (string.IsNullOrWhiteSpace(instructorName)) return string.Empty;
            var name = instructorName.Trim();
            string[] titles = new[]
            {
                "pgs.ts.", "pgs. ts.", "gs.ts.", "gs. ts.", "pgs.", "gs.",
                "ts.", "ths.", "cn.", "pgs.ts", "gs.ts", "pgs", "gs", "ts", "ths",
                "tiến sĩ", "tien si", "thạc sĩ", "thac si", "giáo sư", "giao su", "phó giáo sư", "pho giao su",
                "thầy", "thay", "cô", "co", "giảng viên", "giang vien", "gv", "gv.",
                "prof.", "prof", "dr.", "dr", "mr.", "mr", "ms.", "ms"
            };
            bool changed = true;
            while (changed)
            {
                changed = false;
                var lower = name.Trim().ToLowerInvariant();
                var norm = RemoveDiacritics(lower);
                foreach (var t in titles)
                {
                    if (norm.StartsWith(t + " ") || lower.StartsWith(t + " "))
                    {
                        int len = t.Length + 1;
                        name = name.Trim().Substring(Math.Min(name.Trim().Length, len)).Trim();
                        changed = true;
                        break;
                    }
                }
            }
            return name.Trim();
        }

        private static bool IsExplicitRecommendationRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string norm = RemoveDiacritics(text.ToLowerInvariant());

            return norm.Contains("goi y") || norm.Contains("tu van") || norm.Contains("de xuat") ||
                   norm.Contains("nen hoc") || norm.Contains("nen dang ky") || norm.Contains("hoc gi ky nay") ||
                   norm.Contains("dang ky gi ky nay") || norm.Contains("ke hoach hoc tap") ||
                   norm.Contains("lo trinh hoc") || norm.Contains("hoc nhe") || norm.Contains("can bang") ||
                   text.ToLowerInvariant().Contains("recommend") || text.ToLowerInvariant().Contains("suggest course") ||
                   text.ToLowerInvariant().Contains("what courses should i");
        }

        private static List<Section> GetRegisteredSectionsForStudent(int studentId, string semester, IEnrollmentRepository enrollmentRepo)
        {
            try
            {
                return enrollmentRepo.GetRegisteredSectionsByStudentAsync(studentId, semester).GetAwaiter().GetResult().ToList();
            }
            catch
            {
                return new List<Section>();
            }
        }

        private static string FormatSchedule(Section section)
        {
            if (section.Schedules == null || !section.Schedules.Any()) return "Chưa xếp lịch";
            return string.Join("; ", section.Schedules.Select(s => $"Thứ {s.DayOfWeek} (Tiết {s.StartPeriod}-{s.EndPeriod}, {s.Room})"));
        }

        private static string FormatScheduleDetailed(Section section, bool isEnglish)
        {
            if (section.Schedules == null || !section.Schedules.Any())
                return isEnglish ? "Schedule TBD" : "Chưa xếp lịch thời khóa biểu";

            var items = section.Schedules.Select(s =>
            {
                if (isEnglish)
                {
                    string dayStr = GetDayNameEnglish(s.DayOfWeek);
                    return $"{dayStr} (Periods {s.StartPeriod} to {s.EndPeriod})";
                }
                else
                {
                    string dayStr = s.DayOfWeek == 8 ? "Chủ Nhật" : $"Thứ {s.DayOfWeek}";
                    return $"{dayStr} (Từ Tiết {s.StartPeriod} đến Tiết {s.EndPeriod})";
                }
            });
            return string.Join(" & ", items);
        }

        private static string GetDayNameEnglish(int dayOfWeek)
        {
            return dayOfWeek switch
            {
                2 => "Monday",
                3 => "Tuesday",
                4 => "Wednesday",
                5 => "Thursday",
                6 => "Friday",
                7 => "Saturday",
                8 => "Sunday",
                _ => $"Day {dayOfWeek}"
            };
        }

        private static bool HasScheduleConflict(Section s1, Section s2)
        {
            if (s1.Schedules == null || s2.Schedules == null) return false;
            foreach (var sch1 in s1.Schedules)
            {
                foreach (var sch2 in s2.Schedules)
                {
                    if (sch1.DayOfWeek == sch2.DayOfWeek)
                    {
                        if (sch1.StartPeriod <= sch2.EndPeriod && sch2.StartPeriod <= sch1.EndPeriod)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace('đ', 'd')
                .Replace('Đ', 'D');
        }

        private class ConversationContextInfo
        {
            public bool HasContext => LastReferencedCourse != null || LastReferencedSection != null ||
                                      !string.IsNullOrEmpty(LastReferencedInstructor) || LastReferencedStudent != null ||
                                      LastMentionedSections.Any();

            public Course? LastReferencedCourse { get; set; }
            public Section? LastReferencedSection { get; set; }
            public string? LastReferencedInstructor { get; set; }
            public Student? LastReferencedStudent { get; set; }
            public List<Section> LastMentionedSections { get; set; } = new List<Section>();
        }

        private static ConversationContextInfo ExtractConversationContext(
            List<AiConversationMessage>? conversationHistory,
            List<Course> allCourses,
            List<Section> allSections,
            List<Student> allStudents)
        {
            var context = new ConversationContextInfo();
            if (conversationHistory == null || !conversationHistory.Any()) return context;

            // Rà soát ngược từ tin nhắn gần nhất
            for (int i = conversationHistory.Count - 1; i >= 0; i--)
            {
                var msg = conversationHistory[i];
                string content = msg.Content;
                string contentLower = content.ToLowerInvariant();
                string contentNorm = RemoveDiacritics(contentLower);

                if (context.LastReferencedCourse == null)
                {
                    context.LastReferencedCourse = FindMatchingCourse(contentNorm, contentLower, allCourses);
                }

                if (context.LastReferencedSection == null)
                {
                    context.LastReferencedSection = FindMatchingSection(contentLower, allSections);
                }

                if (string.IsNullOrEmpty(context.LastReferencedInstructor))
                {
                    var (inst, _, _) = FindMatchingInstructor(contentNorm, contentLower, allSections);
                    if (!string.IsNullOrEmpty(inst)) context.LastReferencedInstructor = inst;
                }

                if (context.LastReferencedStudent == null)
                {
                    context.LastReferencedStudent = FindMatchingStudent(contentNorm, contentLower, allStudents);
                }

                // Trích xuất các mã lớp học phần được nhắc trong câu trả lời gần nhất
                if (!context.LastMentionedSections.Any())
                {
                    var matches = Regex.Matches(contentLower, @"\b(web\d{3}_\d{2}|tin\d{3}_\d{2}|csdl\d{3}_\d{2}|ktpm\d{3}_\d{2}|attt\d{3}_\d{2}|toeic_\d{2}|ai\d{3}_\d{2})\b");
                    foreach (Match m in matches)
                    {
                        var s = allSections.FirstOrDefault(sec => sec.SectionCode.Equals(m.Value, StringComparison.OrdinalIgnoreCase));
                        if (s != null && !context.LastMentionedSections.Any(x => x.SectionId == s.SectionId))
                        {
                            context.LastMentionedSections.Add(s);
                        }
                    }
                }

                if (context.HasContext) break;
            }

            return context;
        }

        #region Specialized Role-Based System Prompts

        /// <summary>
        /// System Prompt dành cho Khách (Chưa đăng nhập):
        /// - Trả lời quy chế đào tạo, hướng dẫn đăng nhập và sử dụng hệ thống.
        /// - Tuyệt đối KHÔNG trả lời TKB, điểm số, học bạ cá nhân; yêu cầu người dùng đăng nhập.
        /// </summary>
        private static string BuildGuestSystemPrompt(
            List<Course> allCourses,
            List<Section> allSections,
            string semester)
        {
            var coursesSummary = string.Join(", ", allCourses.Select(c => $"{c.CourseCode}: {c.CourseName} ({c.Credits}TC)"));

            return $@"Bạn là Trợ lý Tuyển sinh & Cố vấn Học vụ (Trường Đại học Kinh tế Huế) dành cho KHÁCH THAM QUAN (CHƯA ĐĂNG NHẬP).

[PHONG CÁCH GIAO TIẾP]
- Lịch sự, cởi mở, tự nhiên, trả lời THẲNG VÀO TRỌNG TÂM câu hỏi.
- Tuyệt đối không trả lời dập khuôn theo văn mẫu cứng nhắc. Hỗ trợ tiếng Việt và tiếng Anh tự nhiên.
- TUYỆT ĐỐI KHÔNG tự ý chèn một danh sách dài các câu hỏi mẫu SQL Server khi người dùng không yêu cầu.

[DỮ LIỆU CÔNG KHAI ĐƯỢC PHÉP TRẢ LỜI]
- Danh mục môn học đào tạo: {coursesSummary}
- Hạn mức số tín chỉ: Mỗi sinh viên được đăng ký tối thiểu 10 tín chỉ, tối đa 24 tín chỉ trong học kỳ chính ({semester}).
- Mức thu học phí: 450.000 VNĐ / 1 tín chỉ. Nộp qua tài khoản nhà trường tại MB Bank (STK: 101705201967, thụ hưởng TRƯỜNG ĐẠI HỌC KINH TẾ HUẾ, thanh toán VietQR Napas 24/7).
- Quy chế rút môn học: Rút trong 2 tuần đầu đợt đăng ký không lưu điểm F; sau đó phải nộp đơn tại Phòng Đào Tạo.
- Hướng dẫn các tính năng của website (5 phân hệ chính):
  1. Tra cứu & Tìm kiếm học phần (`/sections`): Tìm môn học, xem lịch, giảng viên, phòng học và sĩ số.
  2. Giỏ đăng ký học phần (`/cart`): Lên danh sách lớp dự kiến, kiểm tra trùng lịch và điều kiện tiên quyết trước khi lưu.
  3. Thời khóa biểu (`/schedule`): Xem lịch học theo tuần hoặc danh sách chi tiết.
  4. Thanh toán học phí VietQR (`/tuition`): Tra cứu công nợ (450k/TC) và quét mã VietQR MB Bank 24/7.
  5. Cố vấn Học thuật AI (`/ai-advisor`): Trợ lý thông minh hỗ trợ giải đáp học vụ 24/7.

[QUY TẮC XỬ LÝ Ý ĐỊNH ĐẶC THÙ]
1. HƯỚNG DẪN ĐĂNG NHẬP:
   Khi người dùng hỏi cách đăng nhập / các bước đăng nhập / 'How do I log in':
   Hướng dẫn đúng 3 bước theo giao diện thực tế:
   - Bước 1: Nhấn vào nút 'Đăng nhập' màu xanh ở góc trên bên phải thanh điều hướng (Navbar) hoặc vào `/login`.
   - Bước 2: Nhập Tài khoản/MSSV (ví dụ: `SV21001`, `SV21002`) và Mật khẩu (mặc định của sinh viên là `sv123`, quản trị là `admin123`).
   - Bước 3: Nhấn nút 'Đăng Nhập' để hoàn tất xác thực.
2. HƯỚNG DẪN ĐĂNG XUẤT & ĐỔI MẬT KHẨU:
   - Đăng xuất: Bấm vào tên/avatar ở góc phải trên Navbar -> chọn mục màu đỏ 'Đăng xuất'.
   - Đổi mật khẩu: Bấm vào avatar -> chọn 'Đổi mật khẩu' (hoặc vào `/change-password`).
3. CÂU HỎI NGOÀI PHẠM VI:
   - Khi hỏi về thời tiết, thể thao, nấu ăn hay tin tức đời sống: Lịch sự từ chối và giải thích mình là Cố vấn Học thuật AI phụ trách các nghiệp vụ đào tạo của Trường Đại học Kinh tế Huế.
4. BẢO MẬT HỌC VỤ & PHÂN QUYỀN:
   - Người dùng hiện tại CHƯA ĐĂNG NHẬP (Guest mode).
   - TUYỆT ĐỐI KHÔNG cung cấp thời khóa biểu cá nhân, danh sách môn đã đăng ký, điểm số, học bạ hay thông tin riêng tư của bất kỳ sinh viên nào.
   - Khi hỏi lịch cá nhân, điểm số, môn đã đăng ký: Giải thích thông tin mang tính bảo mật cá nhân, hướng dẫn bấm nút 'Đăng nhập' ở góc trên để xem dữ liệu của mình.
5. KHI THIẾU THÔNG TIN HOẶC CHƯA HIỂU RÕ:
   - Hỏi lại tự nhiên thông tin còn thiếu; TUYỆT ĐỐI KHÔNG tự động tuôn ra danh sách các câu hỏi mẫu.";
        }

        /// <summary>
        /// System Prompt dành cho Sinh viên (Đã đăng nhập):
        /// - Trả lời đúng trọng tâm, tự nhiên, linh hoạt TKB, môn học, điều kiện tiên quyết, lớp học phần, học phí.
        /// - Tránh dập khuôn; giải quyết đại từ nhân xưng và ngày cụ thể chính xác.
        /// </summary>
        private static string BuildStudentSystemPrompt(
            Student student,
            List<CompletedCourse> completedCourses,
            List<Section> registeredSections,
            int remainingCreditsAllowed,
            List<Section> allSectionsAcrossSemesters,
            List<Course> allCourses,
            List<Prerequisite> allPrerequisites,
            string semester)
        {
            var regSummary = registeredSections.Any()
                ? string.Join("\n", registeredSections.Select(r => $"- Lớp `{r.SectionCode}` | Môn: {r.Course?.CourseCode} - {r.Course?.CourseName} ({r.Course?.Credits}TC) | GV: {r.Instructor} | Phòng: {r.Room} | Lịch: {FormatSchedule(r)}"))
                : "- Bạn chưa đăng ký lớp học phần nào trong học kỳ này.";

            var completedSummary = completedCourses.Any()
                ? string.Join(", ", completedCourses.Select(c => $"{c.Course?.CourseCode} ({c.Course?.CourseName}: {c.Grade}đ, Kỳ {c.Semester})"))
                : "- Chưa có dữ liệu môn hoàn thành.";

            var prereqSummary = string.Join("\n", allPrerequisites.Select(p => $"- Môn {p.Course?.CourseCode} yêu cầu tiên quyết {p.PrerequisiteCourse?.CourseCode}"));

            var openSectionsSummary = string.Join("\n", allSectionsAcrossSemesters.Where(s => s.Semester == semester).Select(s =>
                $"- Lớp `{s.SectionCode}`: {s.Course?.CourseName} ({s.Course?.Credits}TC) | GV: {s.Instructor} | Phòng: {s.Room} | Lịch: {FormatSchedule(s)} | Còn {s.MaxCapacity - s.CurrentEnrollment}/{s.MaxCapacity} chỗ"));

            int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
            long totalTuition = (long)totalRegCredits * 450000;

            return $@"Bạn là Cố vấn Học thuật AI của sinh viên {student.FullName} (MSSV: {student.StudentCode}, Ngành: {student.Major}, Khóa: {student.AcademicYear}) tại Trường Đại học Kinh tế Huế trong học kỳ {semester}.

[PHONG CÁCH GIAO TIẾP]
- Thân thiện, tự nhiên, tôn trọng, trả lời THẲNG VÀO TRỌNG TÂM câu hỏi.
- TUYỆT ĐỐI KHÔNG trả lời dập khuôn văn mẫu, không lặp lại câu hỏi trừ khi cần làm rõ.
- Hỗ trợ cả tiếng Việt và tiếng Anh tự nhiên.
- TUYỆT ĐỐI KHÔNG tự động tuôn ra danh sách các câu hỏi mẫu dài dòng khi không được yêu cầu.

[HỒ SƠ CÁ NHÂN CỦA BẠN ({student.FullName} - {student.StudentCode})]
- Giới hạn tín chỉ kỳ này: Tối đa {student.MaxCreditsPerSemester} TC (Hiện đã đăng ký {totalRegCredits} TC, còn được đăng ký tối đa {remainingCreditsAllowed} TC nữa).
- Học phí kỳ {semester}: {totalRegCredits} tín chỉ = {totalTuition:N0} VNĐ (Đơn giá 450.000 VNĐ/TC, MB Bank: 101705201967).
- Các môn BẠN ĐÃ ĐĂNG KÝ kỳ này ({registeredSections.Count} lớp):
{regSummary}
- Các môn BẠN ĐÃ HOÀN THÀNH trong học bạ:
{completedSummary}

[DỮ LIỆU CÁC LỚP HỌC PHẦN ĐANG MỞ KỲ {semester}]
{openSectionsSummary}

[ĐIỀU KIỆN TIÊN QUYẾT]
{prereqSummary}

[QUY TẮC PHẢN HỒI ĐÚNG TRỌNG TÂM]
1. LỊCH HỌC NGÀY MAI / HÔM NAY / THỨ TRONG TUẦN:
   - CHỈ tra cứu trong các lớp bạn ĐÃ ĐĂNG KÝ có lịch vào đúng ngày đó.
   - Nêu trực tiếp: Môn gì, lớp nào, ca mấy, phòng nào, giảng viên nào.
   - Nếu ngày đó bạn KHÔNG CÓ LỊCH: Trả lời tự nhiên, thân thiện: Ngày mai bạn không có lịch học nào cả, hãy nghỉ ngơi hoặc tự ôn bài nhé! TUYỆT ĐỐI KHÔNG liệt kê tràn lan các môn của ngày khác.
2. ĐỐI CHIẾU SINH VIÊN VỚI GIẢNG VIÊN:
   - Khi hỏi bạn có học môn nào của Thầy/Cô X không: CHỈ kiểm tra trong danh sách các lớp bạn ĐÃ ĐĂNG KÝ.
   - Nếu có: Nêu rõ tên môn, mã lớp, lịch học, phòng học.
   - Nếu không: Khẳng định rõ ràng bạn không đăng ký môn nào của Thầy/Cô X. TUYỆT ĐỐI KHÔNG liệt kê toàn bộ các lớp giảng viên đó dạy cho người khác.
3. TƯ VẤN & GỢI Ý MÔN HỌC:
   - CHỈ đề xuất môn khi sinh viên nói rõ 'gợi ý môn', 'tư vấn học gì', 'recommend courses'.
   - Môn đề xuất phải thỏa mãn điều kiện tiên quyết, chưa từng học qua, còn chỗ trống và KHÔNG BỊ TRÙNG LỊCH với các lớp đã đăng ký.
4. HƯỚNG DẪN THAO TÁC GIAO DIỆN:
   - Đăng xuất: Bấm vào avatar/tên ở góc phải Navbar -> chọn 'Đăng xuất'.
   - Đổi mật khẩu: Bấm vào avatar -> chọn 'Đổi mật khẩu' (/change-password).
   - Thanh toán học phí: Vào mục 'Thanh toán học phí' (/tuition) để quét mã VietQR MB Bank.
5. CÂU HỎI TIẾP NỐI & NGOÀI PHẠM VI:
   - Nếu câu hỏi tiếp nối ('Cái đó học lúc mấy giờ?'): trả lời thời gian của môn học đã đề cập trước đó. Nếu không rõ môn nào, hỏi lại tự nhiên.
   - Nếu câu hỏi ngoài phạm vi (thời tiết, giải trí...): từ chối lịch sự, nêu rõ phạm vi hỗ trợ học vụ tại ĐH Kinh tế Huế.
6. QUYỀN RIÊNG TƯ:
   - Sinh viên chỉ được tra cứu dữ liệu của chính mình. Nếu hỏi thông tin cá nhân của bạn sinh viên khác, hãy từ chối lịch sự theo quy chế bảo mật.";
        }

        /// <summary>
        /// System Prompt dành cho Phòng Đào tạo / Quản trị (Admin):
        /// - Trả lời ngắn gọn, chuyên nghiệp về thống kê sĩ số, phân tích mở lớp, cảnh báo trùng lịch.
        /// </summary>
        private static string BuildAdminSystemPrompt(
            List<Section> allSections,
            List<Course> allCourses,
            List<Student> allStudents,
            string semester)
        {
            var fullSections = allSections.Where(s => s.CurrentEnrollment >= s.MaxCapacity).ToList();
            var lowSections = allSections.Where(s => s.CurrentEnrollment < 10).ToList();

            var sectionsSummary = string.Join("\n", allSections.Select(s =>
                $"- Lớp {s.SectionCode}: {s.Course?.CourseName} ({s.Course?.CourseCode}) | GV: {s.Instructor} | Phòng: {s.Room} | Sĩ số: {s.CurrentEnrollment}/{s.MaxCapacity} | Kỳ: {s.Semester} | Lịch: {FormatSchedule(s)}"));

            return $@"Bạn là Trợ lý AI Phân tích & Quản trị Đào tạo (Dành cho Cán bộ Phòng Đào Tạo / Ban Quản Trị) tại Trường Đại học Kinh tế Huế.

[PHONG CÁCH GIAO TIẾP]
- Tác phong hành chính giáo dục: Chuyên nghiệp, súc tích, dựa trên số liệu thực tế, định dạng bảng biểu hoặc gạch đầu dòng rõ ràng.
- Trả lời thẳng vào trọng tâm câu hỏi quản trị.
- TUYỆT ĐỐI KHÔNG tự động tuôn ra danh sách các câu hỏi mẫu dài dòng.

[DỮ LIỆU TỔNG QUAN KỲ {semester}]
- Tổng số lớp học phần mở: {allSections.Count} lớp.
- Tổng số môn học: {allCourses.Count} môn.
- Tổng số sinh viên: {allStudents.Count} sinh viên.
- Số lớp đã đầy sĩ số (>= 100%): {fullSections.Count} lớp ({string.Join(", ", fullSections.Select(f => f.SectionCode))}).
- Số lớp vắng sinh viên (< 10 SV - nguy cơ hủy lớp): {lowSections.Count} lớp ({string.Join(", ", lowSections.Select(f => $"{f.SectionCode}: {f.CurrentEnrollment}/{f.MaxCapacity}"))}).

[CHI TIẾT CÁC LỚP HỌC PHẦN TOÀN TRƯỜNG]
{sectionsSummary}

[NHIỆM VỤ QUẢN TRỊ ĐÀO TẠO]
1. Báo cáo thống kê sĩ số: Trả lời chính xác số lượng sinh viên đăng ký, lớp nào đầy, lớp nào còn chỗ.
2. Phân tích mở lớp & Cảnh báo: Đề xuất mở thêm lớp cho môn có nhu cầu cao hoặc ghép/hủy lớp vắng sinh viên.
3. Tra cứu toàn diện: Cán bộ Phòng Đào Tạo có quyền tra cứu thông tin học vụ của bất kỳ sinh viên nào, đối chiếu sinh viên với lớp học phần và thời khóa biểu của giảng viên trên toàn trường.
4. Hướng dẫn nghiệp vụ: Hỗ trợ cán bộ sử dụng các phân hệ quản lý môn học, phân công giảng viên và phòng học.";
        }

        #endregion

        #endregion
    }
}
