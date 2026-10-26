-- ==============================================================================
-- DỰ ÁN: ĐĂNG KÝ HỌC PHẦN TRỰC TUYẾN (ĐỀ 15)
-- CƠ SỞ DỮ LIỆU: DangKyTinChi
-- TIÊU CHÍ CHẤM THI: Tiêu chí 4 (Từ 5 bảng, 3NF, có khóa ngoại, chạy được trên CSDL trống)
-- ==============================================================================

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = N'DangKyTinChi')
BEGIN
    CREATE DATABASE [DangKyTinChi];
END
GO
USE [DangKyTinChi];
GO

-- 1. XÓA CÁC BẢNG NẾU ĐÃ TỒN TẠI (THEO THỨ TỰ PHỤ THUỘC KHÓA NGOẠI)
IF OBJECT_ID(N'dbo.EnrollmentLines', N'U') IS NOT NULL DROP TABLE dbo.EnrollmentLines;
IF OBJECT_ID(N'dbo.Enrollments', N'U') IS NOT NULL DROP TABLE dbo.Enrollments;
IF OBJECT_ID(N'dbo.SectionSchedules', N'U') IS NOT NULL DROP TABLE dbo.SectionSchedules;
IF OBJECT_ID(N'dbo.Sections', N'U') IS NOT NULL DROP TABLE dbo.Sections;
IF OBJECT_ID(N'dbo.Prerequisites', N'U') IS NOT NULL DROP TABLE dbo.Prerequisites;
IF OBJECT_ID(N'dbo.CompletedCourses', N'U') IS NOT NULL DROP TABLE dbo.CompletedCourses;
IF OBJECT_ID(N'dbo.UserAccounts', N'U') IS NOT NULL DROP TABLE dbo.UserAccounts;
IF OBJECT_ID(N'dbo.Students', N'U') IS NOT NULL DROP TABLE dbo.Students;
IF OBJECT_ID(N'dbo.Courses', N'U') IS NOT NULL DROP TABLE dbo.Courses;
GO

-- 2. TẠO CÁC BẢNG (CHUẨN 3NF, CÓ KHÓA CHÍNH, KHÓA NGOẠI)

-- Bảng 1: Courses (Danh mục học phần)
CREATE TABLE dbo.Courses (
    CourseId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CourseCode NVARCHAR(10) NOT NULL UNIQUE,
    CourseName NVARCHAR(200) NOT NULL,
    Credits INT NOT NULL DEFAULT(3),
    Description NVARCHAR(MAX) NULL
);
GO

-- Bảng 2: Students (Thông tin sinh viên)
CREATE TABLE dbo.Students (
    StudentId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    StudentCode NVARCHAR(20) NOT NULL UNIQUE,
    FullName NVARCHAR(100) NOT NULL,
    Major NVARCHAR(100) NOT NULL,
    AcademicYear NVARCHAR(20) NOT NULL,
    MaxCreditsPerSemester INT NOT NULL DEFAULT(24),
    Email NVARCHAR(100) NULL,
    PhoneNumber NVARCHAR(20) NULL
);
GO

-- Bảng 3: Prerequisites (Điều kiện môn tiên quyết)
CREATE TABLE dbo.Prerequisites (
    PrerequisiteId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CourseId INT NOT NULL,
    PrerequisiteCourseId INT NOT NULL,
    CONSTRAINT FK_Prerequisites_Course FOREIGN KEY (CourseId) REFERENCES dbo.Courses(CourseId),
    CONSTRAINT FK_Prerequisites_PrereqCourse FOREIGN KEY (PrerequisiteCourseId) REFERENCES dbo.Courses(CourseId)
);
GO

