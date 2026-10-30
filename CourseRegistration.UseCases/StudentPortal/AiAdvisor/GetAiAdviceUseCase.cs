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
        private readonly AiAdvisorSettings _aiSettings;

        public GetAiAdviceUseCase(
            IStudentRepository studentRepository,
            ISectionRepository sectionRepository,
            IEnrollmentRepository enrollmentRepository,
            ICourseRepository courseRepository,
            IChatClient? chatClient = null,
            ICurrentUserStateStore? currentUserStateStore = null,
            IRegistrationPeriodService? registrationPeriodService = null,
            AiAdvisorSettings? aiSettings = null)
        {
            _studentRepository = studentRepository;
            _sectionRepository = sectionRepository;
            _enrollmentRepository = enrollmentRepository;
            _courseRepository = courseRepository;
            _chatClient = chatClient;
            _currentUserStateStore = currentUserStateStore;
            _registrationPeriodService = registrationPeriodService;
            // Timeout đọc từ cấu hình AI:TimeoutSeconds (mặc định 45s), không hard-code
            _aiSettings = aiSettings ?? new AiAdvisorSettings();
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
                var allSemesterEnrollments = (await _enrollmentRepository.GetEnrollmentsBySemesterAsync(semester)).ToList();

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

                // Nơi gọi (trang /ai-advisor, chat box) có thể đã thêm câu hỏi hiện tại vào lịch sử TRƯỚC khi gọi UseCase.
                // Bỏ phần tử cuối nếu trùng để câu hỏi không bị gửi hai lần cho LLM / không làm lệch ngữ cảnh hội thoại.
                conversationHistory = RemoveTrailingDuplicateUserPrompt(conversationHistory, userPrompt);

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
                bool llmFailed = false;
                if (_chatClient != null && !string.IsNullOrWhiteSpace(userPrompt))
                {
                    using var llmCts = new CancellationTokenSource();
                    try
                    {
                        // Phân quyền System Prompt chuyên biệt theo 3 vai trò: Khách, Sinh viên, Quản trị
                        string systemPrompt;
                        if (currentRole == "Admin")
                        {
                            systemPrompt = BuildAdminSystemPrompt(allSectionsAcrossSemesters, allCourses, allStudents, semester, allSemesterEnrollments);
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
                                semester,
                                allSemesterEnrollments);
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

                        // Timeout lấy từ cấu hình AI:TimeoutSeconds (LLM local như Ollama cần nhiều giây để phản hồi)
                        llmCts.CancelAfter(_aiSettings.Timeout);
                        var response = await _chatClient.GetResponseAsync(messages, new ChatOptions { Temperature = 0.2f }, llmCts.Token);
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
                        // Tuyệt đối không log thông tin nhạy cảm hoặc API key (chỉ log loại lỗi + message đã cắt ngắn)
                        AiDiagnostics.LogLlmFailure("AiAdvisor", ex, llmCts.IsCancellationRequested, _aiSettings.Timeout);
                        aiSuccess = false;
                        llmFailed = true;
                    }
                }

                // 5. ĐỘNG CƠ CỐ VẤN TRI THỨC SQL SERVER CHUYÊN SÂU (FALLBACK / CORE ENGINE KHI OFFLINE / KHÔNG CÓ API KEY)
                // Đảm bảo hiểu câu hỏi tiếng Việt & tiếng Anh, xác định đúng ý định, truy vấn chính xác SQL Server!
                if (!aiSuccess)
                {
                    advice.IsAiGenerated = false;
                    advice.ModelUsed = "Động Cơ Tri Thức SQL Server Chuyên Sâu";
                    advice.WarningMessage = "💡 Cố vấn AI đang hoạt động ở chế độ Động cơ Tri thức Học thuật Chuyên sâu (truy vấn trực tiếp dữ liệu thực tế từ SQL Server). Kết quả được trích xuất chính xác theo thời gian thực.";
                    if (llmFailed)
                    {
                        advice.WarningMessage += " (Mô hình AI không phản hồi kịp hoặc không kết nối được nên hệ thống tự động dùng chế độ dự phòng này.)";
                    }

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
                        _enrollmentRepository,
                        _studentRepository,
                        allSemesterEnrollments);
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
            IEnrollmentRepository enrollmentRepository,
            IStudentRepository? studentRepository = null,
            List<Enrollment>? allSemesterEnrollments = null)
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

            // 9. Phản hồi giao tiếp Yes/No hoặc Có/Không từ người dùng (Conversational Affirmation / Negation)
            if (IsConversationalAffirmationOrNegationIntent(qNorm, qLower, out bool isAffirmative))
            {
                advice.AiMessage = BuildConversationalYesNoResponse(isAffirmative, isEnglish, currentStudent, semester, context);
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
            // BƯỚC 1.5: ĐỊNH NGHĨA CÁC Ý ĐỊNH HỌC VỤ NGỮ NGHĨA (SEMANTIC INTENTS)
            // =========================================================================
            bool asksPaymentStatus = qNorm.Contains("dong chua") || qNorm.Contains("nop chua") || 
                                     qNorm.Contains("thanh toan chua") || qNorm.Contains("da dong") || 
                                     qNorm.Contains("da nop") || qNorm.Contains("chua dong") || 
                                     qNorm.Contains("chua nop") || qNorm.Contains("con no") || 
                                     qNorm.Contains("no hoc phi") || qNorm.Contains("no tien") || 
                                     qNorm.Contains("tinh trang hoc phi") || qNorm.Contains("trang thai hoc phi") ||
                                     qNorm.Contains("kiem tra hoc phi") || qNorm.Contains("hoan tat hoc phi") ||
                                     qNorm.Contains("dong tien chua") || qNorm.Contains("nop tien chua") ||
                                     qNorm.Contains("dong hoc phi chua") || qNorm.Contains("nop hoc phi chua") ||
                                     qLower.Contains("paid yet") || qLower.Contains("has paid") || 
                                     qLower.Contains("is paid") || qLower.Contains("unpaid") || 
                                     qLower.Contains("payment status") || qLower.Contains("tuition status") ||
                                     (qNorm.Contains("hoc phi") && (qNorm.Contains("chua") || qNorm.Contains("roi") || qNorm.Contains("xong")));

            bool isTuition = qNorm.Contains("hoc phi") || (qNorm.Contains("tien hoc") && !qNorm.Contains("dau tien")) || qNorm.Contains("gia tin chi") ||
                             qNorm.Contains("dong tien") || qNorm.Contains("thanh toan") || qNorm.Contains("gia bao nhieu") ||
                             qNorm.Contains("bao nhieu tien") || qNorm.Contains("don gia") || qNorm.Contains("chuyen tien") ||
                             qNorm.Contains("nop tien") || qNorm.Contains("ngan hang") || qNorm.Contains("stk") ||
                             qNorm.Contains("so tai khoan") || qNorm.Contains("vietqr") || qNorm.Contains("mb bank") ||
                             qNorm.Contains("chuyen khoan") || qLower.Contains("tuition") || qLower.Contains("fee") ||
                             qLower.Contains("payment") || qLower.Contains("how much is") || asksPaymentStatus;

            bool isStudentProfileQuery =
                (matchedExplicitStudent != null || (currentStudent != null && (qNorm.Contains("thong tin cua toi") || qNorm.Contains("ho so cua toi") || qNorm.Contains("thong tin ca nhan") || qNorm.Contains("mssv cua toi") || qNorm.Contains("nganh cua toi")))) &&
                (qNorm.Contains("thong tin") || qNorm.Contains("nganh gi") || qNorm.Contains("nganh nao") ||
                 qNorm.Contains("nganh hoc") || qNorm.Contains("chuyen nganh") || qNorm.Contains("khoa may") ||
                 qNorm.Contains("khoa hoc") || qNorm.Contains("la ai") || qNorm.Contains("email") ||
                 qNorm.Contains("so dien thoai") || qNorm.Contains("sdt") || qNorm.Contains("mssv") ||
                 qNorm.Contains("han muc") || qLower.Contains("profile") || qLower.Contains("who is") ||
                 qLower.Contains("student info") || qLower.Contains("major"));

            bool asksGrades = qNorm.Contains("diem cua em") || qNorm.Contains("diem cua toi") || qNorm.Contains("hoc ba") ||
                              qNorm.Contains("mon da hoc") || qNorm.Contains("mon da qua") || qNorm.Contains("mon tich luy") ||
                              qNorm.Contains("ket qua hoc tap") || qNorm.Contains("diem f") || qNorm.Contains("mon rot") ||
                              qNorm.Contains("bang diem") || qNorm.Contains("diem so") ||
                              qLower.Contains("my grade") || qLower.Contains("transcript") || qLower.Contains("completed course") ||
                              ((qNorm.Contains("diem") || qNorm.Contains("hoc ba") || qNorm.Contains("bang diem")) && (matchedExplicitStudent != null || hasPersonalPronoun));

            bool isAdvisoryRequest = IsExplicitRecommendationRequest(qNorm);

            bool isRegisteredCoursesQuery =
                !isTuition && !isStudentProfileQuery && !asksGrades && !isAdvisoryRequest &&
                IsRegisteredCoursesIntent(qNorm, qLower, hasPersonalPronoun, null);

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
            }
            else if (hasPersonalPronoun || queriedDay.HasValue || isWeekQuery || isRegisteredCoursesQuery || IsPersonalRegistrationQuery(qNorm, qLower))
            {
                // Sử dụng đại từ nhân xưng hoặc hỏi lịch cá nhân / môn đăng ký -> Gán về sinh viên đang đăng nhập
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

            // Cập nhật lại isRegisteredCoursesQuery khi đã biết targetStudent
            if (!isRegisteredCoursesQuery && !isTuition && !isStudentProfileQuery && !asksGrades && !isAdvisoryRequest)
            {
                isRegisteredCoursesQuery = IsRegisteredCoursesIntent(qNorm, qLower, hasPersonalPronoun, targetStudent);
            }

            bool isPersonalSchedule =
                !isTuition && !isStudentProfileQuery && !asksGrades && !isAdvisoryRequest &&
                (queriedDay.HasValue ||
                 isWeekQuery ||
                 isRegisteredCoursesQuery ||
                 qNorm.Contains("trung lich") || qNorm.Contains("trung gio") || qLower.Contains("conflict") ||
                 qNorm.Contains("thoi khoa bieu") || qNorm.Contains("lich hoc") || qNorm.Contains("lich cua toi") || qLower.Contains("my schedule") || qLower.Contains("my classes") ||
                 (matchedCourse != null && (hasPersonalPronoun || targetStudent != null))
                );

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
                        sb.AppendLine($"✅ **YES.** Student **{targetStudent.FullName}** is currently enrolled in **{matchedCrossSections.Count} class(es)** taught by **{matchedInstructorName}**:");
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
                        sb.AppendLine($"❌ **NO.** Student **{targetStudent.FullName}** has **NOT registered for any courses/classes** taught by **{matchedInstructorName}** in semester {semester}.");
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
                        sb.AppendLine($"✅ **CÓ.** Sinh viên **{targetStudent.FullName}** hiện **CÓ ĐĂNG KÝ {matchedCrossSections.Count} lớp học phần** do giảng viên **{matchedInstructorName}** phụ trách:");
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
                        sb.AppendLine($"❌ **KHÔNG.** Sinh viên **{targetStudent.FullName}** **KHÔNG ĐĂNG KÝ môn học/lớp học phần nào** do **{matchedInstructorName}** phụ trách trong học kỳ {semester}.");
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

            if (isFollowUpQuery && !isTuition && !isStudentProfileQuery && !asksGrades && !isRegisteredCoursesQuery && (!queriedDay.HasValue || context.HasContext))
            {
                if (context.HasContext)
                {
                    int? ordinal = DetectOrdinalPosition(qNorm, qLower);
                    Section? targetSec = null;
                    if (ordinal.HasValue && context.LastMentionedSections.Any())
                    {
                        if (ordinal.Value == -1)
                        {
                            targetSec = context.LastMentionedSections.Last();
                        }
                        else if (ordinal.Value < context.LastMentionedSections.Count)
                        {
                            targetSec = context.LastMentionedSections[ordinal.Value];
                        }
                    }

                    if (targetSec == null)
                    {
                        targetSec = matchedSection 
                                    ?? context.LastReferencedSection
                                    ?? (context.LastMentionedSections.Count == 1 ? context.LastMentionedSections.First() : null);
                    }

                    // Ca 2.0: Hỏi về các môn học khác ("Còn môn nào khác?", "Các môn còn lại?", "What else?", "Any other courses?")
                    bool asksOtherCourses = qNorm.Contains("con mon nao") || qNorm.Contains("mon khac") || 
                                            qNorm.Contains("cac mon con lai") || qNorm.Contains("con gi nua") ||
                                            qNorm.Contains("con lop nao") || qNorm.Contains("nhung mon con lai") ||
                                            qLower.Contains("what else") || qLower.Contains("any other");

                    if (asksOtherCourses)
                    {
                        var studentReg = (currentStudent != null && registeredSections.Any())
                            ? registeredSections
                            : context.LastMentionedSections;

                        var otherSecs = (targetSec != null)
                            ? studentReg.Where(s => s.SectionId != targetSec.SectionId).ToList()
                            : studentReg;

                        var sb = new StringBuilder();
                        if (isEnglish)
                        {
                            sb.AppendLine("### 📋 Other Registered Courses in Your Timetable:");
                            if (targetSec != null)
                            {
                                sb.AppendLine($"Besides **{targetSec.Course?.CourseName}** (`{targetSec.SectionCode}`), you have **{otherSecs.Count} other enrolled class(es)** for semester {semester}:");
                            }
                            else
                            {
                                sb.AppendLine($"You have **{otherSecs.Count} other enrolled class(es)** for semester {semester}:");
                            }
                            sb.AppendLine();
                            if (otherSecs.Any())
                            {
                                foreach (var s in otherSecs)
                                {
                                    sb.AppendLine($"- **Class `{s.SectionCode}`** — **{s.Course?.CourseName}** ({s.Course?.Credits} Credits):");
                                    sb.AppendLine($"  + Instructor: **{s.Instructor}** | Room: **{s.Room}**");
                                    sb.AppendLine($"  + Schedule: **{FormatScheduleDetailed(s, true)}**");
                                }
                            }
                            else
                            {
                                sb.AppendLine("- *No other courses found.*");
                            }
                        }
                        else
                        {
                            sb.AppendLine($"### 📋 Các Môn Học Phần Khác Trong Thời Khóa Biểu Của Bạn:");
                            if (targetSec != null)
                            {
                                sb.AppendLine($"Ngoài lớp **{targetSec.Course?.CourseName}** (`{targetSec.SectionCode}`), bạn còn có **{otherSecs.Count} lớp học phần khác** đã đăng ký trong học kỳ {semester}:");
                            }
                            else
                            {
                                sb.AppendLine($"Bạn còn có **{otherSecs.Count} lớp học phần khác** đã đăng ký trong học kỳ {semester}:");
                            }
                            sb.AppendLine();
                            if (otherSecs.Any())
                            {
                                foreach (var s in otherSecs)
                                {
                                    sb.AppendLine($"- **Lớp `{s.SectionCode}`** — **{s.Course?.CourseName}** ({s.Course?.Credits} TC):");
                                    sb.AppendLine($"  + Giảng viên: **{s.Instructor}** | Phòng: **{s.Room}**");
                                    sb.AppendLine($"  + Lịch học: **{FormatScheduleDetailed(s, false)}**");
                                }
                            }
                            else
                            {
                                sb.AppendLine("- *Không còn môn học phần nào khác trong danh sách.*");
                            }
                        }

                        advice.AiMessage = sb.ToString();
                        return;
                    }

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

                    if (asksWhoTeaches && (targetSec != null || context.LastReferencedCourse != null || context.LastMentionedSections.Any()))
                    {
                        if (targetSec != null)
                        {
                            context.LastReferencedSection = targetSec;
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

                    // Ca 2.3: "Cái đó học lúc mấy giờ?", "Môn đó học ca mấy?", "Mấy giờ?", "What time does that class start?"
                    bool asksTimeOfClass = qNorm.Contains("luc may gio") || qNorm.Contains("may gio") || qNorm.Contains("ca may") ||
                                           qNorm.Contains("tiet may") || qNorm.Contains("thoi gian") ||
                                           qLower.Contains("what time") || qLower.Contains("when is it") || qLower.Contains("which period");

                    if (asksTimeOfClass && (targetSec != null || context.LastReferencedCourse != null || context.LastMentionedSections.Any()))
                    {
                        if (targetSec != null)
                        {
                            context.LastReferencedSection = targetSec;
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
                    bool asksRoomOfClass = qNorm.Contains("o dau") || qNorm.Contains("phong nao") || qNorm.Contains("phong may") ||
                                           qLower.Contains("which room") || qLower.Contains("where is it") || qLower.Contains("where do i study");
                    if (asksRoomOfClass && (targetSec != null || context.LastReferencedCourse != null || context.LastMentionedSections.Any()))
                    {
                        if (targetSec != null)
                        {
                            context.LastReferencedSection = targetSec;
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
            // Ví dụ: "Ngày mai tôi học gì?", "Thứ 2 tôi học gì?", "Tôi đã đăng ký môn nào?", "Do I have any classes tomorrow?"
            // =========================================================================
            if (isPersonalSchedule)
            {
                var effectiveStudent = targetStudent ?? currentStudent;
                if (effectiveStudent == null)
                {
                    advice.AiMessage = isEnglish
                        ? "👋 **Hello! You are currently in Guest mode (not logged in).**\n\nTo view your personalized schedule and enrolled courses, please log in with your student account."
                        : "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\nĐể Cố vấn AI tra cứu thời khóa biểu cá nhân của bạn, vui lòng bấm nút **Đăng nhập** bên dưới nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                var effectiveRegisteredSections = effectiveStudent.StudentId == currentStudent?.StudentId
                    ? registeredSections
                    : GetRegisteredSectionsForStudent(effectiveStudent.StudentId, semester, enrollmentRepository);

                // Sub-case 3.1: Hỏi về một ngày cụ thể (Hôm nay / Ngày mai / Thứ 2 - CN)
                if (queriedDay.HasValue)
                {
                    int day = queriedDay.Value;
                    string dayNameVi = day == 8 ? "Chủ Nhật" : $"Thứ {day}";
                    string dayNameEn = GetDayNameEnglish(day);

                    var daySections = effectiveRegisteredSections.Where(s =>
                        s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == day)).ToList();

                    bool isYn = IsYesNoQuery(qNorm, qLower);
                    var sb = new StringBuilder();
                    if (isEnglish)
                    {
                        sb.AppendLine($"### 🗓️ Personal Class Schedule for {dayNameEn} (Semester {semester}):");
                        sb.AppendLine($"Student: **{effectiveStudent.FullName}** (ID: `{effectiveStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (daySections.Any())
                        {
                            string prefix = isYn ? "✅ **YES.** " : "";
                            sb.AppendLine($"{prefix}On **{dayNameEn}**, you have **{daySections.Count} registered class(es)**:");
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
                            string prefix = isYn ? "❌ **NO.** " : "✅ ";
                            sb.AppendLine($"{prefix}On **{dayNameEn}**, you have **no scheduled classes** among your registered courses.");
                            sb.AppendLine($"💡 You can use this time for self-study or review!");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"### 🗓️ Lịch Học Cá Nhân Vào {dayNameVi} (Học kỳ {semester}):");
                        sb.AppendLine($"Sinh viên: **{effectiveStudent.FullName}** (MSSV: `{effectiveStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (daySections.Any())
                        {
                            string prefix = isYn ? "✅ **CÓ.** " : "";
                            sb.AppendLine($"{prefix}Vào **{dayNameVi}**, bạn có **{daySections.Count} lớp học phần** đã đăng ký:");
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
                            string prefix = isYn ? "❌ **KHÔNG.** " : "✅ ";
                            sb.AppendLine($"{prefix}Vào **{dayNameVi}**, bạn **không có lịch học nào** trong số các lớp học phần đã đăng ký.");
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
                        sb.AppendLine($"### 🗓️ Weekly Schedule for Student {effectiveStudent.FullName} (`{effectiveStudent.StudentCode}`) — Semester {semester}:");
                        sb.AppendLine();
                        for (int d = 2; d <= 8; d++)
                        {
                            string dName = GetDayNameEnglish(d);
                            var dSecs = effectiveRegisteredSections.Where(s => s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == d)).ToList();
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
                        sb.AppendLine($"### 🗓️ Thời Khóa Biểu Tuần Của Sinh Viên: **{effectiveStudent.FullName}** (`{effectiveStudent.StudentCode}`) — Học kỳ {semester}:");
                        sb.AppendLine();
                        for (int d = 2; d <= 8; d++)
                        {
                            string dName = d == 8 ? "Chủ Nhật" : $"Thứ {d}";
                            var dSecs = effectiveRegisteredSections.Where(s => s.Schedules != null && s.Schedules.Any(sch => sch.DayOfWeek == d)).ToList();
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

                // Sub-case 3.3: Hỏi về môn học cụ thể hoặc toàn bộ thời khóa biểu / danh sách môn đã đăng ký
                {
                    if (matchedCourse != null)
                    {
                        var regForCourse = effectiveRegisteredSections.Where(s => s.CourseId == matchedCourse.CourseId).ToList();
                        var sbCourseCheck = new StringBuilder();
                        if (isEnglish)
                        {
                            sbCourseCheck.AppendLine($"### 📋 Personal Course Enrollment Check ({semester}):");
                            sbCourseCheck.AppendLine($"Student: **{effectiveStudent.FullName}** (`{effectiveStudent.StudentCode}`)");
                            sbCourseCheck.AppendLine();
                            if (regForCourse.Any())
                            {
                                sbCourseCheck.AppendLine($"✅ **YES.** You are currently enrolled in **{matchedCourse.CourseName}** (`{matchedCourse.CourseCode}` - {matchedCourse.Credits} Credits):");
                                sbCourseCheck.AppendLine();
                                foreach (var s in regForCourse)
                                {
                                    sbCourseCheck.AppendLine($"- Class `{s.SectionCode}`: {FormatScheduleDetailed(s, true)} | Room: {s.Room ?? "TBD"} | Lecturer: {s.Instructor}");
                                }
                            }
                            else
                            {
                                sbCourseCheck.AppendLine($"❌ **NO.** You have **NOT registered** for **{matchedCourse.CourseName}** (`{matchedCourse.CourseCode}`) in semester {semester}.");
                            }
                        }
                        else
                        {
                            sbCourseCheck.AppendLine($"### 📋 Kiểm Tra Đăng Ký Học Phần Cá Nhân (Học kỳ {semester}):");
                            sbCourseCheck.AppendLine($"Sinh viên: **{effectiveStudent.FullName}** (MSSV: `{effectiveStudent.StudentCode}`)");
                            sbCourseCheck.AppendLine();
                            if (regForCourse.Any())
                            {
                                sbCourseCheck.AppendLine($"✅ **CÓ.** Bạn hiện **CÓ ĐĂNG KÝ môn {matchedCourse.CourseName}** (Mã môn: `{matchedCourse.CourseCode}` — {matchedCourse.Credits} Tín chỉ):");
                                sbCourseCheck.AppendLine();
                                foreach (var s in regForCourse)
                                {
                                    sbCourseCheck.AppendLine($"- Mã lớp `{s.SectionCode}`: Lịch học **{FormatScheduleDetailed(s, false)}** | Phòng: **{s.Room ?? "Chưa xếp phòng"}** | Giảng viên: **{s.Instructor}**");
                                }
                            }
                            else
                            {
                                sbCourseCheck.AppendLine($"❌ **KHÔNG.** Bạn **CHƯA ĐĂNG KÝ môn {matchedCourse.CourseName}** (`{matchedCourse.CourseCode}`) trong học kỳ {semester}.");
                            }
                        }
                        advice.AiMessage = sbCourseCheck.ToString();
                        return;
                    }

                    bool asksConflict =
                        qNorm.Contains("trung lich") || qNorm.Contains("trung tiet") || qNorm.Contains("trung gio") ||
                        qNorm.Contains("bi trung") || qNorm.Contains("co trung") || qNorm.Contains("trung khong") ||
                        (qNorm.Contains("trung") && (qNorm.Contains("lich") || qNorm.Contains("tiet") || qNorm.Contains("gio"))) ||
                        qLower.Contains("conflict") || qLower.Contains("overlap");
                    if (asksConflict)
                    {
                        var conflicts = FindScheduleConflicts(effectiveRegisteredSections);
                        var sbConflict = new StringBuilder();
                        if (isEnglish)
                        {
                            sbConflict.AppendLine($"### 🗓️ Timetable Conflict Verification ({semester}):");
                            sbConflict.AppendLine($"Student: **{effectiveStudent.FullName}** (`{effectiveStudent.StudentCode}`)");
                            sbConflict.AppendLine();
                            if (conflicts.Any())
                            {
                                sbConflict.AppendLine($"⚠️ **YES.** A timetable conflict was detected between registered sections:");
                                foreach (var c in conflicts)
                                {
                                    sbConflict.AppendLine($"- Class `{c.Sec1.SectionCode}` ({c.Sec1.Course?.CourseName}) overlaps with `{c.Sec2.SectionCode}` ({c.Sec2.Course?.CourseName}) on Day {c.DayOfWeek}, Periods {c.StartPeriod}-{c.EndPeriod}.");
                                }
                            }
                            else
                            {
                                sbConflict.AppendLine($"✅ **NO.** Your timetable has **NO schedule conflicts** among your {effectiveRegisteredSections.Count} registered classes.");
                            }
                        }
                        else
                        {
                            sbConflict.AppendLine($"### 🗓️ Kết Quả Kiểm Tra Trùng Lịch Thời Khóa Biểu (Học kỳ {semester}):");
                            sbConflict.AppendLine($"Sinh viên: **{effectiveStudent.FullName}** (MSSV: `{effectiveStudent.StudentCode}`)");
                            sbConflict.AppendLine();
                            if (conflicts.Any())
                            {
                                sbConflict.AppendLine($"⚠️ **CÓ.** Phát hiện trùng lịch giữa các lớp học phần đã đăng ký:");
                                foreach (var c in conflicts)
                                {
                                    string dayName = c.DayOfWeek == 8 ? "Chủ Nhật" : $"Thứ {c.DayOfWeek}";
                                    sbConflict.AppendLine($"- Lớp `{c.Sec1.SectionCode}` ({c.Sec1.Course?.CourseName}) bị trùng với lớp `{c.Sec2.SectionCode}` ({c.Sec2.Course?.CourseName}) vào **{dayName}** (Tiết {c.StartPeriod} - {c.EndPeriod}).");
                                }
                            }
                            else
                            {
                                sbConflict.AppendLine($"✅ **KHÔNG.** Thời khóa biểu của bạn hoàn toàn hợp lệ, **KHÔNG BỊ TRÙNG LỊCH** giữa {effectiveRegisteredSections.Count} lớp học phần đã đăng ký.");
                            }
                        }
                        advice.AiMessage = sbConflict.ToString();
                        return;
                    }

                    var sb = new StringBuilder();
                    if (isEnglish)
                    {
                        sb.AppendLine($"### 📋 Your Registered Courses for Semester {semester}:");
                        sb.AppendLine($"Student: **{effectiveStudent.FullName}** (ID: `{effectiveStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (effectiveRegisteredSections.Any())
                        {
                            int totalRegCredits = effectiveRegisteredSections.Sum(s => s.Course?.Credits ?? 0);
                            sb.AppendLine($"You have successfully enrolled in **{effectiveRegisteredSections.Count} classes** (Total: **{totalRegCredits} Credits**):");
                            sb.AppendLine();

                            foreach (var s in effectiveRegisteredSections)
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
                        sb.AppendLine($"Sinh viên: **{effectiveStudent.FullName}** (MSSV: `{effectiveStudent.StudentCode}`)");
                        sb.AppendLine();

                        if (effectiveRegisteredSections.Any())
                        {
                            int totalRegCredits = effectiveRegisteredSections.Sum(s => s.Course?.Credits ?? 0);
                            sb.AppendLine($"Bạn đã đăng ký thành công **{effectiveRegisteredSections.Count} lớp học phần** (Tổng số: **{totalRegCredits} Tín chỉ**):");
                            sb.AppendLine();

                            foreach (var s in effectiveRegisteredSections)
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
            if (asksGrades)
            {
                var targetGradeStudent = matchedExplicitStudent ?? targetStudent ?? currentStudent;
                if (targetGradeStudent == null)
                {
                    advice.AiMessage = isEnglish
                        ? "👋 Please log in with your student account or provide a Student ID to view academic transcripts and completed courses."
                        : "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\nĐể xem học bạ, điểm số tích lũy cá nhân, vui lòng bấm nút **Đăng nhập** nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                if (currentRole == "Student" && currentStudent != null && targetGradeStudent.StudentId != currentStudent.StudentId)
                {
                    advice.AiMessage = isEnglish
                        ? $"🔒 **Privacy Alert:** You are not authorized to view academic records of other students."
                        : $"🔒 **Thông Báo Bảo Mật:** Bạn không có quyền truy cập bảng điểm của sinh viên khác ({targetGradeStudent.FullName}).";
                    return;
                }

                var targetCompletedCourses = completedCourses;
                if (studentRepository != null && targetGradeStudent.StudentId != currentStudent?.StudentId)
                {
                    try
                    {
                        targetCompletedCourses = (studentRepository.GetCompletedCoursesByStudentIdAsync(targetGradeStudent.StudentId).GetAwaiter().GetResult()).ToList();
                    }
                    catch { }
                }

                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 📊 Academic Record & Transcripts: {targetGradeStudent.FullName} (`{targetGradeStudent.StudentCode}`)");
                    sb.AppendLine($"- **Major:** {targetGradeStudent.Major} | **Cohort:** {targetGradeStudent.AcademicYear}");
                    sb.AppendLine($"- **Max credits allowance:** **{targetGradeStudent.MaxCreditsPerSemester} Credits/semester**");
                    sb.AppendLine();

                    if (targetCompletedCourses.Any())
                    {
                        sb.AppendLine($"Completed **{targetCompletedCourses.Count} course(s)**:");
                        sb.AppendLine();
                        foreach (var c in targetCompletedCourses)
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
                    sb.AppendLine($"### 📊 Kết Quả Tích Lũy & Học Bạ Của Sinh Viên: {targetGradeStudent.FullName} (`{targetGradeStudent.StudentCode}`)");
                    sb.AppendLine($"- **Chuyên ngành:** {targetGradeStudent.Major} | **Khóa học:** {targetGradeStudent.AcademicYear}");
                    sb.AppendLine($"- **Hạn mức số tín chỉ tối đa:** **{targetGradeStudent.MaxCreditsPerSemester} Tín chỉ / kỳ**");
                    sb.AppendLine();

                    if (targetCompletedCourses.Any())
                    {
                        sb.AppendLine($"Đã hoàn thành **{targetCompletedCourses.Count} học phần** nền tảng:");
                        sb.AppendLine();
                        foreach (var c in targetCompletedCourses)
                        {
                            string status = c.Grade >= 5.0 ? "✅ Đạt" : "❌ Chưa đạt";
                            sb.AppendLine($"- **{c.Course?.CourseCode}** — {c.Course?.CourseName} ({c.Course?.Credits}TC): **{c.Grade:0.0} điểm** ({status} - Kỳ {c.Semester})");
                        }
                    }
                    else
                    {
                        sb.AppendLine("- *Chưa có dữ liệu môn học đã hoàn thành trong hệ thống.*");
                    }
                }

                advice.AiMessage = sb.ToString();
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 5: YÊU CẦU GỢI Ý / TƯ VẤN LỘ TRÌNH ĐĂNG KÝ HỌC PHẦN (CHỈ KHI YÊU CẦU)
            // =========================================================================
            // Khi câu hỏi nhắc tới MỘT MÔN CỤ THỂ (vd "tư vấn môn lập trình web") thì trả lời về đúng môn đó,
            // không rơi vào gói gợi ý nhiều môn chung chung. Khách (chưa đăng nhập) sẽ được TRƯỜNG HỢP 10 tra cứu môn.
            bool isCourseSpecificAdvisory = isAdvisoryRequest && matchedCourse != null;

            if (isAdvisoryRequest && !(isCourseSpecificAdvisory && currentStudent == null))
            {
                if (currentStudent == null)
                {
                    advice.AiMessage = isEnglish
                        ? "👋 Please log in with your student account so the AI Advisor can analyze your transcript, check prerequisites, and recommend an optimal course plan for you!"
                        : "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\nĐể Cố vấn AI có thể phân tích học bạ, kiểm tra điều kiện tiên quyết và đề xuất lộ trình môn học chính xác cho riêng bạn, vui lòng bấm nút **Đăng nhập** nhé!";
                    advice.SuggestLogin = true;
                    return;
                }

                if (isCourseSpecificAdvisory)
                {
                    GenerateCourseSpecificAdvice(
                        advice,
                        currentStudent,
                        matchedCourse!,
                        sectionsPool.Where(s => s.Semester == semester).ToList(),
                        allCourses,
                        allPrerequisites,
                        completedCourses,
                        registeredSections,
                        remainingCreditsAllowed,
                        semester,
                        isEnglish);
                    return;
                }

                GenerateCourseRecommendations(
                    advice,
                    currentStudent,
                    candidateSections,
                    remainingCreditsAllowed,
                    qNorm,
                    semester,
                    isEnglish,
                    qRaw);
                return;
            }

            // =========================================================================
            // TRƯỜNG HỢP 6: HỌC PHÍ & TÌNH TRẠNG THANH TOÁN (TUITION & PAYMENT STATUS)
            // =========================================================================
            if (isTuition)
            {
                var sb = new StringBuilder();
                var evalTuitionStudent = matchedExplicitStudent ?? targetStudent ?? currentStudent;

                if (asksPaymentStatus)
                {
                    if (evalTuitionStudent == null)
                    {
                        if (isEnglish)
                        {
                            advice.AiMessage = "👋 **You are currently browsing as a Guest (not logged in).**\n\nTo check your personalized tuition payment status (Paid vs Unpaid), please click **Login** with your student account, or specify your Full Name / Student ID (e.g. `SV21001` or `Nguyen Van Tuan`) in your question!";
                            advice.SuggestLogin = true;
                        }
                        else
                        {
                            advice.AiMessage = "👋 **Chào bạn! Bạn hiện đang ở chế độ Khách (chưa đăng nhập).**\n\nĐể Cố vấn AI kiểm tra chính xác trạng thái đóng học phí (đã đóng hay chưa đóng), vui lòng bấm nút **Đăng nhập** tài khoản sinh viên hoặc nhập kèm Họ và tên / Mã số sinh viên (ví dụ: `SV21001` hoặc `Nguyễn Văn Tuấn`) để hệ thống tra cứu CSDL cho bạn nhé!";
                            advice.SuggestLogin = true;
                        }
                        return;
                    }

                    // Tìm phiếu đăng ký của sinh viên
                    var studentEnrollment = allSemesterEnrollments?.FirstOrDefault(e => e.StudentId == evalTuitionStudent.StudentId && (e.Semester == semester || (semester == "2026_HK1" && e.Semester == "2025_HK1")) && e.Status != "Cancelled")
                        ?? allSemesterEnrollments?.FirstOrDefault(e => e.StudentId == evalTuitionStudent.StudentId && e.Status != "Cancelled");

                    if (studentEnrollment == null && enrollmentRepository != null)
                    {
                        try
                        {
                            var studentAllEnrollments = enrollmentRepository.GetEnrollmentsByStudentIdAsync(evalTuitionStudent.StudentId).GetAwaiter().GetResult();
                            studentEnrollment = studentAllEnrollments?.FirstOrDefault(e => (e.Semester == semester || (semester == "2026_HK1" && e.Semester == "2025_HK1")) && e.Status != "Cancelled")
                                ?? studentAllEnrollments?.FirstOrDefault(e => e.Status != "Cancelled");
                        }
                        catch { }
                    }

                    var targetRegSections = (evalTuitionStudent.StudentId == currentStudent?.StudentId)
                        ? registeredSections
                        : (enrollmentRepository != null 
                            ? (enrollmentRepository.GetRegisteredSectionsByStudentAsync(evalTuitionStudent.StudentId, semester).GetAwaiter().GetResult()?.ToList() ?? new List<Section>())
                            : new List<Section>());

                    int totalRegCredits = targetRegSections.Sum(s => s.Course?.Credits ?? 0);
                    if (totalRegCredits == 0 && studentEnrollment != null && studentEnrollment.TotalCredits > 0)
                    {
                        totalRegCredits = studentEnrollment.TotalCredits;
                    }

                    long totalTuition = (long)totalRegCredits * 450000;
                    if (totalTuition == 0 && studentEnrollment != null && studentEnrollment.TuitionFee > 0)
                    {
                        totalTuition = (long)studentEnrollment.TuitionFee;
                    }

                    bool isPaid = studentEnrollment?.IsPaid == true;
                    bool asksDebt = qNorm.Contains("con no") || qNorm.Contains("co no") || qNorm.Contains("no hoc phi") || qLower.Contains("owe") || qLower.Contains("debt");

                    if (isEnglish)
                    {
                        if (asksDebt)
                        {
                            if (isPaid)
                            {
                                sb.AppendLine($"❌ **NO.** Student **{evalTuitionStudent.FullName}** (`{evalTuitionStudent.StudentCode}`) **has zero outstanding tuition debt** for semester **{semester}** (fully paid {totalTuition:N0} VNĐ successfully).");
                            }
                            else
                            {
                                sb.AppendLine($"✅ **YES.** Student **{evalTuitionStudent.FullName}** (`{evalTuitionStudent.StudentCode}`) currently **owes tuition fee** for semester **{semester}**: **{totalTuition:N0} VNĐ** ({totalRegCredits} credits registered).");
                                sb.AppendLine($"- **Status:** **Unpaid**.");
                                sb.AppendLine($"- **Payment channel:** MB Bank VietQR Napas24/7 (Account: 101705201967 - TRUONG DAI HOC KINH TE HUE).");
                            }
                        }
                        else
                        {
                            if (isPaid)
                            {
                                sb.AppendLine($"✅ **PAID.** Student **{evalTuitionStudent.FullName}** (`{evalTuitionStudent.StudentCode}`) has **successfully completed** the tuition fee payment for semester **{semester}**!");
                                sb.AppendLine();
                                sb.AppendLine($"- **Registered credits:** **{totalRegCredits} Credits**");
                                sb.AppendLine($"- **Tuition paid:** **{totalTuition:N0} VNĐ** (450,000 VNĐ / credit)");
                                sb.AppendLine($"- **Payment date:** **{(studentEnrollment?.PaymentDate.HasValue == true ? studentEnrollment.PaymentDate.Value.ToString("dd/MM/yyyy HH:mm:ss") : "Recorded")}**");
                                sb.AppendLine($"- **Payment method:** **{(!string.IsNullOrEmpty(studentEnrollment?.PaymentMethod) ? studentEnrollment.PaymentMethod : "VietQR Napas247")}**");
                                sb.AppendLine($"- **Transaction reference:** **{(!string.IsNullOrEmpty(studentEnrollment?.TransactionReference) ? studentEnrollment.TransactionReference : "VQR-CONFIRMED")}**");
                                sb.AppendLine($"- **Enrollment status:** **{(studentEnrollment?.Status ?? "Confirmed")}**");
                            }
                            else
                            {
                                sb.AppendLine($"❌ **UNPAID.** Student **{evalTuitionStudent.FullName}** (`{evalTuitionStudent.StudentCode}`) **has not paid** the tuition fee for semester **{semester}** yet!");
                                sb.AppendLine();
                                sb.AppendLine($"- **Registered credits:** **{totalRegCredits} Credits**");
                                sb.AppendLine($"- **Tuition due:** **{totalTuition:N0} VNĐ** (Unit price 450,000 VNĐ / credit)");
                                sb.AppendLine($"- **Payment status:** **Unpaid**");
                                sb.AppendLine($"- **Payment instructions:** Please visit `/tuition` to scan the dynamic VietQR MB Bank code for 24/7 real-time settlement.");
                                sb.AppendLine($"- **University Account:** 101705201967 - MB Bank, Beneficiary: TRUONG DAI HOC KINH TE HUE.");
                            }
                        }
                    }
                    else
                    {
                        if (asksDebt)
                        {
                            if (isPaid)
                            {
                                sb.AppendLine($"❌ **KHÔNG.** Sinh viên **{evalTuitionStudent.FullName}** (MSSV: `{evalTuitionStudent.StudentCode}`) **không còn nợ học phí** học kỳ **{semester}** (đã thanh toán toàn bộ **{totalTuition:N0} VNĐ** thành công)!");
                            }
                            else
                            {
                                sb.AppendLine($"✅ **CÓ.** Sinh viên **{evalTuitionStudent.FullName}** (MSSV: `{evalTuitionStudent.StudentCode}`) hiện **đang còn nợ học phí** học kỳ **{semester}** là **{totalTuition:N0} VNĐ** (tương ứng **{totalRegCredits} tín chỉ** đã đăng ký)!");
                                sb.AppendLine();
                                sb.AppendLine($"- **Trạng thái:** **Chưa thanh toán (Unpaid)**.");
                                sb.AppendLine($"- **Kênh nộp tiền:** Quét mã VietQR tại trang **'Thanh toán học phí'** (`/tuition`) hoặc chuyển khoản tới MB Bank STK **101705201967** (TRƯỜNG ĐẠI HỌC KINH TẾ HUẾ).");
                            }
                        }
                        else
                        {
                            if (isPaid)
                            {
                                sb.AppendLine($"✅ **ĐÃ ĐÓNG.** Sinh viên **{evalTuitionStudent.FullName}** (MSSV: `{evalTuitionStudent.StudentCode}`) đã **hoàn tất thanh toán học phí** học kỳ **{semester}**!");
                                sb.AppendLine();
                                sb.AppendLine($"- **Số tín chỉ đã đăng ký:** **{totalRegCredits} Tín chỉ**");
                                sb.AppendLine($"- **Số tiền học phí đã nộp:** **{totalTuition:N0} VNĐ** (Đơn giá 450.000 VNĐ / tín chỉ)");
                                sb.AppendLine($"- **Thời gian thanh toán:** **{(studentEnrollment?.PaymentDate.HasValue == true ? studentEnrollment.PaymentDate.Value.ToString("dd/MM/yyyy HH:mm:ss") : "Đã xác nhận thanh toán")}**");
                                sb.AppendLine($"- **Phương thức thanh toán:** **{(!string.IsNullOrEmpty(studentEnrollment?.PaymentMethod) ? studentEnrollment.PaymentMethod : "VietQR Napas247")}**");
                                sb.AppendLine($"- **Mã giao dịch / Mã tham chiếu:** **{(!string.IsNullOrEmpty(studentEnrollment?.TransactionReference) ? studentEnrollment.TransactionReference : "VQR-CONFIRMED-2026")}**");
                                sb.AppendLine($"- **Trạng thái phiếu đăng ký:** **{(studentEnrollment?.Status ?? "Confirmed")}**");
                            }
                            else
                            {
                                sb.AppendLine($"❌ **CHƯA ĐÓNG.** Sinh viên **{evalTuitionStudent.FullName}** (MSSV: `{evalTuitionStudent.StudentCode}`) hiện **chưa thanh toán** học phí học kỳ **{semester}**!");
                                sb.AppendLine();
                                sb.AppendLine($"- **Số tín chỉ đã đăng ký:** **{totalRegCredits} Tín chỉ**");
                                sb.AppendLine($"- **Tổng số tiền học phí cần nộp:** **{totalTuition:N0} VNĐ** (Đơn giá quy định: **450.000 VNĐ / tín chỉ**)");
                                sb.AppendLine($"- **Trạng thái công nợ:** **Chưa thanh toán (Unpaid)**");
                                sb.AppendLine();
                                sb.AppendLine("- **Hướng dẫn hoàn tất học phí:**");
                                sb.AppendLine("  + Bạn vào mục **'Thanh toán học phí'** (`/tuition`) trên thanh menu để quét mã VietQR MB Bank tự động gạch nợ tức thì 24/7.");
                                sb.AppendLine("  + Hoặc chuyển khoản tới tài khoản thu học phí nhà trường: **101705201967** - **MB Bank (Ngân hàng Quân Đội)**.");
                                sb.AppendLine("  + Đơn vị thụ hưởng: **TRƯỜNG ĐẠI HỌC KINH TẾ HUẾ**.");
                                sb.AppendLine($"  + Cú pháp chuyển khoản: `[{evalTuitionStudent.StudentCode}] HP {semester}`");
                            }
                        }
                    }

                    advice.AiMessage = sb.ToString();
                    return;
                }

                // Không hỏi tình trạng đóng tiền -> Hiển thị quy định mức thu học phí chuẩn
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

                    if (evalTuitionStudent != null && registeredSections.Any())
                    {
                        int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                        long totalTuition = (long)totalRegCredits * 450000;
                        var studentEnrollment = allSemesterEnrollments?.FirstOrDefault(e => e.StudentId == evalTuitionStudent.StudentId && e.Status != "Cancelled");
                        string statusLabel = studentEnrollment?.IsPaid == true ? "✅ Paid" : "❌ Unpaid";
                        sb.AppendLine($"💳 **Your personalized tuition ({evalTuitionStudent.FullName} - {evalTuitionStudent.StudentCode}):**");
                        sb.AppendLine($"- Registered credits: **{totalRegCredits} Credits**");
                        sb.AppendLine($"- Total semester tuition: **{totalTuition:N0} VNĐ** (Status: **{statusLabel}**)");
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

                    if (evalTuitionStudent != null && registeredSections.Any())
                    {
                        int totalRegCredits = registeredSections.Sum(s => s.Course?.Credits ?? 0);
                        long totalTuition = (long)totalRegCredits * 450000;
                        var studentEnrollment = allSemesterEnrollments?.FirstOrDefault(e => e.StudentId == evalTuitionStudent.StudentId && e.Status != "Cancelled");
                        string statusLabel = studentEnrollment?.IsPaid == true ? "✅ Đã đóng" : "❌ Chưa đóng";
                        sb.AppendLine($"💳 **Học phí cá nhân của bạn ({evalTuitionStudent.FullName} - {evalTuitionStudent.StudentCode}):**");
                        sb.AppendLine($"- Tổng số tín chỉ đã đăng ký: **{totalRegCredits} Tín chỉ**");
                        sb.AppendLine($"- Tổng học phí học kỳ {semester}: **{totalTuition:N0} VNĐ** (Trạng thái: **{statusLabel}**)");
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
            // TRƯỜNG HỢP 6.1: HỒ SƠ & THÔNG TIN CÁ NHÂN SINH VIÊN (STUDENT PROFILE)
            // =========================================================================
            if (isStudentProfileQuery)
            {
                var profileStu = matchedExplicitStudent ?? currentStudent;
                if (profileStu != null)
                {
                    var sb = new StringBuilder();
                    if (isEnglish)
                    {
                        sb.AppendLine($"### 👤 Student Profile: **{profileStu.FullName}** (`{profileStu.StudentCode}`)");
                        sb.AppendLine();
                        sb.AppendLine($"- **Student ID (MSSV):** `{profileStu.StudentCode}`");
                        sb.AppendLine($"- **Full Name:** **{profileStu.FullName}**");
                        sb.AppendLine($"- **Academic Major:** **{profileStu.Major}**");
                        sb.AppendLine($"- **Academic Cohort:** **{profileStu.AcademicYear}**");
                        sb.AppendLine($"- **Maximum Credit Limit:** **{profileStu.MaxCreditsPerSemester} Credits/semester**");
                        sb.AppendLine($"- **University Email:** `{profileStu.Email}`");
                        sb.AppendLine($"- **Phone Number:** `{profileStu.PhoneNumber}`");
                    }
                    else
                    {
                        sb.AppendLine($"### 👤 Thông Tin Hồ Sơ Sinh Viên: **{profileStu.FullName}** (`{profileStu.StudentCode}`)");
                        sb.AppendLine();
                        sb.AppendLine($"- **Mã số sinh viên (MSSV):** `{profileStu.StudentCode}`");
                        sb.AppendLine($"- **Họ và tên:** **{profileStu.FullName}**");
                        sb.AppendLine($"- **Ngành đào tạo:** **{profileStu.Major}**");
                        sb.AppendLine($"- **Khóa đào tạo:** **{profileStu.AcademicYear}**");
                        sb.AppendLine($"- **Hạn mức đăng ký:** **Tối đa {profileStu.MaxCreditsPerSemester} tín chỉ / học kỳ**");
                        sb.AppendLine($"- **Email sinh viên:** `{profileStu.Email}`");
                        sb.AppendLine($"- **Số điện thoại:** `{profileStu.PhoneNumber}`");
                    }

                    advice.AiMessage = sb.ToString();
                    return;
                }
            }

            // =========================================================================
            // TRƯỜNG HỢP 6.2: DANH SÁCH MÔN ĐÃ ĐĂNG KÝ CỦA SINH VIÊN TRONG HỌC KỲ
            // =========================================================================
            bool isRegisteredListQuery =
                (matchedExplicitStudent != null || (currentStudent != null && (hasPersonalPronoun || qNorm.Contains("da dang ky mon nao") || qNorm.Contains("mon da dang ky")))) &&
                (qNorm.Contains("da dang ky") || qNorm.Contains("dang ky nhung mon") || qNorm.Contains("cac mon da dang ky") ||
                 qNorm.Contains("phieu dang ky") || qNorm.Contains("ket qua dang ky") || qLower.Contains("registered courses") ||
                 qLower.Contains("enrolled courses"));

            if (isRegisteredListQuery && (matchedExplicitStudent != null || currentStudent != null))
            {
                var regStu = matchedExplicitStudent ?? currentStudent!;
                var targetSections = (regStu.StudentId == currentStudent?.StudentId)
                    ? registeredSections
                    : enrollmentRepository.GetRegisteredSectionsByStudentAsync(regStu.StudentId, semester).GetAwaiter().GetResult().ToList();

                var sb = new StringBuilder();
                if (targetSections.Any())
                {
                    int totalCr = targetSections.Sum(s => s.Course?.Credits ?? 0);
                    long totalFee = (long)totalCr * 450000;
                    sb.AppendLine($"### 📋 Danh Sách Học Phần Đã Đăng Ký Kỳ {semester}: **{regStu.FullName}** (`{regStu.StudentCode}`)");
                    sb.AppendLine($"- **Tổng số lớp học phần:** **{targetSections.Count} lớp** ({totalCr} tín chỉ, học phí: {totalFee:N0} VNĐ).");
                    sb.AppendLine();
                    foreach (var sec in targetSections)
                    {
                        sb.AppendLine($"- **Lớp `{sec.SectionCode}`** | Môn: **{sec.Course?.CourseCode} - {sec.Course?.CourseName}** ({sec.Course?.Credits}TC) | GV: **{sec.Instructor}** | Phòng: **{sec.Room}** | Lịch: {FormatSchedule(sec)}");
                    }
                }
                else
                {
                    sb.AppendLine($"### 📋 Danh Sách Học Phần Đã Đăng Ký Kỳ {semester}: **{regStu.FullName}** (`{regStu.StudentCode}`)");
                    sb.AppendLine($"- Sinh viên hiện **chưa đăng ký lớp học phần nào** trong học kỳ {semester}.");
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
            // TRƯỜNG HỢP 9.5: TRA CỨU CÁC LỚP HỌC PHẦN ĐANG MỞ TOÀN TRƯỜNG TRONG KỲ
            // Ví dụ: "Có những lớp học phần nào đang mở?", "Các lớp học phần mở kỳ này"
            // =========================================================================
            bool isOpenSectionsQuery =
                (qNorm.Contains("lop hoc phan") || qNorm.Contains("lop dang mo") || qNorm.Contains("cac lop mo") || qNorm.Contains("mon mo")) &&
                (qNorm.Contains("dang mo") || qNorm.Contains("mo ky nay") || qNorm.Contains("co nhung lop") || qNorm.Contains("danh sach") || qNorm.Contains("nhung lop nao") || qLower.Contains("open sections") || qLower.Contains("available classes"));

            if (isOpenSectionsQuery)
            {
                var openInSemester = sectionsPool.ToList();
                var sb = new StringBuilder();
                if (isEnglish)
                {
                    sb.AppendLine($"### 🏫 Available Sections in Semester {semester} ({openInSemester.Count} classes):\n");
                    foreach (var sec in openInSemester)
                    {
                        int remain = Math.Max(0, sec.MaxCapacity - sec.CurrentEnrollment);
                        sb.AppendLine($"- **Class `{sec.SectionCode}`** — **{sec.Course?.CourseName}** ({sec.Course?.Credits} Credits):");
                        sb.AppendLine($"  + Instructor: **{sec.Instructor}** | Room: **{sec.Room}**");
                        sb.AppendLine($"  + Schedule: **{FormatScheduleDetailed(sec, true)}**");
                        sb.AppendLine($"  + Slots left: **{remain}/{sec.MaxCapacity}**");
                    }
                }
                else
                {
                    sb.AppendLine($"### 🏫 Danh Sách Các Lớp Học Phần Đang Mở Trong Học Kỳ {semester} ({openInSemester.Count} lớp):\n");
                    foreach (var sec in openInSemester)
                    {
                        int remain = Math.Max(0, sec.MaxCapacity - sec.CurrentEnrollment);
                        sb.AppendLine($"- **Lớp `{sec.SectionCode}`** — **{sec.Course?.CourseName}** (Mã môn: `{sec.Course?.CourseCode}` - **{sec.Course?.Credits} TC**):");
                        sb.AppendLine($"  + Giảng viên: **{sec.Instructor}** | Phòng: **{sec.Room}**");
                        sb.AppendLine($"  + Lịch học: **{FormatScheduleDetailed(sec, false)}**");
                        sb.AppendLine($"  + Sĩ số: **{sec.CurrentEnrollment}/{sec.MaxCapacity} sinh viên** (Còn trống **{remain} chỗ**)");
                    }
                }

                advice.AiMessage = sb.ToString();
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

                // Nếu câu hỏi đối chiếu cả Giảng viên và Môn học: "Thầy Hà Ngọc Long có dạy môn Lập trình Web không?"
                if (!string.IsNullOrEmpty(matchedInstructorName))
                {
                    var instructorSectionsForCourse = courseSections.Where(s => IsInstructorMatch(s.Instructor, matchedInstructorName)).ToList();
                    var sbInstCourse = new StringBuilder();
                    if (isEnglish)
                    {
                        sbInstCourse.AppendLine($"### 👨‍🏫 Lecturer & Course Lookup (Semester {semester}):");
                        sbInstCourse.AppendLine($"- Lecturer: **{matchedInstructorName}**");
                        sbInstCourse.AppendLine($"- Course: **{matchedCourse.CourseName}** (`{matchedCourse.CourseCode}` - {matchedCourse.Credits} Credits)");
                        sbInstCourse.AppendLine();

                        if (instructorSectionsForCourse.Any())
                        {
                            sbInstCourse.AppendLine($"✅ **YES.** Lecturer **{matchedInstructorName}** currently teaches **{instructorSectionsForCourse.Count} section(s)** of **{matchedCourse.CourseName}**:");
                            sbInstCourse.AppendLine();
                            foreach (var s in instructorSectionsForCourse)
                            {
                                int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                                sbInstCourse.AppendLine($"- Class `{s.SectionCode}`: {FormatScheduleDetailed(s, true)} | Room: {s.Room ?? "TBD"} | Slots: {s.CurrentEnrollment}/{s.MaxCapacity} ({remain} left)");
                            }
                        }
                        else
                        {
                            sbInstCourse.AppendLine($"❌ **NO.** Lecturer **{matchedInstructorName}** does **NOT teach any section** of **{matchedCourse.CourseName}** in semester {semester}.");
                            if (courseSections.Any())
                            {
                                sbInstCourse.AppendLine();
                                sbInstCourse.AppendLine($"💡 This course is taught by other instructor(s): {string.Join(", ", courseSections.Select(s => $"**{s.Instructor}**").Distinct())}.");
                            }
                        }
                    }
                    else
                    {
                        sbInstCourse.AppendLine($"### 👨‍🏫 Thông Tin Giảng Viên Phụ Trách Môn Học (Học kỳ {semester}):");
                        sbInstCourse.AppendLine($"- Giảng viên: **{matchedInstructorName}**");
                        sbInstCourse.AppendLine($"- Môn học: **{matchedCourse.CourseName}** (Mã môn: `{matchedCourse.CourseCode}` — **{matchedCourse.Credits} Tín chỉ**)");
                        sbInstCourse.AppendLine();

                        if (instructorSectionsForCourse.Any())
                        {
                            sbInstCourse.AppendLine($"✅ **CÓ.** Giảng viên **{matchedInstructorName}** hiện **CÓ PHỤ TRÁCH {instructorSectionsForCourse.Count} lớp học phần** môn **{matchedCourse.CourseName}**:");
                            sbInstCourse.AppendLine();
                            foreach (var s in instructorSectionsForCourse)
                            {
                                int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                                sbInstCourse.AppendLine($"- Mã lớp `{s.SectionCode}`: Lịch học **{FormatScheduleDetailed(s, false)}** | Phòng: **{s.Room ?? "Chưa xếp"}** | Sĩ số: **{s.CurrentEnrollment}/{s.MaxCapacity}** (Còn trống **{remain} chỗ**)");
                            }
                        }
                        else
                        {
                            sbInstCourse.AppendLine($"❌ **KHÔNG.** Giảng viên **{matchedInstructorName}** hiện **KHÔNG PHỤ TRÁCH lớp nào** của môn **{matchedCourse.CourseName}** trong học kỳ {semester}.");
                            if (courseSections.Any())
                            {
                                sbInstCourse.AppendLine();
                                sbInstCourse.AppendLine($"💡 Môn học này hiện đang do các giảng viên sau phụ trách: {string.Join(", ", courseSections.Select(s => $"**{s.Instructor}**").Distinct())}.");
                            }
                        }
                    }
                    advice.AiMessage = sbInstCourse.ToString();
                    return;
                }

                bool isCourseYn = IsYesNoQuery(qNorm, qLower);
                var sb = new StringBuilder();
                if (isEnglish)
                {
                    if (isCourseYn)
                    {
                        sb.AppendLine(courseSections.Any()
                            ? $"✅ **YES.** In semester **{semester}**, there are **{courseSections.Count} section(s) currently open** for **{matchedCourse.CourseName}** (`{matchedCourse.CourseCode}`):\n"
                            : $"❌ **NO.** In semester **{semester}**, there are **no open sections** scheduled for **{matchedCourse.CourseName}** (`{matchedCourse.CourseCode}`).\n");
                    }
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
                    if (isCourseYn)
                    {
                        sb.AppendLine(courseSections.Any()
                            ? $"✅ **CÓ.** Trong học kỳ **{semester}**, nhà trường **CÓ MỞ {courseSections.Count} lớp học phần** cho môn **{matchedCourse.CourseName}** (`{matchedCourse.CourseCode}`):\n"
                            : $"❌ **KHÔNG.** Trong học kỳ **{semester}**, nhà trường **CHƯA MỞ lớp học phần nào** cho môn **{matchedCourse.CourseName}** (`{matchedCourse.CourseCode}`).\n");
                    }
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

                bool isInstYn = IsYesNoQuery(qNorm, qLower);

                if (isEnglish)
                {
                    sb.AppendLine($"### 👨‍🏫 Lecturer Information & Assigned Courses: **{matchedInstructorName}**");
                    sb.AppendLine();
                    string prefixEn = isInstYn ? "✅ **YES.** " : "";
                    sb.AppendLine($"{prefixEn}Lecturer **{matchedInstructorName}** currently teaches **{matchedInstructorSections.Count} class(es)** (Total enrollment: **{totalEnrolled}/{totalCap} students**):");
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
                    string prefixVi = isInstYn ? "✅ **CÓ.** " : "";
                    sb.AppendLine($"{prefixVi}Cố vấn AI đã truy vấn CSDL SQL Server thời gian thực. Giảng viên **{matchedInstructorName}** hiện phụ trách **{matchedInstructorSections.Count} lớp học phần** (Tổng sĩ số: **{totalEnrolled}/{totalCap} sinh viên**):");
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
                qNorm.Contains("thay ") || qLower.Contains("cô ") || qNorm.Contains("co giao ") || qNorm.Contains("giang vien") ||
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
            // TRƯỜNG HỢP 15: CÂU HỎI HỌC THUẬT RỘNG / QUY CHẾ / TƯ VẤN (TỰ NHIÊN NHƯ AI THỰC THỤ)
            // Trả lời đa dạng, đúng trọng tâm câu hỏi của sinh viên Trường Đại học Kinh tế - Đại học Huế.
            // =========================================================================
            if (TryAnswerGeneralAcademicKnowledge(qNorm, qLower, qRaw, isEnglish, currentStudent, semester, out string generalResponse))
            {
                advice.AiMessage = generalResponse;
                return;
            }

            // Với câu hỏi quá ngắn hoặc cụt lủn (1-2 từ như "ngày mai", "tiết mấy", "học phí", "đăng ký", "cho hỏi chút") -> Hỏi lại lịch sự
            bool isVeryShortAmbiguous = qNorm.Length <= 14 && (
                qNorm == "ngay mai" || qNorm == "tiet may" || qNorm == "hoc phi" ||
                qNorm == "dang ky" || qNorm == "cho hoi chut" || qNorm == "hoi chut" ||
                qNorm == "lich hoc" || qNorm == "lop hoc" || qNorm == "cho hoi");

            if (isVeryShortAmbiguous)
            {
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
                return;
            }

            // Với các câu hỏi khác: Phản hồi thông minh, chuyên nghiệp, đúng trọng tâm như AI cố vấn thực thụ
            advice.AiMessage = GenerateSynthesizedAiAcademicResponse(qRaw, qNorm, qLower, isEnglish, currentStudent, semester);
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
            bool isEnglish,
            string? userPrompt = null)
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

            // Nhận diện chủ đề cụ thể người dùng nêu (web / cơ sở dữ liệu / an toàn thông tin).
            // Có chủ đề -> chỉ gợi ý lớp đúng chủ đề, KHÔNG lấp thêm môn không liên quan cho đủ tín chỉ.
            bool wantsWeb = qNorm.Contains("web");
            bool wantsDatabase = qNorm.Contains("csdl") || qNorm.Contains("co so du lieu");
            bool wantsSecurity = qNorm.Contains("an toan") || qNorm.Contains("bao mat");
            bool hasSpecificTopic = wantsWeb || wantsDatabase || wantsSecurity;

            var scored = new List<(Section Section, double Score, string Reason, bool OnTopic)>();
            foreach (var sec in candidateSections)
            {
                if (sec.Course == null) continue;
                double score = 10.0;
                var reasons = new List<string>();

                var courseNameNorm = RemoveDiacritics(sec.Course.CourseName.ToLowerInvariant());

                bool onTopic = false;

                if (wantsWeb && courseNameNorm.Contains("web"))
                {
                    score += 50;
                    onTopic = true;
                    reasons.Add(isEnglish ? "Matches your Web Programming interest" : "Khớp mục tiêu chuyên sâu Lập trình Web");
                }
                if (wantsDatabase && courseNameNorm.Contains("co so du lieu"))
                {
                    score += 45;
                    onTopic = true;
                    reasons.Add(isEnglish ? "Matches Database goal" : "Khớp nguyện vọng Cơ sở dữ liệu");
                }
                if (wantsSecurity && courseNameNorm.Contains("an toan"))
                {
                    score += 50;
                    onTopic = true;
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
                scored.Add((sec, score, string.Join("; ", reasons), onTopic));
            }

            if (hasSpecificTopic)
            {
                scored = scored.Where(x => x.OnTopic).ToList();
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

            // Lời mở đầu phản ánh đúng câu hỏi: trích lại yêu cầu + mô tả mục tiêu gợi ý thực sự áp dụng
            string quoted = QuoteUserPrompt(userPrompt);
            var focusVi = new List<string>();
            var focusEn = new List<string>();
            if (wantsWeb) { focusVi.Add("chủ đề Web"); focusEn.Add("Web topic"); }
            if (wantsDatabase) { focusVi.Add("chủ đề Cơ sở dữ liệu"); focusEn.Add("Database topic"); }
            if (wantsSecurity) { focusVi.Add("chủ đề An toàn thông tin"); focusEn.Add("Security topic"); }
            if (avoidMondayMorning) { focusVi.Add("tránh sáng Thứ 2"); focusEn.Add("avoid Monday morning"); }
            if (!hasSpecificTopic) { focusVi.Add($"mục tiêu khoảng {targetCredits} tín chỉ"); focusEn.Add($"target of about {targetCredits} credits"); }

            if (advice.RecommendedSections.Count == 0)
            {
                advice.AiMessage = isEnglish
                    ? $"### 🎯 Course suggestions for your request\n\n" +
                      (quoted.Length > 0 ? $"You asked: *\"{quoted}\"*.\n\n" : string.Empty) +
                      $"- I could not find any open section in semester {semester} that matches ({string.Join(", ", focusEn)}) and also satisfies your prerequisites, schedule and remaining credit limit."
                    : $"### 🎯 Gợi ý học phần theo yêu cầu của bạn\n\n" +
                      (quoted.Length > 0 ? $"Bạn đã hỏi: *\"{quoted}\"*.\n\n" : string.Empty) +
                      $"- Hiện chưa có lớp học phần nào trong học kỳ {semester} vừa khớp yêu cầu ({string.Join(", ", focusVi)}) vừa đạt điều kiện tiên quyết, không trùng lịch và còn trong hạn mức tín chỉ.";
                return;
            }

            if (isEnglish)
            {
                advice.AiMessage = (hasSpecificTopic ? $"### 🎯 Course suggestions for your request\n\n" : $"### 🎯 Course package suggestion for semester {semester}\n\n") +
                                   $"Hello **{student.FullName}** (`{student.StudentCode}` - Major: {student.Major})!\n\n" +
                                   (quoted.Length > 0 ? $"- You asked: *\"{quoted}\"* -> focus: {string.Join(", ", focusEn)}.\n" : string.Empty) +
                                   $"- Based on your completed courses and academic goals, the AI Advisor recommends **{advice.RecommendedSections.Count} course(s) ({advice.TotalCredits} Credits)** for semester {semester}.\n" +
                                   $"- **Academic Safety Guarantee:** 100% of recommended courses meet all prerequisites and **have zero schedule conflicts**.\n" +
                                   $"- **User Confirmation Required:** This recommendation is an advisory suggestion. Please review the items below, select desired courses, and click **'Confirm & Add to Cart'** to proceed.";
            }
            else
            {
                advice.AiMessage = (hasSpecificTopic ? $"### 🎯 Gợi ý học phần theo yêu cầu của bạn\n\n" : $"### 🎯 Gói học phần gợi ý cho học kỳ {semester}\n\n") +
                                   $"Chào bạn **{student.FullName}** (`{student.StudentCode}` - Ngành {student.Major})!\n\n" +
                                   (quoted.Length > 0 ? $"- Bạn đã hỏi: *\"{quoted}\"* -> trọng tâm gợi ý: {string.Join(", ", focusVi)}.\n" : string.Empty) +
                                   $"- Dựa trên hồ sơ học bạ và nguyện vọng của bạn, Cố vấn AI đề xuất gói học phần gồm **{advice.RecommendedSections.Count} môn ({advice.TotalCredits} Tín chỉ)** trong học kỳ {semester}.\n" +
                                   $"- **Cam kết an toàn học vụ:** 100% môn đề xuất đã thỏa mãn điều kiện tiên quyết và **tuyệt đối không bị trùng lịch học**.\n" +
                                   $"- **Xác nhận người dùng:** Dữ liệu do AI đề xuất dưới đây là gợi ý. Bạn hãy rà soát danh sách bên dưới, chọn các môn mong muốn và bấm nút **'Xác Nhận & Thêm Vào Giỏ Đăng Ký'** để lưu vào giỏ hàng.";
            }
        }

        /// <summary>
        /// Tư vấn đúng MỘT MÔN cụ thể sinh viên nhắc tới (vd "tư vấn môn lập trình web"):
        /// thông tin môn, tiên quyết & trạng thái của sinh viên, các lớp đang mở (GV, lịch, phòng, chỗ còn), lớp trùng lịch.
        /// RecommendedSections chỉ chứa lớp của môn này và chỉ gồm lớp sinh viên THỰC SỰ đăng ký được;
        /// sinh viên vẫn phải xác nhận trước khi thêm vào giỏ (AI chỉ gợi ý).
        /// </summary>
        private static void GenerateCourseSpecificAdvice(
            AiCourseAdvice advice,
            Student student,
            Course course,
            List<Section> semesterSections,
            List<Course> allCourses,
            List<Prerequisite> allPrerequisites,
            List<CompletedCourse> completedCourses,
            List<Section> registeredSections,
            int remainingCreditsAllowed,
            string semester,
            bool isEnglish)
        {
            var passedCourseIds = completedCourses.Where(c => c.Grade >= 5.0).Select(c => c.CourseId).ToHashSet();
            bool alreadyPassed = passedCourseIds.Contains(course.CourseId);
            bool alreadyRegistered = registeredSections.Any(r => r.CourseId == course.CourseId);

            var prereqCourses = allPrerequisites
                .Where(p => p.CourseId == course.CourseId)
                .Select(p => allCourses.FirstOrDefault(c => c.CourseId == p.PrerequisiteCourseId))
                .Where(c => c != null)
                .Select(c => c!)
                .ToList();
            var missingPrereqs = prereqCourses.Where(c => !passedCourseIds.Contains(c.CourseId)).ToList();

            var openSections = semesterSections.Where(s => s.CourseId == course.CourseId).ToList();
            bool exceedsCredits = course.Credits > remainingCreditsAllowed;
            bool eligible = !alreadyPassed && !alreadyRegistered && !missingPrereqs.Any();

            var sb = new StringBuilder();
            if (isEnglish)
            {
                sb.AppendLine($"### 📘 Advice for **{course.CourseName}** (`{course.CourseCode}`)");
                sb.AppendLine();
                sb.AppendLine($"- **Credits:** {course.Credits}");
                sb.AppendLine($"- **Prerequisites:** {(prereqCourses.Any() ? string.Join(", ", prereqCourses.Select(c => $"{c.CourseName} ({c.CourseCode}) - {(passedCourseIds.Contains(c.CourseId) ? "passed" : "NOT passed")}")) : "None")}");
                sb.AppendLine($"- **Your status ({student.FullName}):** {(alreadyPassed ? "you have already passed this course" : alreadyRegistered ? $"already registered in {semester}" : missingPrereqs.Any() ? "prerequisites NOT satisfied, registration would be blocked" : "eligible to register")}");
                sb.AppendLine($"- **Remaining credit limit:** {remainingCreditsAllowed} credits");
            }
            else
            {
                sb.AppendLine($"### 📘 Tư vấn môn **{course.CourseName}** (`{course.CourseCode}`)");
                sb.AppendLine();
                sb.AppendLine($"- **Số tín chỉ:** {course.Credits}");
                sb.AppendLine($"- **Học phần tiên quyết:** {(prereqCourses.Any() ? string.Join(", ", prereqCourses.Select(c => $"{c.CourseName} ({c.CourseCode}) - {(passedCourseIds.Contains(c.CourseId) ? "đã đạt" : "CHƯA đạt")}")) : "Không yêu cầu")}");
                sb.AppendLine($"- **Tình trạng của bạn ({student.FullName}):** {(alreadyPassed ? "bạn đã hoàn thành môn này" : alreadyRegistered ? $"bạn đã đăng ký môn này trong học kỳ {semester}" : missingPrereqs.Any() ? "CHƯA đủ tiên quyết, hệ thống sẽ chặn nếu đăng ký" : "đủ điều kiện đăng ký")}");
                sb.AppendLine($"- **Hạn mức tín chỉ còn lại:** {remainingCreditsAllowed} tín chỉ");
            }
            sb.AppendLine();

            if (!openSections.Any())
            {
                sb.AppendLine(isEnglish
                    ? $"- No section of this course is open in semester {semester}."
                    : $"- Học kỳ {semester} hiện chưa mở lớp học phần nào cho môn này.");
            }
            else
            {
                sb.AppendLine(isEnglish
                    ? $"**Open sections in {semester} ({openSections.Count}):**"
                    : $"**Các lớp đang mở trong học kỳ {semester} ({openSections.Count} lớp):**");

                foreach (var s in openSections)
                {
                    int remain = Math.Max(0, s.MaxCapacity - s.CurrentEnrollment);
                    bool isFull = remain == 0;
                    var conflictCodes = registeredSections
                        .Where(r => r.SectionId != s.SectionId && HasScheduleConflict(s, r))
                        .Select(r => r.SectionCode)
                        .ToList();

                    string note;
                    if (isFull) note = isEnglish ? "FULL" : "HẾT CHỎ";
                    else if (conflictCodes.Any()) note = isEnglish ? $"CONFLICTS with {string.Join(", ", conflictCodes)}" : $"TRÙNG LỊCH với {string.Join(", ", conflictCodes)}";
                    else note = isEnglish ? "no conflict" : "không trùng lịch";

                    sb.AppendLine(isEnglish
                        ? $"- `{s.SectionCode}`: {s.Instructor} | {FormatScheduleDetailed(s, true)} | Room {s.Room} | {remain}/{s.MaxCapacity} slots left | {note}"
                        : $"- `{s.SectionCode}`: GV {s.Instructor} | {FormatScheduleDetailed(s, false)} | Phòng {s.Room} | còn {remain}/{s.MaxCapacity} chỗ | {note}");

                    // Chỉ đề xuất lớp sinh viên thực sự đăng ký được (đủ điều kiện, còn chỗ, không trùng lịch, trong hạn mức)
                    if (eligible && !isFull && !conflictCodes.Any() && !exceedsCredits)
                    {
                        advice.RecommendedSections.Add(new AiRecommendedSection
                        {
                            SectionId = s.SectionId,
                            SectionCode = s.SectionCode,
                            CourseId = s.CourseId,
                            CourseCode = course.CourseCode,
                            CourseName = course.CourseName,
                            Credits = course.Credits,
                            Instructor = s.Instructor,
                            Room = s.Room,
                            ScheduleSummary = FormatSchedule(s),
                            RecommendationReason = isEnglish
                                ? $"Matches the course you asked about; prerequisites met, {remain} slots left, no schedule conflict"
                                : $"Đúng môn bạn hỏi; đạt tiên quyết, còn {remain} chỗ, không trùng lịch",
                            IsSelected = true
                        });
                    }
                }
            }

            sb.AppendLine();
            if (advice.RecommendedSections.Any())
            {
                sb.AppendLine(isEnglish
                    ? "- **User Confirmation Required:** this is only a suggestion. Review the section(s) below and click **'Confirm & Add to Cart'** to continue."
                    : "- **Xác nhận người dùng:** đây chỉ là gợi ý. Bạn hãy rà soát lớp bên dưới và bấm **'Xác Nhận & Thêm Vào Giỏ Đăng Ký'** để tiếp tục.");
            }
            else
            {
                string reason;
                if (alreadyPassed) reason = isEnglish ? "you have already passed this course" : "bạn đã hoàn thành môn này";
                else if (alreadyRegistered) reason = isEnglish ? "you are already registered for it" : "bạn đã đăng ký môn này rồi";
                else if (missingPrereqs.Any()) reason = isEnglish ? $"you still need: {string.Join(", ", missingPrereqs.Select(c => c.CourseName))}" : $"bạn còn thiếu học phần tiên quyết: {string.Join(", ", missingPrereqs.Select(c => c.CourseName))}";
                else if (exceedsCredits) reason = isEnglish ? "it would exceed your remaining credit limit" : "sẽ vượt hạn mức tín chỉ còn lại";
                else if (!openSections.Any()) reason = isEnglish ? "no section is open this semester" : "học kỳ này chưa mở lớp";
                else reason = isEnglish ? "every open section is full or conflicts with your schedule" : "các lớp đang mở đều hết chỗ hoặc trùng lịch của bạn";
                sb.AppendLine(isEnglish
                    ? $"- No section is suggested for cart right now because {reason}."
                    : $"- Chưa đề xuất lớp nào để thêm vào giỏ vì {reason}.");
            }

            advice.AiMessage = sb.ToString();
        }

        /// <summary>Làm sạch câu hỏi người dùng để trích dẫn vào lời mở đầu (bỏ ký tự markdown, cắt ngắn).</summary>
        private static string QuoteUserPrompt(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;
            var s = Regex.Replace(prompt.Trim(), @"[\r\n\*`_]+", " ");
            return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
        }

        /// <summary>
        /// Bỏ tin nhắn cuối của lịch sử nếu nó chính là câu hỏi hiện tại (nơi gọi đã thêm trước). Không sửa danh sách gốc của nơi gọi.
        /// </summary>
        private static List<AiConversationMessage>? RemoveTrailingDuplicateUserPrompt(List<AiConversationMessage>? history, string userPrompt)
        {
            if (history == null || history.Count == 0 || string.IsNullOrWhiteSpace(userPrompt)) return history;

            var last = history[history.Count - 1];
            if (last.IsUser && string.Equals(last.Content?.Trim(), userPrompt, StringComparison.Ordinal))
            {
                return history.Take(history.Count - 1).ToList();
            }
            return history;
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
                   qNorm.Contains("ket qua dang ky") || qNorm.Contains("danh sach mon da dang ky") ||
                   (qNorm.Contains("mon") && qNorm.Contains("da dang ky")) || qNorm.Contains("mon da dk") ||
                   qLower.Contains("enrolled courses") || qLower.Contains("registered courses") || qLower.Contains("my schedule") ||
                   ((qLower.Contains("course") || qLower.Contains("class")) && (qLower.Contains("register") || qLower.Contains("enroll")));
        }

        private static bool IsRegisteredCoursesIntent(string qNorm, string qLower, bool hasPersonalPronoun, Student? targetStudent)
        {
            // Loại trừ các câu hỏi về mở/đóng cổng, thời gian đăng ký hoặc rút môn
            if (qNorm.Contains("mo cong") || qNorm.Contains("dong cong") || qNorm.Contains("thoi gian mo") ||
                qNorm.Contains("khi nao mo") || qNorm.Contains("khi nao dong") || qNorm.Contains("dot dang ky") ||
                qNorm.Contains("han dang ky") || qNorm.Contains("rut mon") || qNorm.Contains("huy mon") ||
                qNorm.Contains("bo mon") || qNorm.Contains("han muc") || qLower.Contains("registration period"))
            {
                return false;
            }

            bool hasPersonalScope = hasPersonalPronoun || targetStudent != null ||
                                    qNorm.Contains("cua toi") || qNorm.Contains("cua em") || qNorm.Contains("cua minh") ||
                                    qNorm.Contains("cho toi") || qNorm.Contains("cho em") || qNorm.Contains("giup toi") ||
                                    qNorm.Contains("toi ") || qNorm.Contains("minh ") || qNorm.Contains("em ") ||
                                    qLower.Contains("my ") || qLower.Contains("for me") || qLower.Contains("i have") || qLower.Contains("did i");

            // Hành động: đăng ký, ghi danh, đã chọn, đã lấy, tham gia, xem, học (khi đi kèm đại từ/ngữ cảnh cá nhân)
            bool hasRegistrationAction =
                qNorm.Contains("dang ky") || qNorm.Contains("ghi danh") || qNorm.Contains("da lay") ||
                qNorm.Contains("da chon") || qNorm.Contains("tham gia") || qNorm.Contains("da dk") ||
                qNorm.Contains("hoc nhung mon") || qNorm.Contains("hoc mon gi") || qNorm.Contains("hoc lop nao") ||
                qNorm.Contains("hoc nhung lop") ||
                (hasPersonalScope && (qNorm.Contains("hoc mon") || qNorm.Contains("hoc lop") || qNorm.Contains("hoc nhung") || (qNorm.Contains("em hoc") && qNorm.Contains("mon")) || (qNorm.Contains("toi hoc") && qNorm.Contains("mon")) || (qNorm.Contains("minh hoc") && qNorm.Contains("mon")))) ||
                qLower.Contains("register") || qLower.Contains("enrolled") || qLower.Contains("signed up");

            // Đối tượng học vụ: môn, lớp, học phần, tín chỉ, subject, course, class
            bool hasAcademicSubject =
                qNorm.Contains("mon") || qNorm.Contains("lop") || qNorm.Contains("hoc phan") ||
                qNorm.Contains("tin chi") || qLower.Contains("course") || qLower.Contains("class") || qLower.Contains("subject");

            // Cách diễn đạt dạng "Danh sách học phần của tôi đâu", "Thời khóa biểu của tôi"
            bool isListOrSchedule =
                (qNorm.Contains("danh sach") || qNorm.Contains("thoi khoa bieu") || qNorm.Contains("lich hoc") || qNorm.Contains("phieu dang ky") || qNorm.Contains("ket qua")) &&
                hasAcademicSubject && (hasPersonalScope || qNorm.Contains("da dang ky") || qNorm.Contains("da ghi danh"));

            if (isListOrSchedule) return true;

            // Dạng hỏi: "Mình đăng ký những lớp gì rồi?", "Tôi đã đăng ký môn nào?", "Kiểm tra giúp tôi các môn đã ghi danh", "học kỳ này em học những môn gì"
            if (hasRegistrationAction && hasAcademicSubject)
            {
                bool isQuestioning = hasPersonalScope || qNorm.Contains("gi") || qNorm.Contains("nao") ||
                                     qNorm.Contains("dau") || qNorm.Contains("roi") || qNorm.Contains("chua") ||
                                     qNorm.Contains("kiem tra") || qNorm.Contains("xem") || qNorm.Contains("bao nhieu");
                if (isQuestioning) return true;
            }

            if ((qLower.Contains("registered course") || qLower.Contains("enrolled course") || qLower.Contains("registered class") || qLower.Contains("enrolled class") ||
                 (qLower.Contains("my") && (qLower.Contains("course") || qLower.Contains("class")) && (qLower.Contains("register") || qLower.Contains("enroll")))))
            {
                return true;
            }

            return false;
        }

        private static int? DetectOrdinalPosition(string qNorm, string qLower)
        {
            if (qNorm.Contains("dau tien") || qNorm.Contains("thu nhat") || qNorm.Contains("so 1") || qNorm.Contains("dau") ||
                qLower.Contains("first") || qLower.Contains("1st"))
            {
                return 0;
            }

            if (qNorm.Contains("mon thu hai") || qNorm.Contains("cai thu hai") || qNorm.Contains("lop thu hai") ||
                qNorm.Contains("mon thu 2") || qNorm.Contains("cai thu 2") || qNorm.Contains("lop thu 2") ||
                qNorm.Contains("tiep theo") || qLower.Contains("second") || qLower.Contains("2nd") || qLower.Contains("next"))
            {
                return 1;
            }

            if (qNorm.Contains("mon thu ba") || qNorm.Contains("cai thu ba") || qNorm.Contains("mon thu 3") ||
                qNorm.Contains("lop thu ba") || qNorm.Contains("lop thu 3") || qLower.Contains("third") || qLower.Contains("3rd"))
            {
                return 2;
            }

            if (qNorm.Contains("cuoi cung") || qNorm.Contains("chot") || qLower.Contains("last"))
            {
                return -1;
            }

            return null;
        }

        private static bool IsFollowUpQuestion(string qNorm, string qLower)
        {
            if (qNorm.Contains("mo cong") || qNorm.Contains("dong cong") || qNorm.Contains("thoi gian mo") ||
                qNorm.Contains("khi nao mo") || qNorm.Contains("khi nao dong") || qNorm.Contains("dot dang ky") ||
                qNorm.Contains("han dang ky") || qLower.Contains("registration period"))
            {
                return false;
            }

            return qNorm.Contains("mon do") || qNorm.Contains("mon nay") ||
                   qNorm.Contains("lop do") || qNorm.Contains("lop nay") ||
                   qNorm.Contains("thay do") || qNorm.Contains("co do") ||
                   qNorm.Contains("the con") || qNorm.Contains("mon nao co lich") ||
                   qNorm.Contains("cai do") || qNorm.Contains("cai nay") ||
                   qNorm.Contains("dau tien") || qNorm.Contains("thu nhat") || qNorm.Contains("thu hai") ||
                   qNorm.Contains("con mon nao") || qNorm.Contains("mon khac") || qNorm.Contains("con gi nua") ||
                   qNorm.Contains("con lop nao") || qNorm.Contains("cac mon con lai") ||
                   qNorm.Contains("luc may gio") || qNorm.Contains("may gio") || qNorm.Contains("ca may") ||
                   qNorm.Contains("tiet may") || qNorm.Contains("thoi gian hoc") ||
                   qNorm.Contains("ai day") || qNorm.Contains("thay nao day") || qNorm.Contains("co nao day") ||
                   qNorm.Contains("phong nao") || qNorm.Contains("o dau") || qNorm.Contains("phong may") ||
                   qLower.Contains("who teaches it") || qLower.Contains("who teaches that") ||
                   qLower.Contains("which of those") || qLower.Contains("which room") ||
                   qLower.Contains("what time") || qLower.Contains("when is it") ||
                   qLower.Contains("what about") || qLower.Contains("what else") || qLower.Contains("any other") ||
                   qLower.Contains("first one") || qLower.Contains("second one") || qLower.Contains("last one");
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
            bool asksLoginVi = (qNorm.Contains("dang nhap") || qNorm.Contains("login") || qNorm.Contains("log in")) &&
                (qNorm.Contains("buoc") || qNorm.Contains("nhung buoc") || qNorm.Contains("cac buoc") ||
                 qNorm.Contains("nhu the nao") || qNorm.Contains("the nao") || qNorm.Contains("lam sao") ||
                 qNorm.Contains("cach") || qNorm.Contains("huong dan") || qNorm.Contains("o dau") ||
                 qNorm.Contains("muon") || qNorm.Contains("khong biet") || qNorm.Contains("cho nao") ||
                 qNorm.Contains("vao he thong") || qNorm.Contains("chi toi") || qNorm.Contains("chi giup") ||
                 qNorm.Contains("vao") || qNorm.Contains("ntn"));

            bool asksSystemAccessVi = (qNorm.Contains("vao he thong") || qNorm.Contains("truy cap he thong") || qNorm.Contains("vao tai khoan")) &&
                (qNorm.Contains("lam sao") || qNorm.Contains("the nao") || qNorm.Contains("cach") || qNorm.Contains("huong dan") || qNorm.Contains("buoc") || qNorm.Contains("ntn") || qNorm.Contains("muon"));

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
            if (allStudents == null || !allStudents.Any()) return null;

            // 1. Khớp trực tiếp theo StudentCode
            foreach (var stu in allStudents)
            {
                if (!string.IsNullOrWhiteSpace(stu.StudentCode) && qLower.Contains(stu.StudentCode.ToLowerInvariant()))
                {
                    return stu;
                }
            }

            // 2. Khớp Regex MSSV (e.g. SV21001, SV 21001)
            var mssvMatch = Regex.Match(qLower, @"\b(sv\s*?\d{3,8})\b");
            if (mssvMatch.Success)
            {
                string code = mssvMatch.Groups[1].Value.Replace(" ", "").ToUpperInvariant();
                var s = allStudents.FirstOrDefault(st => st.StudentCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (s != null) return s;
            }

            // 3. Khớp theo họ tên đầy đủ
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
            if (qNorm.Contains("tri tue nhan tao") || qNorm.Contains("hoc may") || qLower.Contains("machine learning") || qNorm.Contains("ai401") || (Regex.IsMatch(qNorm, @"\bai\b") && qNorm.Contains("mon")))
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

            bool isNameMai = qNorm.Contains("thi mai") || qNorm.Contains("tran mai") || qNorm.Contains("nguyen mai") || qNorm.Contains("le mai");
            bool isTomorrowWord = qNorm.Contains("ngay mai") || qLower.Contains("tomorrow") ||
                                  (!isNameMai && Regex.IsMatch(qNorm, @"\b(mai|sang mai|chieu mai|toi mai)\b"));

            if (isTomorrowWord)
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

                // Trích xuất các mã lớp học phần được nhắc trong câu trả lời gần nhất theo đúng thứ tự xuất hiện
                if (!context.LastMentionedSections.Any())
                {
                    var matches = Regex.Matches(content, @"\b([A-Za-z0-9]{2,10}_\d{2})\b");
                    foreach (Match m in matches)
                    {
                        string code = m.Groups[1].Value;
                        var s = allSections.FirstOrDefault(sec => sec.SectionCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                        if (s != null && !context.LastMentionedSections.Any(x => x.SectionId == s.SectionId))
                        {
                            context.LastMentionedSections.Add(s);
                        }
                    }

                    if (!context.LastMentionedSections.Any())
                    {
                        var orderedFound = new List<(int Index, Section Sec)>();
                        foreach (var sec in allSections)
                        {
                            int idx = content.IndexOf(sec.SectionCode, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                orderedFound.Add((idx, sec));
                            }
                        }
                        foreach (var item in orderedFound.OrderBy(x => x.Index))
                        {
                            if (!context.LastMentionedSections.Any(x => x.SectionId == item.Sec.SectionId))
                            {
                                context.LastMentionedSections.Add(item.Sec);
                            }
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
            string semester,
            List<Enrollment>? allSemesterEnrollments = null)
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

            var studentEnrollment = allSemesterEnrollments?.FirstOrDefault(e => e.StudentId == student.StudentId && e.Status != "Cancelled");
            bool isStudentPaid = studentEnrollment?.IsPaid == true;
            string paymentDetail = isStudentPaid
                ? $"ĐÃ HOÀN TẤT THANH TOÁN (Số tiền: {(studentEnrollment!.TuitionFee > 0 ? studentEnrollment.TuitionFee : totalTuition):N0} VNĐ, ngày: {studentEnrollment.PaymentDate:dd/MM/yyyy HH:mm}, qua: {studentEnrollment.PaymentMethod}, mã GD: {studentEnrollment.TransactionReference})"
                : $"CHƯA THANH TOÁN (Còn nợ: {totalTuition:N0} VNĐ, hạn mức nộp qua VietQR Napas247 MB Bank: 101705201967)";

            return $@"Bạn là Cố vấn Học thuật AI của sinh viên {student.FullName} (MSSV: {student.StudentCode}, Ngành: {student.Major}, Khóa: {student.AcademicYear}) tại Trường Đại học Kinh tế Huế trong học kỳ {semester}.

[PHONG CÁCH GIAO TIẾP]
- Thân thiện, tự nhiên, tôn trọng, trả lời THẲNG VÀO TRỌNG TÂM câu hỏi.
- TUYỆT ĐỐI KHÔNG trả lời dập khuôn văn mẫu, không lặp lại câu hỏi trừ khi cần làm rõ.
- Hỗ trợ cả tiếng Việt và tiếng Anh tự nhiên.
- TUYỆT ĐỐI KHÔNG tự động tuôn ra danh sách các câu hỏi mẫu dài dòng khi không được yêu cầu.

[HỒ SƠ CÁ NHÂN CỦA BẠN ({student.FullName} - {student.StudentCode})]
- Giới hạn tín chỉ kỳ này: Tối đa {student.MaxCreditsPerSemester} TC (Hiện đã đăng ký {totalRegCredits} TC, còn được đăng ký tối đa {remainingCreditsAllowed} TC nữa).
- Học phí kỳ {semester}: {totalRegCredits} tín chỉ = {totalTuition:N0} VNĐ (Đơn giá 450.000 VNĐ/TC, MB Bank: 101705201967).
- Tình trạng đóng học phí thực tế: {paymentDetail}
- Các môn BẠN ĐÃ ĐĂNG KÝ kỳ này ({registeredSections.Count} lớp):
{regSummary}
- Các môn BẠN ĐÃ HOÀN THÀNH trong học bạ:
{completedSummary}

[DỮ LIỆU CÁC LỚP HỌC PHẦN ĐANG MỞ KỲ {semester}]
{openSectionsSummary}

[ĐIỀU KIỆN TIÊN QUYẾT]
{prereqSummary}

[QUY TẮC PHẢN HỒI ĐÚNG TRỌNG TÂM]
1. TÌNH TRẠNG ĐÓNG HỌC PHÍ:
   - Nếu hỏi bạn đã đóng học phí chưa, có nợ tiền không: Hãy trả lời TRỰC TIẾP và DỨT KHOÁT ngay đầu câu: '✅ ĐÃ ĐÓNG.' hoặc '❌ CHƯA ĐÓNG.', kèm chi tiết số tiền, ngày giờ nộp và phương thức!
2. LỊCH HỌC NGÀY MAI / HÔM NAY / THỨ TRONG TUẦN:
   - CHỈ tra cứu trong các lớp bạn ĐÃ ĐĂNG KÝ có lịch vào đúng ngày đó.
   - Nêu trực tiếp: Môn gì, lớp nào, ca mấy, phòng nào, giảng viên nào.
   - Nếu ngày đó bạn KHÔNG CÓ LỊCH: Trả lời tự nhiên, thân thiện: Ngày mai bạn không có lịch học nào cả, hãy nghỉ ngơi hoặc tự ôn bài nhé! TUYỆT ĐỐI KHÔNG liệt kê tràn lan các môn của ngày khác.
3. ĐỐI CHIẾU SINH VIÊN VỚI GIẢNG VIÊN:
   - Khi hỏi bạn có học môn nào của Thầy/Cô X không: CHỈ kiểm tra trong danh sách các lớp bạn ĐÃ ĐĂNG KÝ.
   - Nếu có: Nêu rõ tên môn, mã lớp, lịch học, phòng học.
   - Nếu không: Khẳng định rõ ràng bạn không đăng ký môn nào của Thầy/Cô X. TUYỆT ĐỐI KHÔNG liệt kê toàn bộ các lớp giảng viên đó dạy cho người khác.
4. TƯ VẤN & GỢI Ý MÔN HỌC:
   - CHỈ đề xuất môn khi sinh viên nói rõ 'gợi ý môn', 'tư vấn học gì', 'recommend courses'.
   - Môn đề xuất phải thỏa mãn điều kiện tiên quyết, chưa từng học qua, còn chỗ trống và KHÔNG BỊ TRÙNG LỊCH với các lớp đã đăng ký.
5. HƯỚNG DẪN THAO TÁC GIAO DIỆN:
   - Đăng xuất: Bấm vào avatar/tên ở góc phải Navbar -> chọn 'Đăng xuất'.
   - Đổi mật khẩu: Bấm vào avatar -> chọn 'Đổi mật khẩu' (/change-password).
   - Thanh toán học phí: Vào mục 'Thanh toán học phí' (/tuition) để quét mã VietQR MB Bank.
6. CÂU HỎI TIẾP NỐI & NGOÀI PHẠM VI:
   - Nếu câu hỏi tiếp nối ('Cái đó học lúc mấy giờ?'): trả lời thời gian của môn học đã đề cập trước đó. Nếu không rõ môn nào, hỏi lại tự nhiên.
   - Nếu câu hỏi ngoài phạm vi (thời tiết, giải trí...): từ chối lịch sự, nêu rõ phạm vi hỗ trợ học vụ tại ĐH Kinh tế Huế.
7. QUYỀN RIÊNG TƯ:
   - Sinh viên chỉ được tra cứu dữ liệu của chính mình. Nếu hỏi thông tin cá nhân của bạn sinh viên khác, hãy từ chối lịch sự theo quy chế bảo mật.";
        }

        /// <summary>
        /// System Prompt dành cho Phòng Đào tạo / Quản trị (Admin):
        /// - Trả lời ngắn gọn, chuyên nghiệp về thống kê sĩ số, phân tích mở lớp, cảnh báo trùng lịch, công nợ học phí.
        /// </summary>
        private static string BuildAdminSystemPrompt(
            List<Section> allSections,
            List<Course> allCourses,
            List<Student> allStudents,
            string semester,
            List<Enrollment>? allSemesterEnrollments = null)
        {
            var fullSections = allSections.Where(s => s.CurrentEnrollment >= s.MaxCapacity).ToList();
            var lowSections = allSections.Where(s => s.CurrentEnrollment < 10).ToList();

            var sectionsSummary = string.Join("\n", allSections.Select(s =>
                $"- Lớp {s.SectionCode}: {s.Course?.CourseName} ({s.Course?.CourseCode}) | GV: {s.Instructor} | Phòng: {s.Room} | Sĩ số: {s.CurrentEnrollment}/{s.MaxCapacity} | Kỳ: {s.Semester} | Lịch: {FormatSchedule(s)}"));

            int totalEnr = allSemesterEnrollments?.Count ?? 0;
            int paidEnr = allSemesterEnrollments?.Count(e => e.IsPaid) ?? 0;
            int unpaidEnr = allSemesterEnrollments?.Count(e => !e.IsPaid) ?? 0;

            return $@"Bạn là Trợ lý AI Phân tích & Quản trị Đào tạo (Dành cho Cán bộ Phòng Đào Tạo / Ban Quản Trị) tại Trường Đại học Kinh tế Huế.

[PHONG CÁCH GIAO TIẾP]
- Tác phong hành chính giáo dục: Chuyên nghiệp, súc tích, dựa trên số liệu thực tế, định dạng bảng biểu hoặc gạch đầu dòng rõ ràng.
- Trả lời thẳng vào trọng tâm câu hỏi quản trị.
- TUYỆT ĐỐI KHÔNG tự động tuôn ra danh sách các câu hỏi mẫu dài dòng.

[DỮ LIỆU TỔNG QUAN KỲ {semester}]
- Tổng số lớp học phần mở: {allSections.Count} lớp.
- Tổng số môn học: {allCourses.Count} môn.
- Tổng số sinh viên: {allStudents.Count} sinh viên.
- Thống kê học phí: {totalEnr} phiếu đăng ký, {paidEnr} sinh viên đã nộp, {unpaidEnr} sinh viên chưa nộp.
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

        #region Natural Language Yes/No Helpers & General Academic Knowledge Engine

        private static bool IsYesNoQuery(string qNorm, string qLower)
        {
            if (string.IsNullOrWhiteSpace(qNorm)) return false;

            // Dấu hiệu câu hỏi Có / Không trong tiếng Việt
            if (qNorm.Contains("co ") && (qNorm.Contains(" khong") || qNorm.Contains(" ko") || qNorm.EndsWith(" k") || qNorm.Contains(" k ") || qNorm.Contains(" chua")))
                return true;
            if (qNorm.EndsWith("khong") || qNorm.EndsWith("khong?") || qNorm.EndsWith("ko") || qNorm.EndsWith("ko?") || qNorm.EndsWith("k?"))
                return true;
            if (qNorm.Contains("phai khong") || qNorm.Contains("dung khong") || qNorm.Contains("duoc khong") || qNorm.Contains("het cho chua") || qNorm.Contains("con cho khong"))
                return true;
            if (qNorm.Contains("co mon nao khong") || qNorm.Contains("co hoc khong") || qNorm.Contains("co lop khong") || qNorm.Contains("co mo khong") || qNorm.Contains("co day khong") || qNorm.Contains("co trung khong"))
                return true;

            // Dấu hiệu câu hỏi Yes / No trong tiếng Anh
            if (qLower.StartsWith("do ") || qLower.StartsWith("does ") || qLower.StartsWith("is ") || qLower.StartsWith("are ") ||
                qLower.StartsWith("can ") || qLower.StartsWith("will ") || qLower.StartsWith("did ") || qLower.StartsWith("has ") || qLower.StartsWith("have "))
                return true;
            if (qLower.Contains("do i have") || qLower.Contains("is there") || qLower.Contains("are there") || qLower.Contains("does he") || qLower.Contains("does she") || qLower.Contains("is it") || qLower.Contains("or not"))
                return true;

            return false;
        }

        private static bool IsConversationalAffirmationOrNegationIntent(string qNorm, string qLower, out bool isAffirmative)
        {
            isAffirmative = false;
            string trimmed = qNorm.Trim().TrimEnd('.', '!', '?', ',');
            string trimmedLower = qLower.Trim().TrimEnd('.', '!', '?', ',');

            string[] affirmativePatterns = new[]
            {
                "co", "co nha", "co chu", "co chu sao ko", "co chu sao khong", "co nhe", "co chu a",
                "da co", "da co a", "da vang", "vang", "vang a", "vang dung roi",
                "dung", "dung roi", "dung roi a", "chuan", "chuan roi", "chinh xac",
                "ok", "oke", "okay", "oki", "ok nhe", "duoc", "duoc chu", "duoc chu sao khong",
                "uh", "u", "uhm", "uh dung roi",
                "yes", "yeah", "yep", "yup", "sure", "correct", "right", "of course", "yes please", "sure thing", "sounds good"
            };

            foreach (var p in affirmativePatterns)
            {
                if (trimmed == p || trimmedLower == p)
                {
                    isAffirmative = true;
                    return true;
                }
            }

            string[] negationPatterns = new[]
            {
                "khong", "ko", "k", "khong nha", "khong nhe", "khong a", "da khong", "da khong a",
                "chua", "chua nha", "chua nhe", "chua a", "da chua",
                "khong can", "ko can", "thoi", "thoi khoi", "thoi khong can", "het roi", "khong con gi",
                "no", "nope", "nah", "not really", "no thanks", "none", "nothing", "no need"
            };

            foreach (var p in negationPatterns)
            {
                if (trimmed == p || trimmedLower == p)
                {
                    isAffirmative = false;
                    return true;
                }
            }

            return false;
        }

        private static string BuildConversationalYesNoResponse(
            bool isAffirmative,
            bool isEnglish,
            Student? currentStudent,
            string semester,
            ConversationContextInfo context)
        {
            if (isEnglish)
            {
                if (isAffirmative)
                {
                    if (context.LastReferencedCourse != null)
                    {
                        return $"✅ **Yes, absolutely!**\n\nRegarding **{context.LastReferencedCourse.CourseName}** (`{context.LastReferencedCourse.CourseCode}`), would you like me to check open sections, schedules, lecturers, or show you how to register it in your cart?";
                    }
                    if (context.LastReferencedSection != null)
                    {
                        return $"✅ **Yes, understood!**\n\nFor section `{context.LastReferencedSection.SectionCode}` ({context.LastReferencedSection.Course?.CourseName}), would you like me to guide you to the Course Registration page (`/sections`) or check for potential timetable conflicts?";
                    }

                    return $"✅ **Yes, noted!**\n\nHow can I best assist you further? You can ask me:\n" +
                           $"- 🗓️ *\"Do I have any classes tomorrow?\"* or *\"What is my schedule this week?\"*\n" +
                           $"- 📘 *\"What courses are open this semester?\"*\n" +
                           $"- 👨‍🏫 *\"Which classes does Dr. Ha Ngoc Long teach?\"*\n" +
                           $"- 💰 *\"How much is tuition fee per credit?\"*";
                }
                else
                {
                    return $"✨ **Understood! No problem at all.**\n\n" +
                           $"If you need any academic advising, timetable checks, or tuition fee guidance later, feel free to ask anytime. Wishing you a successful semester {semester} at Hue University of Economics!";
                }
            }
            else
            {
                if (isAffirmative)
                {
                    if (context.LastReferencedCourse != null)
                    {
                        return $"✅ **Dạ vâng! Tuyệt vời.**\n\n" +
                               $"Về học phần **{context.LastReferencedCourse.CourseName}** (`{context.LastReferencedCourse.CourseCode}`), bạn có muốn Cố vấn AI kiểm tra danh sách các lớp đang mở, lịch học chi tiết hay điều kiện tiên quyết không?";
                    }
                    if (context.LastReferencedSection != null)
                    {
                        return $"✅ **Dạ vâng! Cố vấn AI đã ghi nhận.**\n\n" +
                               $"Về lớp học phần `{context.LastReferencedSection.SectionCode}` ({context.LastReferencedSection.Course?.CourseName}), bạn có muốn tôi hướng dẫn cách thêm vào giỏ đăng ký hoặc kiểm tra xem có bị trùng lịch với các môn khác của bạn không?";
                    }

                    string name = currentStudent != null ? $" bạn **{currentStudent.FullName}**" : " bạn";
                    return $"✅ **Dạ vâng! Rất sẵn sàng hỗ trợ{name}.**\n\n" +
                           $"Bạn đang quan tâm đến nội dung học vụ nào? Bạn có thể hỏi tôi:\n" +
                           $"- 🗓️ *\"Ngày mai tôi có học không?\"* hoặc *\"Thứ 2 tôi học gì?\"*\n" +
                           $"- 📘 *\"Kỳ này có mở môn Lập trình Web không?\"*\n" +
                           $"- 👨‍🏫 *\"Thầy Hà Ngọc Long dạy những lớp nào?\"*\n" +
                           $"- 💰 *\"Học phí 1 tín chỉ bao nhiêu tiền và nộp qua đâu?\"*\n" +
                           $"- 💡 *\"Gợi ý đăng ký học phần tối ưu cho tôi\"*";
                }
                else
                {
                    string name = currentStudent != null ? $" bạn **{currentStudent.FullName}**" : " bạn";
                    return $"✨ **Dạ vâng, không có vấn đề gì cả! Cố vấn AI đã hiểu ý{name}.**\n\n" +
                           $"Nếu trong quá trình học tập hoặc đăng ký tín chỉ tại Trường Đại học Kinh tế - Đại học Huế bạn cần hỗ trợ thêm thông tin gì (môn học, lịch học, giảng viên hay học phí), cứ nhắn cho Cố vấn AI bất cứ lúc nào nhé!\n\n" +
                           $"Chúc bạn luôn có những kỳ học thật thuận lợi và đạt kết quả cao nhất!";
                }
            }
        }

        private static List<(Section Sec1, Section Sec2, int DayOfWeek, int StartPeriod, int EndPeriod)> FindScheduleConflicts(List<Section> sections)
        {
            var conflicts = new List<(Section, Section, int, int, int)>();
            for (int i = 0; i < sections.Count; i++)
            {
                var s1 = sections[i];
                if (s1.Schedules == null) continue;
                for (int j = i + 1; j < sections.Count; j++)
                {
                    var s2 = sections[j];
                    if (s2.Schedules == null) continue;

                    foreach (var sch1 in s1.Schedules)
                    {
                        foreach (var sch2 in s2.Schedules)
                        {
                            if (sch1.DayOfWeek == sch2.DayOfWeek)
                            {
                                int overlapStart = Math.Max(sch1.StartPeriod, sch2.StartPeriod);
                                int overlapEnd = Math.Min(sch1.EndPeriod, sch2.EndPeriod);
                                if (overlapStart <= overlapEnd)
                                {
                                    conflicts.Add((s1, s2, sch1.DayOfWeek, overlapStart, overlapEnd));
                                }
                            }
                        }
                    }
                }
            }
            return conflicts;
        }

        private static bool TryAnswerGeneralAcademicKnowledge(
            string qNorm,
            string qLower,
            string qRaw,
            bool isEnglish,
            Student? currentStudent,
            string semester,
            out string response)
        {
            response = string.Empty;

            // 1. Các ngành đào tạo tại Trường Đại học Kinh tế - Đại học Huế
            bool asksMajors =
                qNorm.Contains("cac nganh") || qNorm.Contains("nganh dao tao") || qNorm.Contains("co nhung nganh") ||
                qNorm.Contains("nganh hoc") || qNorm.Contains("tin hoc kinh te") || qNorm.Contains("he thong thong tin") ||
                qNorm.Contains("thuong mai dien tu") || qNorm.Contains("kinh te so") || qNorm.Contains("quan tri kinh doanh") ||
                qNorm.Contains("tai chinh ngan hang") || qNorm.Contains("ke toan") || qNorm.Contains("logistics") ||
                qLower.Contains("what majors") || qLower.Contains("degree programs") || qLower.Contains("available majors");

            if (asksMajors)
            {
                response = isEnglish
                    ? "### 🎓 Academic Programs & Majors at Hue University of Economics (HCE):\n\n" +
                      "Hue University of Economics offers diverse undergraduate programs structured under modern credit-based training:\n\n" +
                      "1. **Information Technology & Digital Economics:**\n" +
                      "   - **Economic Informatics (Tin học Kinh tế):** Integrating IT applications, software engineering, and data analysis in economic management.\n" +
                      "   - **Management Information Systems (MIS):** Enterprise resource planning, business intelligence, and database design.\n" +
                      "   - **E-Commerce (Thương mại điện tử):** Digital marketing, online business models, and payment gateways.\n" +
                      "   - **Digital Economy (Kinh tế số):** Data-driven economy, fintech, and digital transformation.\n\n" +
                      "2. **Management & Business Administration:**\n" +
                      "   - Business Administration, Marketing, International Business, Logistics & Supply Chain Management.\n\n" +
                      "3. **Finance, Banking & Accounting:**\n" +
                      "   - Finance - Banking, Accounting, Auditing, Economic Law.\n\n" +
                      "💡 *Training duration: 4 years (130 - 135 credits). Students graduate with a Bachelor's degree and strong industry readiness.*"
                    : "### 🎓 Các Ngành Đào Tạo Tiêu Biểu Tại Trường Đại học Kinh tế - Đại học Huế (HCE):\n\n" +
                      "Trường Đại học Kinh tế – Đại học Huế đào tạo theo hệ thống tín chỉ hiện đại với 3 khối ngành trọng điểm:\n\n" +
                      "1. 💻 **Khối Công Nghệ Thông Tin & Kinh Tế Số:**\n" +
                      "   - **Tin học Kinh tế:** Chuyên sâu về ứng dụng công nghệ thông tin, phát triển phần mềm ứng dụng, phân tích dữ liệu kinh doanh và chuyển đổi số doanh nghiệp.\n" +
                      "   - **Hệ thống thông tin quản lý (MIS):** Thiết kế và quản trị hệ thống ERP, quản trị CSDL lớn, phân tích nghiệp vụ kinh doanh (BA).\n" +
                      "   - **Thương mại điện tử (E-Commerce):** Kinh doanh số, Digital Marketing, sàn giao dịch trực tuyến và thanh toán số.\n" +
                      "   - **Kinh tế số:** Mô hình kinh tế dữ liệu, công nghệ tài chính (FinTech) và quản trị số.\n\n" +
                      "2. 📈 **Khối Quản Trị & Kinh Doanh:**\n" +
                      "   - **Quản trị kinh doanh**, **Marketing**, **Logistics & Quản lý chuỗi cung ứng**, **Kinh doanh thương mại**, **Kinh tế quốc tế**.\n\n" +
                      "3. 💰 **Khối Tài Chính - Kế Toán & Luật:**\n" +
                      "   - **Tài chính - Ngân hàng**, **Kế toán - Kiểm toán**, **Kinh tế học**, **Luật kinh tế**.\n\n" +
                      "💡 *Thời gian đào tạo: 4 năm (130 - 135 tín chỉ). Sinh viên được trang bị kiến thức chuyên môn, thực hành thực tế và chuẩn đầu ra ngoại ngữ/tin học vững vàng.*";
                return true;
            }

            // 2. Cách tính điểm trung bình GPA / CPA thang điểm 4 & Quy đổi điểm chữ
            bool asksGpa =
                qNorm.Contains("cach tinh diem") || qNorm.Contains("tinh gpa") || qNorm.Contains("tinh cpa") ||
                qNorm.Contains("thang diem 4") || qNorm.Contains("quy doi diem") || qNorm.Contains("diem chu") ||
                qNorm.Contains("diem a") || qNorm.Contains("diem b") || qNorm.Contains("diem c") ||
                qNorm.Contains("diem d") || qNorm.Contains("diem f") || qNorm.Contains("gpa") ||
                qLower.Contains("grading scale") || qLower.Contains("calculate gpa") || qLower.Contains("cpa calculation");

            if (asksGpa)
            {
                response = isEnglish
                    ? "### 📊 GPA / CPA Calculation & 4.0 Grading Scale Guide:\n\n" +
                      "Under university credit regulations, student academic standing is evaluated using letter grades and the **4.0 GPA scale**:\n\n" +
                      "1. **Conversion from 10-Point Scale to Letter & 4.0 Scale:**\n" +
                      "   - `8.5 - 10.0`: **Grade A** (4.0) — Excellent / Outstanding\n" +
                      "   - `7.0 - 8.4`: **Grade B** (3.0) — Good\n" +
                      "   - `5.5 - 6.9`: **Grade C** (2.0) — Average\n" +
                      "   - `4.0 - 5.4`: **Grade D** (1.0) — Below Average (Passed)\n" +
                      "   - `< 4.0`: **Grade F** (0.0) — Failed (Must retake course)\n\n" +
                      "2. **Formula:**\n" +
                      "   $$\\text{GPA} = \\frac{\\sum (\\text{Grade Point (4.0)} \\times \\text{Course Credits})}{\\sum \\text{Total Registered Credits}}$$\n\n" +
                      "3. **Academic Classification:**\n" +
                      "   - **High Distinction:** GPA `3.60 - 4.00`\n" +
                      "   - **Distinction:** GPA `3.20 - 3.59`\n" +
                      "   - **Credit:** GPA `2.50 - 3.19`\n" +
                      "   - **Pass:** GPA `2.00 - 2.49`\n" +
                      "   - **Weak / Academic Warning:** GPA `< 2.00`."
                    : "### 📊 Hướng Dẫn Cách Tính Điểm Trung Bình GPA / CPA Thang Điểm 4:\n\n" +
                      "Theo quy chế đào tạo tín chỉ tại Trường Đại học Kinh tế - Đại học Huế, kết quả học tập được đánh giá qua điểm chữ và quy đổi sang **thang điểm 4.0** như sau:\n\n" +
                      "1. **Bảng quy đổi từ thang điểm 10 sang thang điểm chữ và hệ 4:**\n" +
                      "   - `8.5 – 10.0`: **Điểm A** (Hệ 4: **4.0**) — Xếp loại Giỏi / Xuất sắc\n" +
                      "   - `7.0 – 8.4`: **Điểm B** (Hệ 4: **3.0**) — Xếp loại Khá\n" +
                      "   - `5.5 – 6.9`: **Điểm C** (Hệ 4: **2.0**) — Xếp loại Trung bình\n" +
                      "   - `4.0 – 5.4`: **Điểm D** (Hệ 4: **1.0**) — Xếp loại Trung bình yếu (Đạt môn)\n" +
                      "   - `Dưới 4.0`: **Điểm F** (Hệ 4: **0.0**) — Không đạt (Buộc phải đăng ký học lại)\n\n" +
                      "2. **Công thức tính điểm trung bình (GPA học kỳ hoặc CPA tích lũy):**\n" +
                      "   $$\\text{GPA} = \\frac{\\sum (\\text{Điểm môn hệ 4} \\times \\text{Số tín chỉ môn})}{\\sum \\text{Tổng số tín chỉ tính điểm}}$$\n\n" +
                      "3. **Xếp loại học lực chung:**\n" +
                      "   - **Xuất sắc:** GPA từ `3.60` đến `4.00`\n" +
                      "   - **Giỏi:** GPA từ `3.20` đến `3.59`\n" +
                      "   - **Khá:** GPA từ `2.50` đến `3.19`\n" +
                      "   - **Trung bình:** GPA từ `2.00` đến `2.49`\n" +
                      "   - **Yếu (Nguy cơ cảnh báo học vụ):** GPA dưới `2.00`.";
                return true;
            }

            // 3. Mẹo / Kinh nghiệm đăng ký tín chỉ nhanh
            bool asksRegTips =
                qNorm.Contains("meo dang ky") || qNorm.Contains("dang ky nhanh") || qNorm.Contains("kinh nghiem dang ky") ||
                qNorm.Contains("bi quyet dang ky") || qNorm.Contains("nghen mang") || qNorm.Contains("bi lag") ||
                qNorm.Contains("bi full") || qNorm.Contains("het cho") || qLower.Contains("registration tips") ||
                qLower.Contains("how to register fast");

            if (asksRegTips)
            {
                response = isEnglish
                    ? "### ⚡ Expert Tips for Fast Course Registration (Hue University of Economics):\n\n" +
                      "1. **Prepare Plan A & Plan B:** Always prepare backup section codes in advance in case your primary choice fills up.\n" +
                      "2. **Check Prerequisites Early:** Verify that all prerequisite courses have been completed and passed (grade >= 5.0) to prevent registration locks.\n" +
                      "3. **Pre-stage in Registration Cart:** Log in 10-15 minutes before the opening window, stage your intended classes into `/cart`, and verify no timetable overlap.\n" +
                      "4. **Fast Click upon Gate Opening:** Keep your browser refreshed right at the opening second, click **'Confirm Registration'** immediately.\n" +
                      "5. **Post-Registration Check:** Visit `/schedule` to inspect your confirmed timetable and complete tuition payment via VietQR Napas 24/7."
                    : "### ⚡ Bí Quyết & Mẹo Đăng Ký Tín Chỉ Nhanh, Không Lo Nghẽn Mạng:\n\n" +
                      "Để đăng ký được các lớp học phần như ý và tránh tình trạng hết chỗ tại Cổng đăng ký Trường Đại học Kinh tế Huế, bạn nên áp dụng các mẹo sau:\n\n" +
                      "1. **Chuẩn bị trước Phương án A và Phương án B:** Trước ngày mở cổng, tra cứu danh sách lớp (`/sections`), ghi sẵn mã lớp chính muốn học và luôn chuẩn bị sẵn 1-2 mã lớp dự phòng nếu lớp chính đầy.\n" +
                      "2. **Kiểm tra học phần tiên quyết:** Đảm bảo bạn đã hoàn thành môn tiên quyết (điểm >= 5.0). Hệ thống sẽ chặn ngay nếu cố tình ghi danh môn chưa đủ điều kiện.\n" +
                      "3. **Thêm trước vào 'Giỏ đăng ký' (`/cart`):** Trước giờ mở cổng 10-15 phút, hãy thêm các lớp học phần dự kiến vào giỏ và xem Cố vấn AI kiểm tra trùng lịch.\n" +
                      "4. **Thao tác đúng giờ mở cổng:** Vào mạng Wi-Fi/4G ổn định, canh đồng hồ chính xác từng giây, bấm nút **'Xác Nhận Đăng Ký'** ngay khi hệ thống mở.\n" +
                      "5. **Kiểm tra và hoàn tất học phí:** Sau khi xác nhận thành công, vào mục **'Thời Khóa Biểu'** (`/schedule`) hoặc **'Thanh Toán Học Phí'** (`/tuition`) quét mã VietQR để hoàn tất công nợ (450.000 VNĐ/TC).";
                return true;
            }

            // 4. Chính sách xét cấp Học bổng khuyến khích học tập
            bool asksScholarship =
                qNorm.Contains("hoc bong") || qNorm.Contains("dieu kien hoc bong") || qNorm.Contains("xet hoc bong") ||
                qNorm.Contains("hoc bong khuyen khich") || qLower.Contains("scholarship") || qLower.Contains("scholarship requirements");

            if (asksScholarship)
            {
                response = isEnglish
                    ? "### 🏆 Academic Encouragement Scholarship Regulations (Hue University of Economics):\n\n" +
                      "- **Eligibility Requirements:**\n" +
                      "  + Successfully enrolled in at least **14 credits** in the regular semester.\n" +
                      "  + No failing grades (Grade F) or disciplinary actions during the semester.\n" +
                      "  + Training Evaluation Score (Điểm rèn luyện) must be at least **Good (>= 70)**.\n\n" +
                      "- **Scholarship Tiers:**\n" +
                      "  + **High Distinction (Xuất sắc):** GPA `>= 3.60` AND Training Score `>= 90`.\n" +
                      "  + **Distinction (Giỏi):** GPA `>= 3.20` AND Training Score `>= 80`.\n" +
                      "  + **Credit (Khá):** GPA `>= 2.50` AND Training Score `>= 70`.\n\n" +
                      "💡 *Scholarships are granted from top to bottom according to the Faculty/Major scholarship budget.*"
                    : "### 🏆 Chính Sách Xét Cấp Học Bổng Khuyến Khích Học Tập (Trường ĐH Kinh Tế Huế):\n\n" +
                      "Học bổng khuyến khích học tập được xét và trao vào cuối mỗi học kỳ chính theo các tiêu chí sau:\n\n" +
                      "1. **Điều kiện cần để được đưa vào danh sách xét:**\n" +
                      "   - Đăng ký và tích lũy tối thiểu từ **14 tín chỉ** trở lên trong học kỳ chính.\n" +
                      "   - Không có học phần nào bị điểm F (phải học lại hoặc thi lại) trong học kỳ xét.\n" +
                      "   - Điểm rèn luyện đạt từ loại Khá trở lên (từ 70 điểm trở lên).\n\n" +
                      "2. **Các mức học bổng khuyến khích:**\n" +
                      "   - **Học bổng Xuất sắc:** GPA đạt từ **3.60 trở lên** VÀ Điểm rèn luyện đạt từ **90 điểm trở lên**.\n" +
                      "   - **Học bổng Giỏi:** GPA đạt từ **3.20 đến 3.59** VÀ Điểm rèn luyện đạt từ **80 đến 89 điểm**.\n" +
                      "   - **Học bổng Khá:** GPA đạt từ **2.50 đến 3.19** VÀ Điểm rèn luyện đạt từ **70 đến 79 điểm**.\n\n" +
                      "💡 *Nguyên tắc xét: Xét thứ tự điểm GPA từ cao xuống thấp cho đến khi hết quỹ học bổng của từng Khoa/Ngành trong từng khóa học.*";
                return true;
            }

            // 5. Chuẩn đầu ra Ngoại ngữ & Tin học
            bool asksExitStandards =
                qNorm.Contains("chuan dau ra") || qNorm.Contains("ngoai ngu") || qNorm.Contains("tieng anh") ||
                qNorm.Contains("vstep") || qNorm.Contains("chung chi tin hoc") || qNorm.Contains("ic3") ||
                qNorm.Contains("mos") || qLower.Contains("exit standards") || qLower.Contains("graduation requirements");

            if (asksExitStandards)
            {
                response = isEnglish
                    ? "### 📜 Graduation Exit Standards at Hue University of Economics:\n\n" +
                      "To be eligible for graduation and bachelor's degree conferral, students must fulfill:\n\n" +
                      "1. **Foreign Language Standard:**\n" +
                      "   - VSTEP Level 3 (B1) according to Vietnam's 6-level foreign language framework, or international equivalents (TOEIC 450 - 500+, IELTS 4.5+ depending on major).\n\n" +
                      "2. **Informatics Standard:**\n" +
                      "   - Basic IT Application Certificate (Circular 03/2014/TT-BTTTT) or international certificates (MOS / IC3).\n\n" +
                      "3. **Academic & Physical/Military Education:**\n" +
                      "   - Complete all required curriculum credits (CPA >= 2.00, no failing core courses).\n" +
                      "   - Complete Physical Education and National Defense Education certificates."
                    : "### 📜 Chuẩn Đầu Ra Tốt Nghiệp Tại Trường Đại học Kinh tế - Đại học Huế:\n\n" +
                      "Để đủ điều kiện xét tốt nghiệp và nhận bằng Cử nhân, sinh viên cần hoàn thành các chuẩn đầu ra sau:\n\n" +
                      "1. 🌐 **Chuẩn đầu ra Ngoại ngữ:**\n" +
                      "   - Đạt chứng chỉ **VSTEP Bậc 3 (B1)** theo khung năng lực ngoại ngữ 6 bậc của Bộ GD&ĐT, hoặc các chứng chỉ quốc tế tương đương (TOEIC từ 450 – 500+ điểm tùy chuyên ngành, IELTS từ 4.5+).\n" +
                      "   - Sinh viên các ngành chất lượng cao hoặc tiếng Anh thương mại có chuẩn đầu ra TOEIC từ 600+.\n\n" +
                      "2. 💻 **Chuẩn đầu ra Tin học:**\n" +
                      "   - Đạt chứng chỉ Ứng dụng CNTT cơ bản theo Thông tư 03/2014/TT-BTTTT, hoặc chứng chỉ quốc tế **MOS (Word, Excel, PowerPoint)** / **IC3**.\n\n" +
                      "3. 🏅 **Điều kiện học vụ khác:**\n" +
                      "   - Tích lũy đủ số tín chỉ chương trình đào tạo (130 - 135 TC), điểm CPA tích lũy từ **2.00 trở lên**.\n" +
                      "   - Hoàn thành chứng chỉ Giáo dục Quốc phòng - An ninh và Giáo dục Thể chất.";
                return true;
            }

            // 6. Thủ tục hành chính sinh viên (Giấy xác nhận, bảng điểm, vay vốn, tạm hoãn NVQS)
            bool asksProcedures =
                qNorm.Contains("xin bang diem") || qNorm.Contains("giay xac nhan") || qNorm.Contains("xac nhan sinh vien") ||
                qNorm.Contains("vay von") || qNorm.Contains("nghia vu quan su") || qNorm.Contains("nvqs") ||
                qNorm.Contains("tam hoan") || qNorm.Contains("the sinh vien") || qNorm.Contains("thu tuc sinh vien") ||
                qLower.Contains("student verification") || qLower.Contains("student certificate") || qLower.Contains("transcript request");

            if (asksProcedures)
            {
                response = isEnglish
                    ? "### 🏢 Student Administrative Services & Office Procedures:\n\n" +
                      "1. **Student Status Certificate (for military postponement, student loan, bus pass):**\n" +
                      "   - Contact the One-Stop Student Affairs Office (Phòng Công tác Sinh viên) at 99 Ho Dac Di, Hue City.\n" +
                      "   - Processing time: 1 - 2 working days with university official seal.\n\n" +
                      "2. **Official Academic Transcripts:**\n" +
                      "   - Submit request at Academic Affairs Office (Phòng Đào Tạo).\n\n" +
                      "💡 *Bring your Student ID card or National ID for in-person verification.*"
                    : "### 🏢 Hướng Dẫn Thủ Tục Hành Chính Học Vụ Dành Cho Sinh Viên:\n\n" +
                      "1. 📝 **Xin cấp Giấy xác nhận sinh viên** (Tạm hoãn NVQS, vay vốn ngân hàng chính sách, làm vé xe buýt):\n" +
                      "   - Sinh viên liên hệ Bộ phận Một cửa — **Phòng Công tác Sinh viên (CTSV)** (Nhà Hiệu bộ, Số 99 Hồ Đắc Di, TP. Huế).\n" +
                      "   - Mang theo Thẻ sinh viên hoặc CCCD để nhận giấy xác nhận có mộc đỏ nhà trường trong 1 – 2 ngày làm việc.\n\n" +
                      "2. 📊 **Xin cấp Bảng điểm học tập / Giấy chứng nhận tốt nghiệp tạm thời:**\n" +
                      "   - Đăng ký tại **Phòng Đào Tạo Trường Đại học Kinh tế Huế**.\n\n" +
                      "3. 🚪 **Bảo lưu kết quả học tập / Xin nghỉ học tạm thời:**\n" +
                      "   - Làm đơn theo mẫu của Phòng Đào Tạo, xin ý kiến Cố vấn học tập và nộp hồ sơ trước thời hạn quy định.";
                return true;
            }

            // 7. Thực tập tốt nghiệp & Khóa luận tốt nghiệp
            bool asksThesis =
                qNorm.Contains("thuc tap") || qNorm.Contains("khoa luan") || qNorm.Contains("do an tot nghiep") ||
                qNorm.Contains("chuyen de tot nghiep") || qLower.Contains("internship") || qLower.Contains("thesis") ||
                qLower.Contains("graduation thesis");

            if (asksThesis)
            {
                response = isEnglish
                    ? "### 🎓 Graduation Internship & Thesis Guidelines (Hue University of Economics):\n\n" +
                      "- **Graduation Thesis (Khóa luận tốt nghiệp):**\n" +
                      "  + Eligibility: Minimum 105 - 110 accumulated credits, CPA >= 2.80 - 3.00, and no academic warnings.\n" +
                      "  + Weight: Equivalent to 6 - 9 credits.\n\n" +
                      "- **Graduation Internship & Graduation Alternatives:**\n" +
                      "  + Students who do not undertake a thesis participate in enterprise internships and complete alternative specialized courses.\n" +
                      "- **Timeframe:** Second semester of the final fourth academic year."
                    : "### 🎓 Quy Định Về Thực Tập Doanh Nghiệp & Khóa Luận Tốt Nghiệp:\n\n" +
                      "Vào năm thứ 4 (học kỳ 2), sinh viên Trường Đại học Kinh tế Huế sẽ thực hiện một trong hai hình thức tốt nghiệp:\n\n" +
                      "1. 📘 **Làm Khóa luận tốt nghiệp:**\n" +
                      "   - Điều kiện: Tích lũy tối thiểu từ **105 – 110 tín chỉ**, điểm CPA tích lũy đạt từ **2.80 trở lên** (tùy khoa), không bị kỷ luật học vụ.\n" +
                      "   - Sinh viên được phân công giảng viên hướng dẫn chuyên môn và bảo vệ trước Hội đồng chấm khóa luận.\n\n" +
                      "2. 🏢 **Thực tập tốt nghiệp & Học phần thay thế:**\n" +
                      "   - Dành cho sinh viên không làm khóa luận: Thực tập tại doanh nghiệp/ngân hàng/cơ quan, làm Báo cáo thực tập chuyên đề và học bổ sung 2 môn chuyên ngành thay thế.";
                return true;
            }

            // 8. Phương pháp học đại học hiệu quả
            bool asksStudyMethods =
                qNorm.Contains("phuong phap hoc") || qNorm.Contains("cach hoc dai hoc") || qNorm.Contains("cach hoc tot") ||
                qNorm.Contains("kinh nghiem hoc") || qNorm.Contains("hoc tap hieu qua") || qLower.Contains("study tips") ||
                qLower.Contains("how to study");

            if (asksStudyMethods)
            {
                response = isEnglish
                    ? "### 💡 Effective University Study Methods (Hue University of Economics Advisor):\n\n" +
                      "1. **Preview Before Lectures:** Skim through slide decks and lecture notes before each class.\n" +
                      "2. **Master Active Recall & Spaced Repetition:** Review weekly instead of cramming before final exam week.\n" +
                      "3. **Collaborative Study & Case Studies:** Engage in group projects, business case discussions, and lab assignments.\n" +
                      "4. **Leverage University Resources:** Visit HCE Library and Hue University digital library for rich research papers and textbooks."
                    : "### 💡 Lời Khuyên & Phương Pháp Học Đại Học Hiệu Quả Từ Cố Vấn AI:\n\n" +
                      "Học đại học theo tín chỉ đòi hỏi tinh thần chủ động cao. Dưới đây là các phương pháp học tập hiệu quả dành cho sinh viên ĐH Kinh tế Huế:\n\n" +
                      "1. 📖 **Đọc giáo trình trước buổi học:** Dành 15-20 phút xem qua slide bài giảng của thầy/cô để nắm khung kiến thức trước khi lên lớp.\n" +
                      "2. 👥 **Học nhóm và thảo luận tình huống (Case Study):** Các môn kinh tế và CNTT đòi hỏi làm bài tập lớn và dự án thực tế; học nhóm giúp trao đổi góc nhìn và giải quyết vấn đề nhanh chóng.\n" +
                      "3. 🗓️ **Quản lý thời gian học tập đều đặn:** Ôn tập theo từng tuần, làm đầy đủ bài tập thực hành, tuyệt đối tránh dồn bài sát ngày thi cuối kỳ.\n" +
                      "4. 📚 **Khai thác thư viện trường:** Tận dụng nguồn giáo trình và tài liệu số phong phú tại Thư viện Trường Đại học Kinh tế Huế (99 Hồ Đắc Di).";
                return true;
            }

            return false;
        }

        private static string GenerateSynthesizedAiAcademicResponse(
            string qRaw,
            string qNorm,
            string qLower,
            bool isEnglish,
            Student? currentStudent,
            string semester)
        {
            if (isEnglish)
            {
                string greeting = currentStudent != null
                    ? $"Hello **{currentStudent.FullName}** (`{currentStudent.StudentCode}` - {currentStudent.Major})!"
                    : "Hello!";

                return $"### 🤖 AI Academic Advisor Insight:\n\n" +
                       $"{greeting}\n\n" +
                       $"Regarding your question *\"{qRaw}\"*, here is helpful guidance from Hue University of Economics academic regulations:\n\n" +
                       $"- **Academic Support:** Hue University of Economics applies standard credit-based training regulations. Course registration, timetable allocations, and prerequisite policies are managed digitally.\n" +
                       $"- **Course & Schedule Enquiries:** You can explore open sections under `/sections`, stage them into `/cart`, or view confirmed timetables under `/schedule`.\n" +
                       $"- **Assistance & Inquiries:** For specific paperwork, petitions, or academic advice, you can connect directly with your Faculty Academic Advisor or the Academic Affairs Office (99 Ho Dac Di, Hue City).\n\n" +
                       $"Feel free to ask me any further questions regarding courses, lecturers, tuition, or exam schedules!";
            }
            else
            {
                string greeting = currentStudent != null
                    ? $"Chào bạn **{currentStudent.FullName}** (MSSV: `{currentStudent.StudentCode}` — Ngành {currentStudent.Major})!"
                    : "Chào bạn!";

                return $"### 🤖 Phản Hồi Từ Cố Vấn Học Thuật AI:\n\n" +
                       $"{greeting}\n\n" +
                       $"Về thắc mắc của bạn: *\"{qRaw}\"*, Cố vấn AI xin chia sẻ các thông tin và định hướng học vụ tại Trường Đại học Kinh tế - Đại học Huế như sau:\n\n" +
                       $"- 📌 **Về quy chế đào tạo tín chỉ:** Toàn bộ chương trình đào tạo của nhà trường được tổ chức theo hệ thống tín chỉ chuẩn. Bạn có thể chủ động sắp xếp kế hoạch học tập, đăng ký môn học và theo dõi lộ trình tích lũy của mình.\n" +
                       $"- 🔍 **Tra cứu trực tuyến trên hệ thống:** Bạn có thể vào mục **'Tra cứu môn học'** (`/sections`) để kiểm tra danh mục lớp mở, vào **'Thời khóa biểu'** (`/schedule`) xem lịch học chi tiết, hoặc **'Thanh toán học phí'** (`/tuition`) để kiểm tra công nợ và nộp qua VietQR MB Bank.\n" +
                       $"- 🏢 **Hỗ trợ trực tiếp từ nhà trường:** Nếu cần hỗ trợ các thủ tục hồ sơ giấy tờ, bảo lưu hay xác nhận học vụ, bạn có thể liên hệ trực tiếp **Phòng Đào Tạo** hoặc **Phòng Công tác Sinh viên** (Số 99 Hồ Đắc Di, TP. Huế).\n\n" +
                       $"Nếu bạn có câu hỏi cụ thể hơn về môn học, giảng viên, phòng học hay lịch học các ngày trong tuần, hãy nhắn cho tôi bất kỳ lúc nào nhé!";
            }
        }

        #endregion

        #endregion
    }
}

