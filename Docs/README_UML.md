# HỒ SƠ THIẾT KẾ HỆ THỐNG — SƠ ĐỒ UML (PLANTUML)
## ĐỒ ÁN CAPSTONE ECO2415: ĐĂNG KÝ HỌC PHẦN (ĐỀ 15)

Tài liệu này cung cấp toàn bộ sơ đồ phân tích và thiết kế hệ thống theo chuẩn **PlantUML** cho đề tài Đăng ký Học phần (Course Registration) nhằm đáp ứng trọn vẹn tiêu chí **K5.1**.

---

### DANH SÁCH CÁC SƠ ĐỒ THIẾT KẾ (PLANTUML):

1. **Use Case Diagram (Sơ đồ ca sử dụng):**  
   - File mã nguồn: [usecase.puml](file:///f:/L%E1%BA%ADp%20tr%C3%ACnh%20%E1%BB%A9ng%20d%E1%BB%A5ng%20Web/DoAn/Docs/PlantUML/usecase.puml)  
   - Mô tả: Phân định ranh giới hệ thống, các tác tử (Sinh viên, Cán bộ Đào tạo, Trợ lý AI) và 19 ca sử dụng chi tiết kèm mối quan hệ `<<include>>` với các luật nghiệp vụ (Tiên quyết, Trùng lịch, Tín chỉ tối đa, Sĩ số lớp).

2. **Class Diagram (Sơ đồ lớp phân tích miền nghiệp vụ & kiến trúc):**  
   - File mã nguồn: [class_diagram.puml](file:///f:/L%E1%BA%ADp%20tr%C3%ACnh%20%E1%BB%A9ng%20d%E1%BB%A5ng%20Web/DoAn/Docs/PlantUML/class_diagram.puml)  
   - Mô tả: Khớp 100% 9 thực thể CSDL chuẩn 3NF (`Course`, `Student`, `Prerequisite`, `Section`, `SectionSchedule`, `CompletedCourse`, `Enrollment`, `EnrollmentLine`, `UserAccount`), Domain Services (`EnrollmentValidationService`), Repository Interfaces và các Use Case chính theo Clean Architecture.

3. **Sequence Diagram (Sơ đồ tuần tự cho luồng giao dịch chính):**  
   - File mã nguồn: [sequence_enrollment.puml](file:///f:/L%E1%BA%ADp%20tr%C3%ACnh%20%E1%BB%A9ng%20d%E1%BB%A5ng%20Web/DoAn/Docs/PlantUML/sequence_enrollment.puml)  
   - Mô tả: Chi tiết luồng tương tác 28 bước từ khi sinh viên nhấn nút *"Gửi phiếu đăng ký"* $\rightarrow$ Presentation Layer (`RegistrationCartComponent`) $\rightarrow$ Use Case Layer (`PlaceEnrollmentUseCase`) $\rightarrow$ Domain Layer (`EnrollmentValidationService`) $\rightarrow$ Tạm giữ chỗ nguyên tử chống tranh chấp chỗ cuối $\rightarrow$ Ghi Header-Line trong Transaction SQL Server $\rightarrow$ Dọn giỏ và chuyển sang trang Phiếu đăng ký thành công.

---

### HƯỚNG DẪN XEM SƠ ĐỒ NHANH CHO GIẢNG VIÊN:
- Có thể mở trực tiếp các file `.puml` trong Visual Studio Code (với extension *PlantUML*) hoặc dán mã vào trình tạo ảnh trực tuyến [PlantText](https://www.planttext.com/) / [PlantUML Server](https://www.plantuml.com/plantuml/uml/).
- Các sơ đồ hình ảnh PNG trực quan có sẵn tại thư mục gốc:
  - [DangKiTinChi-Usecase.drawio.png](file:///f:/L%E1%BA%ADp%20tr%C3%ACnh%20%E1%BB%A9ng%20d%E1%BB%A5ng%20Web/DoAn/DangKiTinChi-Usecase.drawio.png)
  - [DangKiTinChi-Sơ đồ Lớp .drawio.png](file:///f:/L%E1%BA%ADp%20tr%C3%ACnh%20%E1%BB%A9ng%20d%E1%BB%A5ng%20Web/DoAn/DangKiTinChi-S%C6%A1%20%C4%91%E1%BB%93%20L%E1%BB%9Bp%20.drawio.png)