-- Bảng 4: Sections (Lớp học phần mở trong học kỳ)
CREATE TABLE dbo.Sections (
    SectionId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    SectionCode NVARCHAR(20) NOT NULL UNIQUE,
    CourseId INT NOT NULL,
    Instructor NVARCHAR(100) NOT NULL,
    MaxCapacity INT NOT NULL DEFAULT(50),
    CurrentEnrollment INT NOT NULL DEFAULT(0),
    Semester NVARCHAR(20) NOT NULL DEFAULT('2026_HK1'),
    Room NVARCHAR(50) NULL,
    RegistrationOpenAt DATETIME2 NULL,
    RegistrationCloseAt DATETIME2 NULL,
    CONSTRAINT FK_Sections_Course FOREIGN KEY (CourseId) REFERENCES dbo.Courses(CourseId) ON DELETE CASCADE
);
GO

-- Bảng 5: SectionSchedules (Lịch học chi tiết của lớp học phần: Thứ, Tiết, Phòng)
CREATE TABLE dbo.SectionSchedules (
    ScheduleId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    SectionId INT NOT NULL,
    DayOfWeek INT NOT NULL, -- 2=Thứ Hai, 3=Thứ Ba... 8=Chủ Nhật
    StartPeriod INT NOT NULL, -- Tiết bắt đầu (1-10)
    EndPeriod INT NOT NULL,   -- Tiết kết thúc (1-10)
    Room NVARCHAR(50) NULL,
    CONSTRAINT FK_SectionSchedules_Section FOREIGN KEY (SectionId) REFERENCES dbo.Sections(SectionId) ON DELETE CASCADE
);
GO

-- Bảng 6: CompletedCourses (Lịch sử học phần sinh viên đã tích lũy điểm đạt)
CREATE TABLE dbo.CompletedCourses (
    CompletedCourseId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    StudentId INT NOT NULL,
    CourseId INT NOT NULL,
    Grade DECIMAL(4,2) NOT NULL DEFAULT(7.0),
    SemesterCompleted NVARCHAR(20) NOT NULL,
    CONSTRAINT FK_CompletedCourses_Student FOREIGN KEY (StudentId) REFERENCES dbo.Students(StudentId) ON DELETE CASCADE,
    CONSTRAINT FK_CompletedCourses_Course FOREIGN KEY (CourseId) REFERENCES dbo.Courses(CourseId)
);
GO

-- Bảng 7: Enrollments (Phiếu đăng ký học phần - Header giao dịch)
CREATE TABLE dbo.Enrollments (
    EnrollmentId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    StudentId INT NOT NULL,
    DateRegistered DATETIME2 NOT NULL DEFAULT(GETDATE()),
    Semester NVARCHAR(20) NOT NULL DEFAULT('2026_HK1'),
    Status NVARCHAR(20) NOT NULL DEFAULT('Confirmed'), -- Confirmed, Cancelled
    DateCancelled DATETIME2 NULL,
    UniqueId NVARCHAR(100) NOT NULL,
    PaymentStatus NVARCHAR(30) NOT NULL DEFAULT('Unpaid'), -- Unpaid, Paid
    PaymentDate DATETIME2 NULL,
    PaymentMethod NVARCHAR(100) NULL,
    TransactionReference NVARCHAR(100) NULL,
    CONSTRAINT FK_Enrollments_Student FOREIGN KEY (StudentId) REFERENCES dbo.Students(StudentId) ON DELETE CASCADE
);
GO

-- Bảng 8: EnrollmentLines (Chi tiết học phần trong phiếu - Line giao dịch)
CREATE TABLE dbo.EnrollmentLines (
    EnrollmentLineId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EnrollmentId INT NULL,
    SectionId INT NOT NULL,
    CONSTRAINT FK_EnrollmentLines_Enrollment FOREIGN KEY (EnrollmentId) REFERENCES dbo.Enrollments(EnrollmentId) ON DELETE CASCADE,
    CONSTRAINT FK_EnrollmentLines_Section FOREIGN KEY (SectionId) REFERENCES dbo.Sections(SectionId)
);
GO

