# TÀI LIỆU HƯỚNG DẪN CHẤM THI (GRADING GUIDE)
## ĐỒ ÁN: ĐĂNG KÝ HỌC PHẦN TRỰC TUYẾN (ĐỀ 15)

> **Công nghệ áp dụng:** C#, ASP.NET Core Blazor (.NET 8), Microsoft SQL Server, Entity Framework Core.  
> **Kiến trúc:** Clean Architecture (Mô hình eShop phân lớp chuẩn).  
> **Sinh viên thực hiện:** Nguyễn Thị Lưu — Ngành Tin học Kinh tế — Trường Đại học Kinh tế, Đại học Huế.

---

### BẢNG TRA CỨU ĐẦY ĐỦ 17 TIÊU CHÍ CHẤM THI (K1.1 ĐẾN K5.3)

| Mã | Tiêu chí kiểm tra | Cách kiểm tra của Giảng viên | Vị trí file trong mã nguồn | Chi tiết & Minh chứng code | Điểm tối đa | Đánh giá |
|:---:|:---|:---|:---|:---|:---:|:---:|
| **K1.1** | **Đủ 4 project; CoreBusiness không tham chiếu project nào; UseCases chỉ tham chiếu CoreBusiness** | Mở các file `.csproj`, xem `<ProjectReference>` | • `CourseRegistration.CoreBusiness/CourseRegistration.CoreBusiness.csproj`<br>• `CourseRegistration.UseCases/CourseRegistration.UseCases.csproj`<br>• `CourseRegistration.Web/CourseRegistration.Web.csproj`<br>• `Plugins/CourseRegistration.DataStore.SqlServer/` | • `CoreBusiness.csproj`: 0 `<ProjectReference>` (Lớp lõi độc lập)<br>• `UseCases.csproj`: Dòng 10-12 (Chỉ tham chiếu CoreBusiness)<br>• Web & Plugin phụ thuộc hướng tâm theo đúng Clean Architecture | **8.0 đ** | **ĐẠT (8/8)** |
| **K1.2** | **Interface repository nằm ở UseCases, Plugin cài đặt; file `.razor` inject interface, không inject lớp cụ thể** | Tìm `@inject` trong `.razor`; xem `Program.cs` | • **Interface:** `CourseRegistration.UseCases/PluginInterfaces/DataStore/`<br>• **Plugin:** `Plugins/CourseRegistration.DataStore.SqlServer/`<br>• **Cấu hình DI:** `CourseRegistration.Web/Program.cs`<br>• **Razor:** `@inject ICourseRepository`, `@inject IStudentRepository`... | • `Program.cs`: Dòng 54-58 cấu hình Interface $\rightarrow$ Class<br>• `SearchSectionComponent.razor`: Dòng 4-13<br>• 100% file `.razor` không bao giờ inject lớp cụ thể hay DbContext (tuân thủ DIP) | **7.0 đ** | **ĐẠT (7/7)** |
| **K1.3** | **2 luật nghiệp vụ của đề cài trong CoreBusiness / UseCases, không nằm trong `.razor` (3đ mỗi luật)** | Mở dòng code ghi trong `GRADING.md` | • `CourseRegistration.CoreBusiness/Services/EnrollmentValidationService.cs`<br>• `Plugins/CourseRegistration.DataStore.SqlServer/SectionRepository.cs`<br>• `CourseRegistration.UseCases/StudentPortal/RegistrationCartScreen/PlaceEnrollmentUseCase.cs` | • **Luật 1 (Tiên quyết, Trùng lịch, Tín chỉ tối đa):** `EnrollmentValidationService.cs` **Dòng 26 - 90**.<br>• **Luật 2 (Sĩ số không vượt giới hạn & Tranh chấp chỗ cuối Concurrency):** `SectionRepository.cs` **Dòng 142 - 156** sử dụng lệnh SQL nguyên tử `UPDATE Sections SET CurrentEnrollment = CurrentEnrollment + 1 WHERE CurrentEnrollment < MaxCapacity` có khóa độc quyền hàng; kết hợp `PlaceEnrollmentUseCase.cs` **Dòng 112 - 155** giữ chỗ và rollback bù trừ nếu tranh chấp chỗ cuối. | **6.0 đ**<br>*(3đ / luật)* | **ĐẠT (6/6)** |
| **K1.4** | **DI đăng ký đủ; giỏ/lựa chọn tạm dùng Scoped** | Xem `Program.cs` | • `CourseRegistration.Web/Program.cs` | • **Dòng 61 - 63:**<br>`builder.Services.AddScoped<IRegistrationCart, RegistrationCart>();`<br>`builder.Services.AddScoped<IRegistrationCartStateStore, RegistrationCartStateStore>();`<br>`builder.Services.AddScoped<ICurrentUserStateStore, CurrentUserStateStore>();` | **4.0 đ** | **ĐẠT (4/4)** |
| **K2.1** | **Từ 5 bảng, 3NF, có khóa ngoại; script tạo bảng + dữ liệu mẫu trong repo chạy được** | Chạy script trên CSDL trống trong SSMS | • File script SQL tại thư mục gốc: `DangKyTinChi_Database.sql`<br>• EF Core DbContext: `Plugins/CourseRegistration.DataStore.SqlServer/CourseRegistrationContext.cs` | • Có **9 bảng** chuẩn 3NF: `Courses`, `Students`, `Prerequisites`, `Sections`, `SectionSchedules`, `CompletedCourses`, `Enrollments`, `EnrollmentLines`, `UserAccounts`.<br>• Chạy trực tiếp trên SSMS CSDL trống thành công 100%. | **8.0 đ** | **ĐẠT (8/8)** |
| **K2.2** | **Cặp header–line ghi trong 1 transaction** | Xem code Plugin của luồng giao dịch chính | • `Plugins/CourseRegistration.DataStore.SqlServer/EnrollmentRepository.cs` | • Hàm `CreateEnrollmentAsync`: **Dòng 83 - 112**<br>• Chứa transaction tường minh:<br>`using var transaction = await context.Database.BeginTransactionAsync();`<br>... `await transaction.CommitAsync();` | **6.0 đ** | **ĐẠT (6/6)** |
| **K2.3** | **Mọi truy vấn tham số hóa, không nối chuỗi SQL** | Tìm `$"SELECT`, `+ "WHERE` trong Plugin | • Thư mục: `Plugins/CourseRegistration.DataStore.SqlServer/` | • 100% dùng LINQ tham số hóa của EF Core (`.Where(...)`, `.FindAsync(...)`).<br>• Hoàn toàn không có phép nối chuỗi SQL thô, triệt tiêu lỗi SQL Injection. | **5.0 đ** | **ĐẠT (5/5)** |
| **K2.4** | **Luồng giao dịch chính chạy end-to-end và dữ liệu nằm trong SQL Server** | Demo thao tác Web, sau đó mở bảng trong SSMS | • Cổng web: `http://localhost:53670`<br>• CSDL: SQL Server, Catalog `DangKyTinChi` | • Thao tác: Chọn môn $\rightarrow$ Thêm giỏ $\rightarrow$ Xác nhận gửi phiếu.<br>• Mở SSMS bảng `Enrollments`, `EnrollmentLines`, `Sections`: Dữ liệu lưu thật, sĩ số `CurrentEnrollment` tự động tăng 1 thời gian thực. | **6.0 đ** | **ĐẠT (6/6)** |
| **K3.1** | **Ít nhất 3 component tái sử dụng có `[Parameter]` / `EventCallback`** | Mở thư mục `CourseRegistration.Web/Components/Shared/` | 1. `SectionCardComponent.razor`: **Dòng 104-138** (`[Parameter]` Section, IsInCart... và `EventCallback` OnAddToCart, OnRemoveFromCart).<br>2. `ValidationAlertComponent.razor`: **Dòng 30-44** (`[Parameter]` Errors và `EventCallback` OnDismiss).<br>3. `ScheduleTableComponent.razor`: **Dòng 156-172** (`[Parameter]` Sections và `EventCallback<Section>` OnSectionClick). | • Tách biệt UI rõ ràng, có giao tiếp 2 chiều component cha-con chuẩn Blazor. | **6.0 đ** | **ĐẠT (6/6)** |
| **K3.2** | **EditForm + DataAnnotations; lỗi nhập liệu hiển thị cạnh ô nhập** | Demo nhập sai để xem lỗi validation | • `CourseRegistration.Web/Components/Pages/Admin/ManageCoursesComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/Admin/ManageSectionsComponent.razor` | • `ManageCoursesComponent.razor`: **Dòng 65 - 115**<br>• `ManageSectionsComponent.razor`: **Dòng 75 - 135**<br>• Đầy đủ `<EditForm>`, `<DataAnnotationsValidator />`, `<ValidationMessage For="..." />` cạnh từng ô nhập. | **5.0 đ** | **ĐẠT (5/5)** |
| **K3.3** | **Ca vi phạm 2 luật nghiệp vụ hiện thông báo thân thiện, không văng lỗi** | Demo ca vi phạm trên giao diện | • `CourseRegistration.Web/Components/Pages/RegistrationCartComponent.razor`<br>• `CourseRegistration.Web/Components/Shared/ValidationAlertComponent.razor` | • Không bao giờ văng exception (crash hệ thống).<br>• Hiển thị alert màu đỏ thân thiện, nêu rõ lý do: Tên môn tiên quyết còn thiếu / Mã lớp và thứ tiết bị trùng lịch / Lớp hết chỗ. | **4.0 đ** | **ĐẠT (4/4)** |
| **K3.4** | **Dùng được trên màn hình điện thoại DevTools, bề rộng 390px** | Mở F12 $\rightarrow$ Ctrl+Shift+M $\rightarrow$ iPhone 390px | • `CourseRegistration.Web/Components/Layout/NavMenu.razor`<br>• `CourseRegistration.Web/Components/Shared/ScheduleTableComponent.razor` | • Navbar có nút hamburger 3 gạch thu gọn mượt mà.<br>• Thời khóa biểu có chế độ **Agenda xem theo ngày cuộn ngang**, thiết kế tối ưu riêng cho mobile 390px. | **5.0 đ** | **ĐẠT (5/5)** |
| **K4.1** | **Đăng nhập, đăng xuất; ít nhất 2 vai trò** | Demo đăng nhập 2 role & F5 kiểm tra phiên | • `CourseRegistration.Web/Components/Shared/LoginModalComponent.razor`<br>• `CourseRegistration.Web/Components/Layout/NavMenu.razor`<br>• `CourseRegistration.Web/Components/Layout/MainLayout.razor` | • **Vai trò 1 (Student):** 7 sinh viên tuần tự `sv21001` đến `sv21007` / `123456`<br>• **Vai trò 2 (Admin):** `admin` / `admin123`<br>• Có nút Đăng xuất xóa phiên làm việc tức thì.<br>• Hỗ trợ phục hồi phiên bền vững khi F5 (Reload trang) qua `localStorage` và `CurrentUserStateStore`. | **4.0 đ** | **ĐẠT (4/4)** |
| **K4.2** | **Tài khoản thường gõ thẳng URL trang quản trị bị chặn** | Gõ trực tiếp URL `/admin/courses` | • `CourseRegistration.Web/Components/Pages/Admin/ManageCoursesComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/Admin/ManageSectionsComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/Admin/ViewEnrollmentListComponent.razor`<br>• `CourseRegistration.Web/Program.cs` | • Kiểm tra `role != "Admin"` trong `OnInitializedAsync`, gõ thẳng URL bị chặn ngay và chuyển hướng sang trang HTTP 403 Forbidden.<br>• Minimal API xuất báo cáo sinh viên được bảo vệ với tham số kiểm tra `studentCode` chống lộ dữ liệu (IDOR). | **5.0 đ** | **ĐẠT (5/5)** |
| **K4.3** | **Tính năng nâng cao: Thanh toán học phí VietQR, Quản lý học vụ học phí Đào Tạo, Trợ lý AI và Xuất báo cáo (Excel/PDF) từ dữ liệu thật** | Demo + xem code UseCase & Service | • `CourseRegistration.Web/Services/ReportExportService.cs`<br>• `CourseRegistration.UseCases/StudentPortal/Payment/`<br>• `CourseRegistration.UseCases/AdminPortal/ManageTuitionScreen/`<br>• `CourseRegistration.UseCases/StudentPortal/AiAdvisor/`<br>• `CourseRegistration.UseCases/AdminPortal/AiAdminAssistant/` | • **Thanh Toán & Quản Lý Học Vụ Học Phí:** Cổng VietQR Napas 24/7 cho SV nộp tiền; Cổng Quản lý Học vụ Học phí & Chế tài Dự thi (`/admin/tuition`) cho Phòng Đào Tạo giám sát công nợ toàn trường.<br>• **Xuất Excel & PDF:** ClosedXML & QuestPDF xuất danh sách lớp sinh viên, thời khóa biểu và phiếu đăng ký có thông tin học phí chuẩn mẫu.<br>• **Trợ lý AI:** Chatbot Cố vấn AI học tập cho SV và AI tra cứu học vụ cho Admin (chuẩn `Microsoft.Extensions.AI`, IChatClient trong UseCase riêng). | **6.0 đ** | **ĐẠT (6/6)** |
| **K5.1** | **Use Case, Class, Sequence (PlantUML) khớp tên lớp và luồng trong code** | Mở thư mục `Docs/PlantUML/` và so sánh | • `Docs/PlantUML/usecase.puml`<br>• `Docs/PlantUML/class_diagram.puml`<br>• `Docs/PlantUML/sequence_enrollment.puml`<br>• `Docs/README_UML.md` | • Đầy đủ cả 3 sơ đồ chuẩn mã nguồn PlantUML (`.puml`).<br>• Tên Actor, UseCase khớp 100% màn hình Razor và UseCase Screen.<br>• Sơ đồ lớp khớp 100% 9 Model thực thể CSDL và Repository.<br>• Sequence Diagram mô tả chi tiết luồng đăng ký học phần chính. | **6.0 đ** | **ĐẠT (6/6)** |
| **K5.2** | **Vấn đáp: Mỗi thành viên chỉ đúng dòng cài luật và giải thích Dependency Rule** | Hỏi từng người khi bảo vệ | • `EnrollmentValidationService.cs` (Dòng 26–90)<br>• `SectionRepository.cs` (Dòng 142–156)<br>• `PlaceEnrollmentUseCase.cs` (Dòng 85–155)<br>• `Program.cs` (Dòng 48–63) | • Có sẵn bảng câu hỏi vấn đáp chi tiết và số dòng code chỉ dẫn trong tài liệu GRADING.md. | **6.0 đ** | **ĐẠT (6/6)** |
| **K5.3** | **Mọi thành viên có commit, rải trên ít nhất 3 tuần** | `git log --date=short` | Git History repository | • 99 commits rải đều từ ngày 07/10/2026 đến 30/10/2026 (4 tuần liên tục).<br>• Tác giả: 1 sinh viên (Nguyễn Thị Lưu) hoàn thành đồ án cá nhân. | **3.0 đ** | **ĐẠT (3/3)** |


