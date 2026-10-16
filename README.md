# Hệ thống Đăng ký Học phần

## 1. Giới thiệu
**Course Registration – Đăng ký học phần** là dự án website mô phỏng quy trình đăng ký học phần theo học kỳ, được thực hiện phục vụ học tập và thực hành phát triển ứng dụng Web.
Dự án hướng đến hai nhóm người dùng chính: **Sinh viên** và **cán bộ quản trị/Phòng Đào tạo**. Hệ thống sử dụng dữ liệu mẫu để minh họa việc tra cứu học phần, lớp học phần, lịch học và thông tin đăng ký.
> Đây là dự án mô phỏng phục vụ học tập, không phải hệ thống chính thức của một trường đại học.

## 2. Công nghệ sử dụng

| Công nghệ | Vai trò |
|---|---|
| C# | Ngôn ngữ lập trình |
| .NET 8 | Nền tảng ứng dụng |
| Blazor | Xây dựng giao diện Web |
| Entity Framework Core 8 | Truy xuất dữ liệu theo mô hình DbContext |
| SQL Server | Lưu trữ dữ liệu quan hệ |
| Git và GitHub | Quản lý phiên bản mã nguồn |
| Visual Studio | Môi trường phát triển |

**Lưu ý:** Mã nguồn hiện tại tham chiếu Microsoft.EntityFrameworkCore.SqlServer; README này không mô tả Dapper là công nghệ truy xuất dữ liệu chính.

## 3. Cấu trúc dự án
Solution chính: **CourseRegistration.sln**
| Project/thư mục | Vai trò |
|---|---|
| **CourseRegistration.Web** | Ứng dụng Web Blazor, cấu hình dịch vụ và giao diện |
| **CourseRegistration.CoreBusiness** | Các mô hình dữ liệu và thành phần cốt lõi |
| **CourseRegistration.UseCases** | Các use case và logic ứng dụng |
| **Plugins/CourseRegistration.DataStore.HardCoded** | Thành phần lưu trữ dữ liệu mẫu |
| **Plugins/CourseRegistration.DataStore.SqlServer** | Thành phần truy xuất dữ liệu với SQL Server |
| **Plugins/CourseRegistration.StateStore.DI** | Thành phần đăng ký/cấu hình state store và dependency injection |
| **DangKyTinChi_Database.sql** | Script tạo cơ sở dữ liệu và nạp dữ liệu mẫu |
Ứng dụng được tổ chức theo hướng tách giao diện, logic ứng dụng, mô hình cốt lõi và các thành phần lưu trữ dữ liệu. Việc sử dụng dữ liệu mẫu hay SQL Server phụ thuộc vào cấu hình triển khai trong mã nguồn.

## 4. Nhóm chức năng và phạm vi
Dự án tập trung vào các nghiệp vụ đăng ký học phần sau:
- Tra cứu học phần và lớp học phần.
- Xem thông tin lớp học phần và lịch học.
- Quản lý lựa chọn lớp học phần và thông tin đăng ký.
- Quản lý dữ liệu học phần, sinh viên, lớp học phần và lịch học.
- Xây dựng các điều kiện nghiệp vụ cần kiểm tra khi đăng ký.
Các quy tắc nghiệp vụ mục tiêu của đề tài gồm:
1. Kiểm tra học phần tiên quyết.
2. Phát hiện trùng lịch học.
3. Kiểm tra giới hạn tín chỉ của sinh viên trong học kỳ.
4. Kiểm tra sức chứa tối đa của lớp học phần, kể cả trường hợp nhiều yêu cầu đăng ký đồng thời.
5. Xét trạng thái và thời gian mở/đóng đăng ký khi chức năng này được triển khai.

## 5. Cơ sở dữ liệu
| Bảng | Mục đích |
|---|---|
| **Courses** | Danh mục học phần |
| **Students** | Thông tin sinh viên |
| **Prerequisites** | Quan hệ học phần tiên quyết |
| **Sections** | Các lớp học phần được mở trong học kỳ |
| **SectionSchedules** | Lịch học của từng lớp học phần |
| **CompletedCourses** | Các học phần sinh viên đã hoàn thành |
| **Enrollments** | Phiếu đăng ký học phần |
| **EnrollmentLines** | Chi tiết lớp học phần trong phiếu đăng ký |
| **UserAccounts** | Tài khoản người dùng và vai trò |
Script có dữ liệu mẫu để hỗ trợ thử nghiệm các tình huống như điều kiện tiên quyết, trùng lịch và lớp đã đủ sức chứa. Script có lệnh xóa rồi tạo lại các bảng; hãy chỉ chạy trên cơ sở dữ liệu thử nghiệm vì dữ liệu hiện có trong các bảng này sẽ bị xóa khi chạy lại.

## 6. Định hướng phát triển
- Tiếp tục đối chiếu các use case với chức năng thực tế trên giao diện.
- Kiểm thử các quy tắc tiên quyết, trùng lịch, giới hạn tín chỉ và sức chứa lớp.
- Kiểm tra tính nhất quán giữa model, repository và cấu trúc SQL Server.
- Hoàn thiện các chức năng quản trị và xử lý đăng ký còn thiếu.
- Cải thiện kiểm thử và tài liệu sử dụng khi các chức năng được hoàn thiện.

## 7. Tác giả
**Nguyễn Thị Lưu**  
- Ngành: Tin học Kinh tế  
- Khoa: Hệ thống Thông tin Kinh tế  
- Trường: Trường Đại học Kinh tế – Đại học Huế

## 8. Mã nguồn
Repository: [https://github.com/nguyenluu-jpg/DangKiTinChi](https://github.com/nguyenluu-jpg/DangKiTinChi)

---

*Dự án phục vụ mục đích học tập, thực hành lập trình Web, tổ chức kiến trúc phần mềm và thiết kế cơ sở dữ liệu.*