-- Bảng 9: UserAccounts (Tài khoản người dùng & Phân quyền đăng nhập)
CREATE TABLE dbo.UserAccounts (
    AccountId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Username NVARCHAR(50) NOT NULL UNIQUE,
    Password NVARCHAR(100) NOT NULL,
    FullName NVARCHAR(100) NOT NULL,
    Role NVARCHAR(20) NOT NULL, -- Admin, Student
    StudentId INT NULL,
    CONSTRAINT FK_UserAccounts_Student FOREIGN KEY (StudentId) REFERENCES dbo.Students(StudentId) ON DELETE SET NULL
);
GO

-- ==============================================================================
-- 3. NẠP DỮ LIỆU MẪU (SEED DATA CHO KIỂM THỬ VÀ DEMO CHẤM THI)
-- ==============================================================================

-- A. Dữ liệu Môn học (Courses)
INSERT INTO dbo.Courses (CourseCode, CourseName, Credits, Description) VALUES
(N'TIN101', N'Nhập môn Lập trình', 3, N'Khái niệm lập trình cơ bản, biến, cấu trúc rẽ nhánh, vòng lặp và mảng'),
(N'TIN102', N'Lập trình Hướng đối tượng', 3, N'Lập trình OOP với C#, lớp, đối tượng, tính đóng gói, kế thừa và đa hình'),
(N'WEB301', N'Lập trình Ứng dụng Web', 4, N'Phát triển ứng dụng Web hiện đại với C#, Blazor, Clean Architecture và RESTful API'),
(N'TIN201', N'Cấu trúc Dữ liệu & Giải thuật', 3, N'Danh sách liên kết, ngăn xếp, hàng đợi, cây nhị phân và các thuật toán tìm kiếm/sắp xếp'),
(N'CSDL201', N'Cơ sở Dữ liệu', 3, N'Mô hình quan hệ, đại số quan hệ, chuẩn hóa 3NF và truy vấn ngôn ngữ SQL'),
(N'KTPM302', N'Kỹ thuật Phần mềm', 3, N'Quy trình phát triển phần mềm Agile/Scrum, thiết kế hướng đối tượng UML và kiểm thử'),
(N'ATTT401', N'An toàn Thông tin', 3, N'Mật mã học, bảo mật hệ thống thông tin, xác thực và an toàn mạng ứng dụng'),
(N'TOEIC', N'Tiếng Anh chuyên ngành CNTT', 2, N'Thuật ngữ tiếng Anh chuyên ngành công nghệ thông tin và giao tiếp công sở'),
-- Môn học mở thêm cho Học kỳ 2 (2026_HK2)
(N'WEB302', N'Phát triển Web Nâng cao & Microservices', 4, N'Kiến trúc Microservices, Docker, CI/CD, SignalR thời gian thực và Web API bảo mật'),
(N'AI401', N'Trí tuệ Nhân tạo & Học máy', 3, N'Thuật toán Machine Learning, Deep Learning, Mạng nơ-ron và ứng dụng AI'),
(N'CSDL202', N'Cơ sở Dữ liệu Phân tán & NoSQL', 3, N'Mô hình CSDL phân tán, MongoDB, Redis caching, tính nhất quán dữ liệu và Big Data'),
(N'ATTT402', N'An ninh Mạng & Phòng chống Tấn công', 3, N'Kỹ thuật phân tích mã độc, bảo mật hạ tầng mạng, phòng chống tấn công DDoS và thâm nhập');
GO

-- B. Dữ liệu Điều kiện Tiên quyết (Prerequisites)
-- WEB301 yêu cầu phải qua TIN102 (OOP)
-- KTPM302 yêu cầu phải qua TIN102 (OOP)
-- TIN201 yêu cầu phải qua TIN101 (Nhập môn lập trình)
-- WEB302 yêu cầu phải qua WEB301
-- AI401 yêu cầu phải qua TIN201
-- CSDL202 yêu cầu phải qua CSDL201
-- ATTT402 yêu cầu phải qua ATTT401
INSERT INTO dbo.Prerequisites (CourseId, PrerequisiteCourseId) VALUES
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB301'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'KTPM302'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN201'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB302'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB301')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'AI401'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN201')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'CSDL202'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'CSDL201')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'ATTT402'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'ATTT401'));
GO

