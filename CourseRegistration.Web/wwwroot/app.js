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

// Kích hoạt trình quản lý mật khẩu của trình duyệt (Chrome, Edge, Safari...) tự động hỏi lưu mật khẩu trên máy
window.triggerBrowserSavePassword = function (username, password) {
    if (window.PasswordCredential && navigator.credentials && navigator.credentials.store) {
        try {
            var cred = new PasswordCredential({
                id: username,
                password: password,
                name: username
            });
            navigator.credentials.store(cred).catch(function () {});
        } catch (e) {
            // Trình duyệt có thể tự xử lý qua sự kiện submit form
        }
    }
};


