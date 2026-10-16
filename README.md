## 1. Giới thiệu dự án
**Đăng Ký Học Phần** là dự án xây dựng website đăng ký học phần dành cho sinh viên, được phát triển nhằm phục vụ quá trình học tập và thực hành lập trình ứng dụng Web.
Hệ thống hướng đến việc hỗ trợ sinh viên tra cứu học phần, xem thông tin lớp học phần, lựa chọn lịch học và quản lý đăng ký học phần. Dự án đồng thời nghiên cứu việc kiểm tra các điều kiện đăng ký như học phần tiên quyết, trùng lịch học và giới hạn số tín chỉ.

## 2. Mục tiêu dự án
- Xây dựng website đăng ký học phần với giao diện dễ sử dụng.
- Quản lý thông tin sinh viên, học phần và lớp học phần.
- Quản lý lịch học của từng lớp học phần.
- Hỗ trợ tạo và quản lý phiếu đăng ký học phần.
- Thiết kế cơ sở dữ liệu có các khóa chính, khóa ngoại và quan hệ giữa các bảng.
- Tổ chức chương trình theo kiến trúc phân lớp, tách biệt giao diện, nghiệp vụ và truy xuất dữ liệu.
- Thực hành quy trình phát triển phần mềm, quản lý mã nguồn bằng Git và GitHub.

## 3. Công nghệ sử dụng
| Công nghệ | Mục đích |
|---|---|
| C# | Ngôn ngữ lập trình chính |
| .NET 8 | Nền tảng phát triển ứng dụng |
| Blazor | Xây dựng giao diện Web |
| SQL Server | Thiết kế và quản lý cơ sở dữ liệu |
| Dapper | Hỗ trợ truy vấn dữ liệu bằng SQL |
| Visual Studio | Môi trường phát triển |
| Git & GitHub | Quản lý phiên bản và lưu trữ mã nguồn |
| PlantUML / diagrams.net | Thiết kế sơ đồ hệ thống và ERD |
*Lưu ý: Các công nghệ SQL Server và Dapper được sử dụng trong quá trình xây dựng, tích hợp dữ liệu; mức độ tích hợp thực tế phụ thuộc vào phiên bản mã nguồn hiện tại.*

## 4. Chức năng của hệ thống
### 4.1. Chức năng dành cho sinh viên
- Tra cứu và tìm kiếm học phần.
- Xem thông tin lớp học phần.
- Xem lịch học theo ngày và tiết học.
- Lựa chọn lớp học phần để đăng ký.
- Quản lý các học phần đã chọn.
- Xác nhận hoặc hủy đăng ký theo quy tắc nghiệp vụ được triển khai.
- Xem thông tin đăng ký học phần.

### 4.2. Quản lý nghiệp vụ đăng ký
Hệ thống hướng đến việc hỗ trợ các quy tắc nghiệp vụ:
- Kiểm tra điều kiện học phần tiên quyết.
- Kiểm tra trùng lịch học.
- Kiểm tra số lượng sinh viên tối đa của lớp học phần.
- Kiểm tra giới hạn số tín chỉ trong học kỳ.
- Quản lý trạng thái đăng ký và thời gian mở/đóng đăng ký.
Các chức năng cần được kiểm thử và hoàn thiện trong code trước khi xác nhận là đã triển khai đầy đủ.

## 5. Kiến trúc dự án
Dự án được tổ chức theo hướng phân tách trách nhiệm giữa các thành phần.
| Project | Vai trò |
|---|---|
| `CourseRegistration.Web` | Giao diện và xử lý tương tác Web |
| `CourseRegistration.CoreBusiness` | Chứa các model và dữ liệu cốt lõi của hệ thống |
| `CourseRegistration.UseCases` | Tổ chức các trường hợp sử dụng và nghiệp vụ |
| `CourseRegistration.DataStore` | Thành phần liên quan đến lưu trữ và truy xuất dữ liệu |
Cấu trúc thực tế có thể được mở rộng tùy theo các repository và plugin đang được sử dụng trong từng giai đoạn phát triển.

## 6. Thiết kế cơ sở dữ liệu
Cơ sở dữ liệu dự kiến gồm 8 bảng chính:
| Bảng | Chức năng |
|---|---|
| `Students` | Lưu thông tin sinh viên |
| `Courses` | Lưu danh mục học phần |
| `Prerequisites` | Quản lý quan hệ học phần tiên quyết |
| `CompletedCourses` | Lưu các học phần đã hoàn thành |
| `Sections` | Quản lý các lớp học phần được mở |
| `SectionSchedules` | Lưu lịch học của lớp học phần |
| `Enrollments` | Quản lý phiếu đăng ký |
| `EnrollmentLines` | Lưu chi tiết các lớp học phần trong phiếu đăng ký |
Các quan hệ chính gồm:
- Một sinh viên có thể có nhiều phiếu đăng ký.
- Một phiếu đăng ký có thể có nhiều dòng đăng ký.
- Một học phần có thể mở nhiều lớp học phần.
- Một lớp học phần có thể có nhiều lịch học.
- Một sinh viên có thể hoàn thành nhiều học phần.
- Một học phần có thể yêu cầu hoàn thành học phần tiên quyết.
Sơ đồ ERD được xây dựng để thể hiện các thực thể, khóa chính, khóa ngoại và quan hệ giữa các bảng. Thiết kế cần được đối chiếu với database thực tế để bảo đảm tính nhất quán và đáp ứng chuẩn hóa 3NF.

## 7. Kết quả hướng đến
Dự án hướng đến một website đăng ký tín chỉ có cấu trúc rõ ràng, hỗ trợ quản lý học phần, lớp học phần, lịch học và đăng ký học phần.
Thông qua quá trình thực hiện, người phát triển có cơ hội vận dụng kiến thức về lập trình Web, phân tích nghiệp vụ, thiết kế cơ sở dữ liệu, kiến trúc phần mềm và quản lý mã nguồn.

## 8. Hướng phát triển
- Hoàn thiện việc tích hợp SQL Server với các repository.
- Hoàn thiện các quy tắc nghiệp vụ đăng ký và hủy đăng ký.
- Bổ sung dữ liệu mẫu đa dạng cho học phần, lớp học phần và lịch học.
- Hoàn thiện chức năng quản lý dành cho Phòng Đào tạo.
- Bổ sung kiểm tra quyền truy cập theo vai trò nếu thuộc phạm vi yêu cầu.
- Kiểm thử dữ liệu và nghiệp vụ trước khi triển khai.

## 9. Tác giả

**Nguyễn Thị Lưu**
- Ngành: Tin học Kinh tế
- Khoa: Hệ thống Thông tin Kinh tế
- Trường: Trường Đại học Kinh tế – Đại học Huế
## 10. Repository
GitHub: [https://github.com/nguyenluu-jpg/DangKiTinChi](https://github.com/nguyenluu-jpg/DangKiTinChi)
---
*Dự án được thực hiện với mục đích học tập, thực hành phát triển ứng dụng Web và xây dựng hệ thống đăng ký học phần.*