-- C. Dữ liệu Sinh viên (Students)
INSERT INTO dbo.Students (StudentCode, FullName, Major, AcademicYear, MaxCreditsPerSemester, Email, PhoneNumber) VALUES
(N'SV21001', N'Nguyễn Văn Tuấn', N'Công nghệ Thông tin', N'K21', 24, N'tuan.nv@student.edu.vn', N'0901234567'),
(N'SV21002', N'Trần Thị Mai', N'Hệ thống Thông tin', N'K21', 20, N'mai.tt@student.edu.vn', N'0912345678'),
(N'SV21003', N'Lê Hoàng Nam', N'Kỹ thuật Phần mềm', N'K21', 24, N'nam.lh@student.edu.vn', N'0923456789'),
(N'SV21004', N'Phạm Minh Anh', N'Công nghệ Thông tin', N'K21', 20, N'anh.pm@student.edu.vn', N'0934567890'),
(N'SV21005', N'Hoàng Đức Huy', N'Khoa học Máy tính', N'K21', 24, N'huy.hd@student.edu.vn', N'0945678901'),
(N'SV21006', N'Vũ Thảo Linh', N'Hệ thống Thông tin', N'K21', 22, N'linh.vt@student.edu.vn', N'0956789012'),
(N'SV21007', N'Đặng Quốc Bảo', N'An toàn Thông tin', N'K21', 24, N'bao.dq@student.edu.vn', N'0967890123'),
(N'SV21008', N'Bùi Phương Thảo', N'Kỹ thuật Phần mềm', N'K21', 24, N'thao.bp@student.edu.vn', N'0978901234'),
(N'SV21009', N'Dương Gia Huy', N'Công nghệ Thông tin', N'K21', 24, N'huy.dg@student.edu.vn', N'0989012345'),
(N'SV21010', N'Lý Khánh Huyền', N'Khoa học Máy tính', N'K21', 22, N'huyen.lk@student.edu.vn', N'0990123456'),
(N'SV21011', N'Trương Quốc Bảo', N'Kỹ thuật Phần mềm', N'K21', 24, N'bao.tq@student.edu.vn', N'0901122334'),
(N'SV21012', N'Đỗ Hoài Nam', N'Công nghệ Thông tin', N'K21', 24, N'nam.dh@student.edu.vn', N'0912233445'),
(N'SV21013', N'Phan Thu Trang', N'Hệ thống Thông tin', N'K21', 20, N'trang.pt@student.edu.vn', N'0923344556'),
(N'SV21014', N'Hồ Nhật Quang', N'An toàn Thông tin', N'K21', 24, N'quang.hn@student.edu.vn', N'0934455667'),
(N'SV21015', N'Lê Thị Hương Giang', N'Khoa học Máy tính', N'K21', 24, N'giang.lt@student.edu.vn', N'0945566778'),
(N'SV21016', N'Trịnh Thế Vinh', N'Kỹ thuật Phần mềm', N'K21', 22, N'vinh.tt@student.edu.vn', N'0956677889'),
(N'SV21017', N'Cao Mỹ Duyên', N'Công nghệ Thông tin', N'K21', 24, N'duyen.cm@student.edu.vn', N'0967788990'),
(N'SV21018', N'Ngô Tấn Phát', N'Hệ thống Thông tin', N'K21', 20, N'phat.nt@student.edu.vn', N'0978899001'),
(N'SV21019', N'Đoàn Bảo Ngọc', N'Khoa học Máy tính', N'K21', 24, N'ngoc.db@student.edu.vn', N'0989900112'),
(N'SV21020', N'Vương Đình Khôi', N'An toàn Thông tin', N'K21', 24, N'khoi.vd@student.edu.vn', N'0990011223');
GO