---

### HƯỚNG DẪN KỊCH BẢN DEMO NHANH CHO GIẢNG VIÊN CHẤM THI (3 PHÚT)

#### 1. Tài khoản đăng nhập hệ thống:
* **Quản trị viên (Admin - Phòng Đào Tạo):** `admin` / mật khẩu: `admin123`
* **Sinh viên 1 (Đạt chuẩn tiên quyết):** `sv21001` (Nguyễn Văn Tuấn) / mật khẩu: `123456`
* **Sinh viên 2 (CHƯA học OOP TIN102 -> DEMO CHẶN TIÊN QUYẾT):** `sv21002` (Trần Thị Mai) / mật khẩu: `123456`
* **Các sinh viên khác:** `sv21003` đến `sv21007` / mật khẩu: `123456`

---

#### 2. Kịch bản Demo 2 Luật nghiệp vụ (Tiêu chí K1.3 & K3.3):
* **Demo Luật 1 (Vi phạm học phần tiên quyết):**
  1. Đăng nhập bằng sinh viên `sv21002`.
  2. Vào **Tra cứu học phần**, tìm môn **Lập trình Ứng dụng Web (WEB301)**.
  3. Hệ thống hiển thị cảnh báo: *"Chưa đạt học phần tiên quyết: Lập trình Hướng đối tượng (TIN102)"*.
  4. Nếu cố tình đưa vào giỏ và bấm *"Gửi phiếu đăng ký"*, hệ thống CHẶN GIAO DỊCH và hiển thị thông báo thân thiện: *"Hệ Thống Chặn Đăng Ký (Vi Phạm Luật Nghiệp Vụ) - Chưa tích lũy môn học tiên quyết: Lập trình Hướng đối tượng"*.
