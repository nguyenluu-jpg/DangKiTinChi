# TÀI LIỆU HƯỚNG DẪN CHẤM THI (GRADING GUIDE)
## ĐỒ ÁN: ĐĂNG KÝ HỌC PHẦN TRỰC TUYẾN (ĐỀ 15)

> **Công nghệ áp dụng:** C#, ASP.NET Core Blazor (.NET 8), Microsoft SQL Server, Entity Framework Core.  
> **Kiến trúc:** Clean Architecture (Mô hình eShop phân lớp chuẩn).

---

### BẢNG TRA CỨU NHANH 13 TIÊU CHÍ CHẤM THI

| STT | Tiêu chí chấm thi | Vị trí file trong mã nguồn | Số dòng code / Chi tiết | Điểm tối đa |
|:---:|:---|:---|:---|:---:|
| **1** | **Đủ 4 project; Quy tắc phụ thuộc**<br>• CoreBusiness không tham chiếu ai<br>• UseCases chỉ tham chiếu CoreBusiness | • `CourseRegistration.CoreBusiness/CourseRegistration.CoreBusiness.csproj`<br>• `CourseRegistration.UseCases/CourseRegistration.UseCases.csproj`<br>• `CourseRegistration.Web/CourseRegistration.Web.csproj`<br>• `Plugins/CourseRegistration.DataStore.SqlServer/` | • `CoreBusiness.csproj`: 0 `<ProjectReference>`<br>• `UseCases.csproj`: Dòng 10-12 (Chỉ tham chiếu CoreBusiness)<br>• Clean Architecture 4 project | **Đạt** |
| **2** | **Interface repository ở UseCases, Plugin cài đặt; Razor chỉ inject Interface** | • **Interface:** `CourseRegistration.UseCases/PluginInterfaces/DataStore/`<br>• **Plugin:** `Plugins/CourseRegistration.DataStore.SqlServer/`<br>• **Cấu hình DI:** `CourseRegistration.Web/Program.cs`<br>• **Các file `.razor`:** `@inject ICourseRepository`, `@inject IStudentRepository`... | • `Program.cs`: Dòng 48-79<br>• `SearchSectionComponent.razor`: Dòng 3-9<br>• 100% file `.razor` không inject lớp cụ thể | **Đạt** |
| **3** | **2 luật nghiệp vụ cài trong CoreBusiness / UseCases, không nằm trong `.razor`** | • `CourseRegistration.CoreBusiness/Services/EnrollmentValidationService.cs`<br>• `CourseRegistration.UseCases/StudentPortal/RegistrationCartScreen/PlaceEnrollmentUseCase.cs` | • **Luật 1 (Tiên quyết):** `EnrollmentValidationService.cs` **Dòng 26 - 43**<br>• **Luật 2 (Chống trùng lịch):** `EnrollmentValidationService.cs` **Dòng 46 - 70**<br>• `PlaceEnrollmentUseCase.cs`: **Dòng 85 - 111**<br>• Giỏ hàng Scoped: `Program.cs` **Dòng 57-59** | **6.0 đ**<br>*(3đ / luật)* |
| **4** | **Từ 5 bảng, 3NF, có khóa ngoại; script tạo bảng + dữ liệu mẫu chạy được trên CSDL trống** | • File script SQL tại thư mục gốc: `DangKyTinChi_Database.sql`<br>• EF Core DbContext: `Plugins/CourseRegistration.DataStore.SqlServer/CourseRegistrationContext.cs` | • Có **9 bảng** chuẩn 3NF: `Courses`, `Students`, `Prerequisites`, `Sections`, `SectionSchedules`, `CompletedCourses`, `Enrollments`, `EnrollmentLines`, `UserAccounts`.<br>• Chạy trực tiếp trên SSMS CSDL trống thành công 100%. | **Đạt** |
| **5** | **Cặp header–line ghi trong 1 transaction (Code Plugin luồng giao dịch chính)** | • `Plugins/CourseRegistration.DataStore.SqlServer/EnrollmentRepository.cs` | • Hàm `CreateEnrollmentAsync`: **Dòng 83 - 114**<br>• Chứa lệnh transaction tường minh:<br>`using var transaction = await context.Database.BeginTransactionAsync();`<br>... `await transaction.CommitAsync();` | **Đạt** |
| **6** | **Mọi truy vấn tham số hóa, không nối chuỗi SQL** | • Thư mục: `Plugins/CourseRegistration.DataStore.SqlServer/` | • 100% dùng LINQ tham số hóa của EF Core (`.Where(...)`, `.FindAsync(...)`).<br>• Không có `$"SELECT`, `+ "WHERE` trong Plugin. | **Đạt** |
| **7** | **Luồng giao dịch chính chạy end-to-end và dữ liệu nằm trong SQL Server** | • Cổng web: `http://localhost:53670` (hoặc `https://localhost:53669`)<br>• CSDL: SQL Server `MIU\MSSQLSERVER01`, Catalog `DangKyTinChi` | • Thao tác: Chọn môn $\rightarrow$ Thêm giỏ $\rightarrow$ Xác nhận gửi phiếu.<br>• Mở SSMS bảng `Enrollments`, `EnrollmentLines`, `Sections` dữ liệu cập nhật thời gian thực. | **Đạt** |
| **8** | **Ít nhất 3 component tái sử dụng có `[Parameter]` / `EventCallback` trong thư mục Components** | • `CourseRegistration.Web/Components/Shared/` | 1. `SectionCardComponent.razor`: **Dòng 104-138** (`[Parameter]` Section, IsInCart... và `EventCallback` OnAddToCart, OnRemoveFromCart).<br>2. `ValidationAlertComponent.razor`: **Dòng 24-38** (`[Parameter]` Errors và `EventCallback` OnDismiss).<br>3. `ScheduleTableComponent.razor`: **Dòng 153-170** (`[Parameter]` Sections, `[Parameter]` OnSectionClick và `EventCallback<Section>` OnSectionClick). | **Đạt** |
| **9** | **EditForm + DataAnnotations; lỗi nhập liệu hiển thị cạnh ô nhập** | • `CourseRegistration.Web/Components/Pages/LoginComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/Admin/ManageCoursesComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/Admin/ManageSectionsComponent.razor` | • `LoginComponent.razor`: **Dòng 34 - 66**<br>• `ManageCoursesComponent.razor`: **Dòng 37 - 80**<br>• `ManageSectionsComponent.razor`: **Dòng 37 - 100**<br>• Có `<EditForm>`, `<DataAnnotationsValidator />`, `<ValidationMessage For="..." />` cạnh từng ô nhập. | **Đạt** |
| **10** | **Ca vi phạm 2 luật nghiệp vụ hiện thông báo thân thiện, không văng lỗi** | • `CourseRegistration.Web/Components/Pages/RegistrationCartComponent.razor`<br>• `CourseRegistration.Web/Components/Shared/ValidationAlertComponent.razor` | • Không văng exception (crash).<br>• Hiển thị alert thông báo lỗi chi tiết lý do vi phạm (tên môn tiên quyết chưa đạt / tên lớp và tiết học bị trùng). | **Đạt** |
| **11** | **Dùng được trên màn hình điện thoại DevTools, bề rộng 390px** | • `CourseRegistration.Web/Components/Layout/NavMenu.razor`<br>• `CourseRegistration.Web/Components/Shared/ScheduleTableComponent.razor` | • Navbar có nút hamburger thu gọn trên mobile.<br>• Thời khóa biểu có chế độ **Agenda xem theo ngày cuộn ngang**, thiết kế tối ưu riêng cho mobile 390px. | **Đạt** |
| **12** | **Đăng nhập, đăng xuất; ít nhất 2 vai trò** | • `CourseRegistration.Web/Components/Pages/LoginComponent.razor`<br>• `CourseRegistration.Web/Components/Layout/NavMenu.razor` | • **Vai trò 1 (Student):** 7 sinh viên tuần tự `sv21001` đến `sv21007` / `123456`<br>• **Vai trò 2 (Admin):** `admin` / `admin123`<br>• Có nút Đăng xuất xóa phiên làm việc. | **Đạt** |
| **13** | **Tài khoản thường gõ thẳng URL trang quản trị bị chặn** | • `CourseRegistration.Web/Components/Pages/Admin/ManageCoursesComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/Admin/ManageSectionsComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/Admin/ViewEnrollmentListComponent.razor`<br>• `CourseRegistration.Web/Components/Pages/AccessDeniedComponent.razor` | • `ManageCoursesComponent.razor`<br>• `ManageSectionsComponent.razor`<br>• `ViewEnrollmentListComponent.razor`<br>• Kiểm tra `role != "Admin"` trong `OnInitializedAsync`, gõ thẳng URL hoặc truy cập trái phép bị chặn ngay với thông báo HTTP 403 Forbidden giải thích rõ ràng lý do bị từ chối truy cập. | **Đạt** |