-- D. Dữ liệu Môn đã hoàn thành (CompletedCourses)
-- SV21001 đã học đạt TIN101 và TIN102 -> Đủ điều kiện học mọi môn
-- SV21002 CHƯA ĐẠT TIN102 -> Dùng để DEMO ca vi phạm Luật 1 (Học phần tiên quyết)!
INSERT INTO dbo.CompletedCourses (StudentId, CourseId, Grade, SemesterCompleted) VALUES
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 8.0, N'2024_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102'), 7.5, N'2024_HK2'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21002'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 6.5, N'2025_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21003'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 7.5, N'2024_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21003'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102'), 8.0, N'2024_HK2'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21003'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'CSDL201'), 8.5, N'2025_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21004'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 8.5, N'2025_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21005'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 7.0, N'2024_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21005'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102'), 7.0, N'2024_HK2'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21006'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 8.0, N'2025_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21007'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 9.0, N'2024_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21007'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102'), 8.5, N'2024_HK2');
GO

-- E. Dữ liệu Lớp học phần mở trong kỳ (Sections: gồm cả 2 Học kỳ 2026_HK1 và 2026_HK2)
INSERT INTO dbo.Sections (SectionCode, CourseId, Instructor, MaxCapacity, CurrentEnrollment, Semester, Room) VALUES
-- Học kỳ 1 (2026_HK1)
(N'WEB301_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB301'), N'TS. Hà Ngọc Long', 50, 0, N'2026_HK1', N'Lab 402'),
(N'WEB301_02', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB301'), N'ThS. Trần Thị Thanh Trúc', 50, 0, N'2026_HK1', N'Lab 403'),
(N'TIN201_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN201'), N'PGS.TS. Lê Hoàng Sơn', 60, 0, N'2026_HK1', N'Phòng A101'),
(N'TIN201_02', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN201'), N'ThS. Đỗ Minh Tuấn', 60, 0, N'2026_HK1', N'Phòng A102'),
(N'CSDL201_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'CSDL201'), N'TS. Phạm Thanh Tùng', 60, 0, N'2026_HK1', N'Phòng B204'),
(N'KTPM302_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'KTPM302'), N'TS. Ngô Bảo Châu', 10, 10, N'2026_HK1', N'Phòng C301'), -- Lớp đầy chỗ (10/10) để demo hết slot
(N'TIN102_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102'), N'ThS. Vũ Mạnh Cường', 60, 0, N'2026_HK1', N'Phòng A205'),
(N'ATTT401_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'ATTT401'), N'PGS.TS. Ngô Văn Dũng', 40, 0, N'2026_HK1', N'Lab 501'),
(N'TOEIC_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TOEIC'), N'ThS. Lý Thị Mỹ Hạnh', 80, 0, N'2026_HK1', N'Hội trường A'),
-- Học kỳ 2 (2026_HK2)
(N'WEB302_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB302'), N'TS. Hà Ngọc Long', 50, 0, N'2026_HK2', N'Lab 402'),
(N'AI401_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'AI401'), N'PGS.TS. Lê Hoàng Sơn', 60, 0, N'2026_HK2', N'Lab 502'),
(N'CSDL202_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'CSDL202'), N'TS. Phạm Thanh Tùng', 60, 0, N'2026_HK2', N'Phòng B301'),
(N'ATTT402_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'ATTT402'), N'PGS.TS. Ngô Văn Dũng', 40, 0, N'2026_HK2', N'Lab 501'),
(N'KTPM302_02', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'KTPM302'), N'TS. Ngô Bảo Châu', 60, 0, N'2026_HK2', N'Phòng C302'),
(N'TOEIC_02', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TOEIC'), N'ThS. Lý Thị Mỹ Hạnh', 80, 0, N'2026_HK2', N'Hội trường B');
GO

-- F. Dữ liệu Thời khóa biểu lớp (SectionSchedules)
-- Lớp WEB301_01: Thứ Hai tiết 1-4
-- Lớp TIN201_01: Thứ Hai tiết 1-3 -> TRÙNG LỊCH với WEB301_01 để DEMO ca vi phạm Luật 2 (Chống trùng lịch)!
-- Lớp TIN201_02: Thứ Sáu tiết 2-4 -> Không trùng
INSERT INTO dbo.SectionSchedules (SectionId, DayOfWeek, StartPeriod, EndPeriod, Room) VALUES
-- Lịch học HK1
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'WEB301_01'), 2, 1, 4, N'Lab 402'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'WEB301_02'), 4, 7, 10, N'Lab 403'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TIN201_01'), 2, 1, 3, N'Phòng A101'), -- Trùng Thứ 2 tiết 1-3 với WEB301_01
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TIN201_02'), 6, 2, 4, N'Phòng A102'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'CSDL201_01'), 3, 7, 9, N'Phòng B204'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'KTPM302_01'), 5, 1, 3, N'Phòng C301'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TIN102_01'), 7, 1, 3, N'Phòng A205'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'ATTT401_01'), 6, 7, 9, N'Lab 501'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TOEIC_01'), 7, 1, 3, N'Hội trường A'),
-- Lịch học HK2
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'WEB302_01'), 3, 1, 4, N'Lab 402'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'AI401_01'), 4, 1, 3, N'Lab 502'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'CSDL202_01'), 5, 7, 9, N'Phòng B301'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'ATTT402_01'), 6, 1, 3, N'Lab 501'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'KTPM302_02'), 2, 7, 9, N'Phòng C302'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TOEIC_02'), 7, 7, 9, N'Hội trường B');
GO

