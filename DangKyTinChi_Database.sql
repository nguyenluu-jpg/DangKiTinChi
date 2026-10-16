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
(N'TOEIC', N'Tiếng Anh chuyên ngành CNTT', 2, N'Thuật ngữ tiếng Anh chuyên ngành công nghệ thông tin và giao tiếp công sở');
GO

-- B. Dữ liệu Điều kiện Tiên quyết (Prerequisites)
-- WEB301 yêu cầu phải qua TIN102 (OOP)
-- KTPM302 yêu cầu phải qua TIN102 (OOP)
-- TIN201 yêu cầu phải qua TIN101 (Nhập môn lập trình)
INSERT INTO dbo.Prerequisites (CourseId, PrerequisiteCourseId) VALUES
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB301'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'KTPM302'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102')),
((SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN201'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'));
GO

-- C. Dữ liệu Sinh viên (Students)
INSERT INTO dbo.Students (StudentCode, FullName, Major, AcademicYear, MaxCreditsPerSemester, Email, PhoneNumber) VALUES
(N'SV21001', N'Nguyễn Văn Tuấn', N'Công nghệ Thông tin', N'K21', 24, N'tuan.nv@student.edu.vn', N'0901234567'),
(N'SV22002', N'Trần Thị Mai', N'Hệ thống Thông tin', N'K22', 20, N'mai.tt@student.edu.vn', N'0912345678');
GO

-- D. Dữ liệu Môn đã hoàn thành (CompletedCourses)
-- SV21001 đã học đạt TIN101 và TIN102 -> Đủ điều kiện học mọi môn
-- SV22002 CHƯA ĐẠT TIN102 -> Dùng để DEMO ca vi phạm Luật 1 (Học phần tiên quyết)!
INSERT INTO dbo.CompletedCourses (StudentId, CourseId, Grade, SemesterCompleted) VALUES
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 8.0, N'2024_HK1'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102'), 7.5, N'2024_HK2'),
((SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV22002'), (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN101'), 6.5, N'2025_HK1');
GO

-- E. Dữ liệu Lớp học phần mở trong kỳ (Sections)
INSERT INTO dbo.Sections (SectionCode, CourseId, Instructor, MaxCapacity, CurrentEnrollment, Semester, Room) VALUES
(N'WEB301_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB301'), N'TS. Nguyễn Văn A', 50, 1, N'2026_HK1', N'Lab 402'),
(N'WEB301_02', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'WEB301'), N'ThS. Trần Thị B', 50, 0, N'2026_HK1', N'Lab 403'),
(N'TIN201_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN201'), N'PGS. Lê Hoàng C', 60, 0, N'2026_HK1', N'Phòng A101'),
(N'TIN201_02', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN201'), N'ThS. Đỗ Minh D', 60, 0, N'2026_HK1', N'Phòng A102'),
(N'CSDL201_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'CSDL201'), N'TS. Phạm Thanh E', 60, 0, N'2026_HK1', N'Phòng B204'),
(N'KTPM302_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'KTPM302'), N'TS. Ngô Bảo F', 50, 50, N'2026_HK1', N'Phòng C301'), -- Lớp đầy chỗ (50/50) để demo hết slot
(N'TIN102_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TIN102'), N'ThS. Vũ Mạnh G', 60, 0, N'2026_HK1', N'Phòng A205'),
(N'ATTT401_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'ATTT401'), N'PGS. Ngô Văn K', 40, 1, N'2026_HK1', N'Lab 501'),
(N'TOEIC_01', (SELECT CourseId FROM dbo.Courses WHERE CourseCode = N'TOEIC'), N'ThS. Lý Thị N', 80, 1, N'2026_HK1', N'Hội trường A');
GO

-- F. Dữ liệu Thời khóa biểu lớp (SectionSchedules)
-- Lớp WEB301_01: Thứ Hai tiết 1-4
-- Lớp TIN201_01: Thứ Hai tiết 1-3 -> TRÙNG LỊCH với WEB301_01 để DEMO ca vi phạm Luật 2 (Chống trùng lịch)!
-- Lớp TIN201_02: Thứ Sáu tiết 2-4 -> Không trùng
INSERT INTO dbo.SectionSchedules (SectionId, DayOfWeek, StartPeriod, EndPeriod, Room) VALUES
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'WEB301_01'), 2, 1, 4, N'Lab 402'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'WEB301_02'), 4, 7, 10, N'Lab 403'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TIN201_01'), 2, 1, 3, N'Phòng A101'), -- Trùng Thứ 2 tiết 1-3 với WEB301_01
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TIN201_02'), 6, 2, 4, N'Phòng A102'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'CSDL201_01'), 3, 7, 9, N'Phòng B204'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'KTPM302_01'), 5, 1, 3, N'Phòng C301'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TIN102_01'), 7, 1, 3, N'Phòng A205'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'ATTT401_01'), 6, 7, 9, N'Lab 501'),
((SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TOEIC_01'), 7, 1, 3, N'Hội trường A');
GO

-- G. Dữ liệu Tài khoản Người dùng (UserAccounts)
INSERT INTO dbo.UserAccounts (Username, Password, FullName, Role, StudentId) VALUES
(N'admin', N'admin123', N'Cán bộ Quản trị Đào tạo', N'Admin', NULL),
(N'sv21001', N'123456', N'Nguyễn Văn Tuấn', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001')),
(N'sv22002', N'123456', N'Trần Thị Mai', N'Student', (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV22002'));
GO

-- H. Dữ liệu Phiếu Đăng Ký Mẫu (Enrollments & EnrollmentLines)
DECLARE @stu1 INT = (SELECT StudentId FROM dbo.Students WHERE StudentCode = N'SV21001');
DECLARE @secWeb INT = (SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'WEB301_01');
DECLARE @secAttt INT = (SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'ATTT401_01');
DECLARE @secToeic INT = (SELECT SectionId FROM dbo.Sections WHERE SectionCode = N'TOEIC_01');

INSERT INTO dbo.Enrollments (StudentId, DateRegistered, Semester, Status, UniqueId)
VALUES (@stu1, GETDATE(), N'2026_HK1', N'Confirmed', N'SAMPLE-ENROLLMENT-001');

DECLARE @newEnrollId INT = SCOPE_IDENTITY();

INSERT INTO dbo.EnrollmentLines (EnrollmentId, SectionId) VALUES
(@newEnrollId, @secWeb),
(@newEnrollId, @secAttt),
(@newEnrollId, @secToeic);
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