---

### HƯỚNG DẪN DEMO NHANH CHO GIẢNG VIÊN CHẤM THI

#### 1. Tài khoản đăng nhập hệ thống:
* **Quản trị viên (Admin - Phòng Đào Tạo):** `admin` / mật khẩu: `admin123`
* **Sinh viên 1 (Đã đạt chuẩn tiên quyết TIN101 & TIN102):** `sv21001` (Nguyễn Văn Tuấn) / mật khẩu: `123456`
* **Sinh viên 2 (CHƯA học OOP - TIN102 -> DEMO CHẶN TIÊN QUYẾT):** `sv21002` (Trần Thị Mai) / mật khẩu: `123456`
* **Các sinh viên khác (Dữ liệu tuần tự):**
  * `sv21003` (Lê Hoàng Nam) / mật khẩu: `123456`
  * `sv21004` (Phạm Minh Anh) / mật khẩu: `123456`
  * `sv21005` (Hoàng Đức Huy) / mật khẩu: `123456`
  * `sv21006` (Vũ Thảo Linh) / mật khẩu: `123456`
  * `sv21007` (Đặng Quốc Bảo) / mật khẩu: `123456`

---

#### 2. Kịch bản Demo 2 Luật nghiệp vụ (Tiêu chí 3 & Tiêu chí 10):
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

