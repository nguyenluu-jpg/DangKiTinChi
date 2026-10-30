namespace CourseRegistration.UseCases.StudentPortal.AiAdvisor
{
    /// <summary>
    /// Cấu hình dùng chung cho các UseCase gọi IChatClient (GetAiAdviceUseCase, AdminAiAssistantUseCase).
    /// Giá trị được nạp từ mục "AI" trong appsettings / User Secrets tại Program.cs (UseCases không phụ thuộc IConfiguration).
    /// </summary>
    public class AiAdvisorSettings
    {
        /// <summary>Thời gian chờ mặc định (giây) cho một lần gọi LLM. LLM local như Ollama cần nhiều hơn 3-4 giây.</summary>
        public const int DefaultTimeoutSeconds = 45;

        /// <summary>Cấu hình "AI:TimeoutSeconds". Giá trị &lt;= 0 sẽ dùng mặc định.</summary>
        public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

        public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds > 0 ? TimeoutSeconds : DefaultTimeoutSeconds);
    }

    /// <summary>
    /// Ghi log chẩn đoán khi gọi LLM thất bại. Chỉ ghi loại lỗi + thông điệp (đã cắt ngắn),
    /// tuyệt đối không ghi API key, system prompt hay nội dung hội thoại.
    /// </summary>
    internal static class AiDiagnostics
    {
        public static void LogLlmFailure(string source, Exception ex, bool timeoutTokenFired, TimeSpan timeout)
        {
            string kind;
            if (ex is OperationCanceledException && timeoutTokenFired)
                kind = $"TIMEOUT sau {timeout.TotalSeconds:0}s (tăng AI:TimeoutSeconds hoặc kiểm tra mô hình đã tải xong chưa)";
            else if (ex is OperationCanceledException)
                kind = "BỊ HỦY (không phải do timeout)";
            else if (ex is HttpRequestException || ex.InnerException is HttpRequestException)
                kind = "LỖI KẾT NỐI tới AI endpoint (dịch vụ chưa chạy / sai Endpoint / mất mạng)";
            else
                kind = "LỖI KHÁC từ IChatClient (sai ModelId, API key, hết hạn mức...)";

            Console.WriteLine($"[{source}] LLM thất bại -> dùng fallback. Nguyên nhân: {kind}. " +
                              $"{ex.GetType().Name}: {Truncate(ex.Message)}" +
                              (ex.InnerException != null ? $" | Inner: {ex.InnerException.GetType().Name}: {Truncate(ex.InnerException.Message)}" : string.Empty));
        }

        private static string Truncate(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 300 ? s : s.Substring(0, 300) + "...";
        }
    }
}