* **Demo Luật 2 (Vi phạm chống trùng lịch học):**
  1. Đăng nhập bằng sinh viên `sv21001`.
  2. Chọn môn `WEB301_01` (Lịch học: Thứ 2, tiết 1-4) vào giỏ.
  3. Chọn môn `TIN201_01` (Lịch học: Thứ 2, tiết 1-3) vào giỏ.
  4. Vào **Giỏ đăng ký** $\rightarrow$ Bấm *"Gửi phiếu đăng ký"*.
  5. Hệ thống từ chối giao dịch và báo lỗi: *"Trùng lịch với lớp WEB301_01 (Lập trình Ứng dụng Web): Thứ 2 tiết 1-3"*.

---

#### 3. Kịch bản Demo Phân quyền & Chặn URL (Tiêu chí K4.1 & K4.2):
* Khi đang ở tài khoản sinh viên `sv21001` hoặc chưa đăng nhập:
  * Gõ trực tiếp lên thanh địa chỉ trình duyệt: `http://localhost:53670/admin/courses` hoặc `/admin/enrollments`.
  * Hệ thống lập tức chặn lại và chuyển hướng về trang **`/access-denied` (403 - Từ chối truy cập)**.
* Khi đăng nhập tài khoản `admin`:
  * Xuất hiện menu **Phòng Đào Tạo** với các chức năng quản lý môn học, lớp học phần và **Quản lý Đăng ký & Rút môn SV**.