#### 3. Kịch bản Demo Phân quyền & Chặn URL (Tiêu chí 12 & Tiêu chí 13):
* Khi đang ở tài khoản sinh viên `sv21001` hoặc chưa đăng nhập:
  * Gõ trực tiếp lên thanh địa chỉ trình duyệt: `http://localhost:53670/admin/courses` hoặc `http://localhost:53670/admin/enrollments`.
  * Hệ thống lập tức chặn lại và chuyển hướng về trang **`/access-denied` (403 - Từ chối truy cập)**.
* Khi đăng nhập tài khoản `admin`:
  * Xuất hiện menu **Phòng Đào Tạo** với các chức năng quản lý môn học, lớp học phần và **Quản lý Đăng ký & Rút môn SV**.

---

#### 4. Kịch bản Demo Giao dịch Header - Line & Nghiệp vụ Hủy môn sau hạn (Tiêu chí 5 & 7):
1. Sinh viên `sv21001` gửi phiếu đăng ký $\rightarrow$ Dữ liệu ghi vào bảng `Enrollments` (Header) và `EnrollmentLines` (Line) trong 1 Transaction, sĩ số lớp `WEB301_01` tăng từ 0 lên 1.
2. Đăng nhập `admin` $\rightarrow$ Vào **Phòng Đào Tạo $\rightarrow$ Quản lý Đăng ký & Rút môn SV**.
3. Chọn sinh viên `sv21001` $\rightarrow$ Thấy môn `WEB301_01` $\rightarrow$ Bấm **"Hủy môn / Rút môn"** $\rightarrow$ Chọn lý do học vụ và xác nhận.
4. Môn học được hủy khỏi phiếu của sinh viên, mở SSMS kiểm tra thấy `CurrentEnrollment` của lớp tự động trừ đi 1 để nhả chỗ cho sinh viên khác.
