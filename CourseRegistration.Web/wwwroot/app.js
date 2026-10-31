// Client-side helper scripts for Course Registration Web App

window.aiChatScrollToBottom = function (selector) {
    setTimeout(function () {
        var el = document.querySelector(selector);
        if (el) {
            el.scrollTo({
                top: el.scrollHeight,
                behavior: 'smooth'
            });
        }
    }, 40);
};

// Quản lý ghi nhớ mật khẩu / tài khoản an toàn cho sinh viên
window.authStorage = {
    saveCredentials: function (username, password, remember) {
        try {
            if (remember) {
                localStorage.setItem('cr_remember_enabled', 'true');
                localStorage.setItem('cr_saved_username', username || '');
                localStorage.setItem('cr_saved_password', password || '');
            } else {
                localStorage.removeItem('cr_remember_enabled');
                localStorage.removeItem('cr_saved_username');
                localStorage.removeItem('cr_saved_password');
            }
        } catch (e) {
            console.warn('LocalStorage error:', e);
        }
    },
    loadCredentials: function () {
        try {
            var enabled = localStorage.getItem('cr_remember_enabled') === 'true';
            if (enabled) {
                return {
                    remember: true,
                    username: localStorage.getItem('cr_saved_username') || '',
                    password: localStorage.getItem('cr_saved_password') || ''
                };
            }
        } catch (e) {
            console.warn('LocalStorage error:', e);
        }
        return { remember: false, username: '', password: '' };
    },
    clearCredentials: function () {
        try {
            localStorage.removeItem('cr_remember_enabled');
            localStorage.removeItem('cr_saved_username');
            localStorage.removeItem('cr_saved_password');
        } catch (e) {
            console.warn('LocalStorage error:', e);
        }
    }
};

// Kích hoạt hỏi lưu mật khẩu trên trình duyệt Google Chrome / Chromium (Chống nhân bản / trùng lặp)
var _lastBrowserSaveTrigger = 0;
window.triggerBrowserSavePassword = function (username, password) {
    if (!username || !password) return;

    // Chống nhân bản: Bỏ qua nếu vừa được gọi trong vòng 1.5 giây
    var now = Date.now();
    if (now - _lastBrowserSaveTrigger < 1500) {
        return;
    }
    _lastBrowserSaveTrigger = now;

    // 1. Đồng bộ giá trị vào input trên form đang mở (nếu có)
    try {
        var uInput = document.getElementById('username');
        var pInput = document.getElementById('password');
        if (uInput) uInput.value = username;
        if (pInput) pInput.value = password;
    } catch (e) { }

    // 2. Tự động lưu vào LocalStorage an toàn
    try {
        // Lưu an toàn thông tin đăng nhập vào LocalStorage
        window.authStorage.saveCredentials(username, password, true);
    } catch (e) { }

    // Đã hoàn tất lưu tài khoản an toàn vào authStorage
    // Tắt lệnh submit form nền vào iframe để ngăn Google Chrome kích hoạt cảnh báo native trễ khi người dùng chuyển sang trang khác (như trang Thanh toán học phí)
    return true;
};

window.notifyWeakPasswordAndSave = function (username, password, isWeak) {
    return window.triggerBrowserSavePassword(username, password);
};

// Quản lý trạng thái đã hiển thị cảnh báo mật khẩu trong phiên (chống lặp lại)
window.hasBreachWarningBeenShown = function (username) {
    try {
        return sessionStorage.getItem('cr_breach_warned_' + username) === 'true';
    } catch (e) {
        return false;
    }
};

window.markBreachWarningShown = function (username) {
    try {
        sessionStorage.setItem('cr_breach_warned_' + username, 'true');
    } catch (e) { }
};