---

#### 4. Kịch bản Demo Giao dịch Header - Line & Nghiệp vụ Hủy môn sau hạn (Tiêu chí K2.2 & K2.4):
1. Sinh viên `sv21001` gửi phiếu đăng ký $\rightarrow$ Dữ liệu ghi vào bảng `Enrollments` (Header) và `EnrollmentLines` (Line) trong 1 Transaction, sĩ số lớp `WEB301_01` tăng từ 0 lên 1.
2. Đăng nhập `admin` $\rightarrow$ Vào **Phòng Đào Tạo $\rightarrow$ Quản lý Đăng ký & Rút môn SV**.
3. Chọn sinh viên `sv21001` $\rightarrow$ Thấy môn `WEB301_01` $\rightarrow$ Bấm **"Hủy môn / Rút môn"** $\rightarrow$ Chọn lý do học vụ và xác nhận.
4. Môn học được hủy khỏi phiếu của sinh viên, mở SSMS kiểm tra thấy `CurrentEnrollment` của lớp tự động trừ đi 1 để nhả chỗ cho sinh viên khác.

---

#### 5. Kịch bản Demo Tính năng Nâng cao (Tiêu chí K4.3):
1. **Xuất Báo Cáo:** Vào trang **Xem danh sách lớp theo SV** hoặc **Phiếu đăng ký** $\rightarrow$ Bấm nút **Xuất Excel** hoặc **Xuất PDF** $\rightarrow$ Tệp tải về ngay với định dạng bảng biểu, màu sắc tiêu chuẩn trường học (kèm thông tin học phí và trạng thái thanh toán).
2. **Cố Vấn AI:** Bấm menu **Cố vấn AI** $\rightarrow$ Hỏi đáp về lộ trình học tập, môn tiên quyết, gợi ý môn học theo học kỳ.
3. **Thanh Toán Học Phí Trực Tuyến VietQR (Napas 247):** Bấm menu **Học phí** hoặc vào **Phiếu đăng ký** $\rightarrow$ Bấm nút **"Nộp Học Phí VietQR"** $\rightarrow$ Hiển thị mã QR động chuẩn Napas247 (quét bằng App ngân hàng thật) $\rightarrow$ Bấm *"Tôi Đã Hoàn Tất Chuyển Khoản"* $\rightarrow$ Hệ thống đối soát thời gian thực, cấp mã biên lai điện tử (E-Receipt) và cập nhật trạng thái phiếu thành *"Đã hoàn tất học phí"*.
4. **Quản Lý Học Vụ Học Phí & Chế Tài Dự Thi (Phòng Đào Tạo):** Đăng nhập `admin` $\rightarrow$ Vào **Phòng Đào Tạo $\rightarrow$ Quản lý Học Phí & Chế Tài Thi** (`/admin/tuition`) $\rightarrow$ Xem 4 thẻ KPI toàn trường, danh sách sinh viên đủ điều kiện thi / bị cấm thi do nợ học phí, gạch nợ cứu xét hoặc hủy xác nhận thời gian thực. Thao tác tương tự ngay trong Tab 1 của **Quản lý Đăng ký & Rút môn SV**.