-- G. Dữ liệu Tài khoản Người dùng (UserAccounts)
INSERT INTO dbo.UserAccounts (Username, Password, FullName, Role, StudentId) VALUES
(N'admin', N'admin123', N'Cán bộ Quản trị Đào tạo', N'Admin', NULL),
(N'sv21001', N'123456', N'Nguyễn Văn Tuấn', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001')),
(N'sv21002', N'123456', N'Trần Thị Mai', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21002')),
(N'sv21003', N'123456', N'Lê Hoàng Nam', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21003')),
(N'sv21004', N'123456', N'Phạm Minh Anh', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21004')),
(N'sv21005', N'123456', N'Hoàng Đức Huy', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21005')),
(N'sv21006', N'123456', N'Vũ Thảo Linh', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21006')),
(N'sv21007', N'123456', N'Đặng Quốc Bảo', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21007')),
(N'sv21008', N'123456', N'Bùi Phương Thảo', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21008')),
(N'sv21009', N'123456', N'Dương Gia Huy', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21009')),
(N'sv21010', N'123456', N'Lý Khánh Huyền', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21010')),
(N'sv21011', N'123456', N'Trương Quốc Bảo', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21011')),
(N'sv21012', N'123456', N'Đỗ Hoài Nam', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21012')),
(N'sv21013', N'123456', N'Phan Thu Trang', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21013')),
(N'sv21014', N'123456', N'Hồ Nhật Quang', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21014')),
(N'sv21015', N'123456', N'Lê Thị Hương Giang', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21015')),
(N'sv21016', N'123456', N'Trịnh Thế Vinh', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21016')),
(N'sv21017', N'123456', N'Cao Mỹ Duyên', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21017')),
(N'sv21018', N'123456', N'Ngô Tấn Phát', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21018')),
(N'sv21019', N'123456', N'Đoàn Bảo Ngọc', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21019')),
(N'sv21020', N'123456', N'Vương Đình Khôi', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21020'));
GO

-- H. Dữ liệu Đăng Ký Học Phần Đầy Đủ Cho Tất Cả Các Lớp (Enrollments & EnrollmentLines)
-- Tạo 20 phiếu đăng ký cho 20 sinh viên
INSERT INTO dbo.Enrollments (StudentId, DateRegistered, Semester, Status, UniqueId, PaymentStatus, PaymentDate, PaymentMethod, TransactionReference) VALUES
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001'), DATEADD(day, -10, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0001', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21002'), DATEADD(day, -9, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0002', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21003'), DATEADD(day, -9, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0003', N'Paid', DATEADD(day, -8, GETDATE()), N'Chuyển khoản trực tuyến 24/7', N'VQR202610141020003'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21004'), DATEADD(day, -8, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0004', N'Paid', DATEADD(day, -7, GETDATE()), N'Chuyển khoản trực tuyến 24/7', N'VQR202610150915004'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21005'), DATEADD(day, -8, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0005', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21006'), DATEADD(day, -7, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0006', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21007'), DATEADD(day, -7, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0007', N'Paid', DATEADD(day, -6, GETDATE()), N'Chuyển khoản trực tuyến 24/7', N'VQR202610161430007'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21008'), DATEADD(day, -6, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0008', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21009'), DATEADD(day, -6, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0009', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21010'), DATEADD(day, -5, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0010', N'Paid', DATEADD(day, -4, GETDATE()), N'Chuyển khoản trực tuyến 24/7', N'VQR202610181120010'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21011'), DATEADD(day, -5, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0011', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21012'), DATEADD(day, -4, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0012', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21013'), DATEADD(day, -4, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0013', N'Paid', DATEADD(day, -3, GETDATE()), N'Chuyển khoản trực tuyến 24/7', N'VQR202610190845013'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21014'), DATEADD(day, -3, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0014', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21015'), DATEADD(day, -3, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0015', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21016'), DATEADD(day, -2, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0016', N'Paid', DATEADD(day, -1, GETDATE()), N'Chuyển khoản trực tuyến 24/7', N'VQR202610211610016'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21017'), DATEADD(day, -2, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0017', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21018'), DATEADD(day, -1, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0018', N'Unpaid', NULL, NULL, NULL),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21019'), DATEADD(day, -1, GETDATE()), N'2026_HK1', N'Confirmed', N'ENR-2026-0019', N'Paid', GETDATE(), N'Chuyển khoản trực tuyến 24/7', N'VQR202610220815019'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21020'), GETDATE(), N'2026_HK1', N'Confirmed', N'ENR-2026-0020', N'Unpaid', NULL, NULL, NULL);
GO

-- Chi tiết từng môn của từng sinh viên vào các lớp học phần
INSERT INTO dbo.EnrollmentLines (EnrollmentId, SectionId)
SELECT e.EnrollmentId, s.SectionId
FROM (
    VALUES
    (N'SV21001', N'WEB301_01'), (N'SV21001', N'CSDL201_01'), (N'SV21001', N'ATTT401_01'), (N'SV21001', N'TOEIC_01'),
    (N'SV21002', N'CSDL201_01'), (N'SV21002', N'TIN102_01'), (N'SV21002', N'TOEIC_01'),
    (N'SV21003', N'WEB301_01'), (N'SV21003', N'TIN201_02'), (N'SV21003', N'KTPM302_01'), (N'SV21003', N'TOEIC_01'),
    (N'SV21004', N'TIN201_01'), (N'SV21004', N'CSDL201_01'), (N'SV21004', N'TIN102_01'), (N'SV21004', N'TOEIC_01'),
    (N'SV21005', N'WEB301_01'), (N'SV21005', N'TIN201_02'), (N'SV21005', N'KTPM302_01'), (N'SV21005', N'TOEIC_01'),
    (N'SV21006', N'TIN201_01'), (N'SV21006', N'CSDL201_01'), (N'SV21006', N'TIN102_01'), (N'SV21006', N'TOEIC_01'),
    (N'SV21007', N'WEB301_01'), (N'SV21007', N'CSDL201_01'), (N'SV21007', N'ATTT401_01'), (N'SV21007', N'KTPM302_01'),
    (N'SV21008', N'WEB301_01'), (N'SV21008', N'TIN201_01'), (N'SV21008', N'KTPM302_01'), (N'SV21008', N'TOEIC_01'),
    (N'SV21009', N'WEB301_01'), (N'SV21009', N'TIN201_02'), (N'SV21009', N'KTPM302_01'), (N'SV21009', N'TOEIC_01'),
    (N'SV21010', N'WEB301_01'), (N'SV21010', N'TIN201_01'), (N'SV21010', N'ATTT401_01'), (N'SV21010', N'KTPM302_01'),
    (N'SV21011', N'WEB301_02'), (N'SV21011', N'TIN201_02'), (N'SV21011', N'KTPM302_01'),
    (N'SV21012', N'WEB301_02'), (N'SV21012', N'TIN201_01'), (N'SV21012', N'KTPM302_01'),
    (N'SV21013', N'WEB301_01'), (N'SV21013', N'TIN201_02'), (N'SV21013', N'KTPM302_01'),
    (N'SV21014', N'WEB301_02'), (N'SV21014', N'CSDL201_01'), (N'SV21014', N'TOEIC_01'),
    (N'SV21015', N'WEB301_02'), (N'SV21015', N'TIN201_02'), (N'SV21015', N'CSDL201_01'),
    (N'SV21016', N'WEB301_02'), (N'SV21016', N'CSDL201_01'), (N'SV21016', N'ATTT401_01'),
    (N'SV21017', N'WEB301_02'), (N'SV21017', N'TIN102_01'), (N'SV21017', N'TOEIC_01'),
    (N'SV21018', N'TIN201_01'), (N'SV21018', N'TIN102_01'), (N'SV21018', N'CSDL201_01'),
    (N'SV21019', N'TIN201_01'), (N'SV21019', N'TIN102_01'), (N'SV21019', N'TOEIC_01'),
    (N'SV21020', N'CSDL201_01'), (N'SV21020', N'TIN102_01'), (N'SV21020', N'TOEIC_01')
) AS PlanData(StudentCode, SectionCode)
INNER JOIN dbo.Students stu ON PlanData.StudentCode = stu.StudentCode
INNER JOIN dbo.Enrollments e ON stu.StudentId = e.StudentId AND e.Semester = N'2026_HK1' AND e.Status = N'Confirmed'
INNER JOIN dbo.Sections s ON PlanData.SectionCode = s.SectionCode;
GO

-- Cập nhật sĩ số CurrentEnrollment khớp chính xác với số lượng sinh viên trong EnrollmentLines
UPDATE s
SET s.CurrentEnrollment = ISNULL(enrCount.TotalCount, 0)
FROM dbo.Sections s
LEFT JOIN (
    SELECT el.SectionId, COUNT(*) AS TotalCount
    FROM dbo.EnrollmentLines el
    INNER JOIN dbo.Enrollments e ON el.EnrollmentId = e.EnrollmentId
    WHERE e.Semester = N'2026_HK1' AND e.Status = N'Confirmed'
    GROUP BY el.SectionId
) enrCount ON s.SectionId = enrCount.SectionId
WHERE s.Semester = N'2026_HK1';
GO

-- XÁC NHẬN SỐ LƯỢNG BẢN GHI HOÀN TẤT
SELECT 'Courses' AS TableName, COUNT(*) AS TotalRows FROM dbo.Courses
UNION ALL SELECT 'Students', COUNT(*) FROM dbo.Students
UNION ALL SELECT 'Prerequisites', COUNT(*) FROM dbo.Prerequisites
UNION ALL SELECT 'Sections', COUNT(*) FROM dbo.Sections
UNION ALL SELECT 'SectionSchedules', COUNT(*) FROM dbo.SectionSchedules
UNION ALL SELECT 'CompletedCourses', COUNT(*) FROM dbo.CompletedCourses
UNION ALL SELECT 'Enrollments', COUNT(*) FROM dbo.Enrollments
UNION ALL SELECT 'EnrollmentLines', COUNT(*) FROM dbo.EnrollmentLines
UNION ALL SELECT 'UserAccounts', COUNT(*) FROM dbo.UserAccounts;
GO