---

#### 6. Kịch bản Demo Thiết kế Hệ thống (Tiêu chí K5.1):
* Sơ đồ Use Case phân ranh giới chuẩn hệ thống: [DangKiTinChi-Usecase.drawio.png](file:///f:/L%E1%BA%ADp%20tr%C3%ACnh%20%E1%BB%A9ng%20d%E1%BB%A5ng%20Web/DoAn/DangKiTinChi-Usecase.drawio.png).
* Sơ đồ lớp (Class Diagram) chuẩn 9 bảng 3NF: [DangKiTinChi-Sơ đồ Lớp .drawio.png](file:///f:/L%E1%BA%ADp%20tr%C3%ACnh%20%E1%BB%A9ng%20d%E1%BB%A5ng%20Web/DoAn/DangKiTinChi-S%C6%A1%20%C4%91%E1%BB%93%20L%E1%BB%9Bp%20.drawio.png).

---

#### 7. Kịch bản Demo 2 Học kỳ & 3 Giai đoạn Linh hoạt (Bao trùm ngày thi 02/11/2026):
1. **Lộ trình 2 Học kỳ chuẩn:**
   * **Học kỳ 1 (2026_HK1):** 15/10/2026 &rarr; 28/02/2027.
   * **Học kỳ 2 (2026_HK2):** 15/12/2026 &rarr; 30/06/2027.
2. **3 Giai đoạn linh hoạt mỗi học kỳ:**
   * **Giai đoạn 1 (15/10 &rarr; 26/10/2026):** Đăng ký chính thức theo lộ trình chuẩn CTĐT.
   * **Giai đoạn 2 (27/10 &rarr; 05/11/2026):** Đăng ký bổ sung, đổi lớp, học vượt & cải thiện điểm.
     * **Bảo vệ đồ án ngày 02/11/2026:** Nằm trọn vẹn trong Giai đoạn 2, hệ thống tự động nhận diện và hiển thị badge màu xanh **`🟢 CỔNG ĐĂNG KÝ ĐANG MỞ`**. Sinh viên và Giảng viên hoàn toàn có thể tra cứu và đăng ký trực tiếp không bị chặn quá hạn!
   * **Giai đoạn 3 (06/11 &rarr; 20/11/2026):** Rút học phần, cứu xét học vụ & chốt danh sách thi học kỳ.
3. **Mô phỏng linh hoạt (Test controls cho Giảng viên):**
   * Ngay trên thanh Banner tại trang **Tra Cứu Học Phần** (`/sections`), có dropdown chọn học kỳ (HK1 / HK2) và dropdown **"Chế độ biểu diễn cho Giảng viên"** cho phép chuyển đổi tức thì giữa các Giai đoạn 1, 2, 3 để chứng minh tính linh hoạt của hệ thống.

---

#### 8. Phân bổ Dữ liệu Học phí Mẫu (Paid / Unpaid) trong CSDL SQL Server:
* **Sinh viên ĐÃ nộp học phí (`Paid`):** `SV21003`, `SV21004`, `SV21007`, `SV21010`, `SV21013`, `SV21016`, `SV21019`.
  * Có ngày nộp `PaymentDate`, phương thức `VietQR Napas247`, và mã giao dịch `TransactionReference`.
  * Hiển thị trạng thái: **`Đủ điều kiện dự thi`** (Màu xanh).
* **Sinh viên CHƯA nộp học phí (`Unpaid`):** `SV21001` (Nguyễn Văn Tuấn), `SV21002` (Trần Thị Mai), `SV21005`, `SV21006`, `SV21008`...
  * Hiển thị trạng thái: **`Tạm hoãn dự thi / Cấm thi`** (Màu đỏ).
  * Cho phép demo:
    1. Sinh viên `sv21001` tự quét mã QR Napas247 tại `/tuition` để nộp tiền và được gạch nợ tự động.
    2. Cán bộ Phòng Đào Tạo tại `/admin/tuition` thực hiện cứu xét gạch nợ học phí cho sinh viên khó khăn bằng nút **"Cứu xét gạch nợ"**.

