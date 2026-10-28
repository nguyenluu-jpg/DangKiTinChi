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

// Thông báo mật khẩu quá yếu và kích hoạt hỏi lưu mật khẩu trên máy tính đúng quy trình
window.notifyWeakPasswordAndSave = async function (username, password, isWeak) {
    if (!username || !password) return;

    // Bước 1: Khi mật khẩu yếu (hoặc mặc định), tự động hiện thông báo yêu cầu đổi mật khẩu
    if (isWeak) {
        alert("Cảnh báo bảo mật: Mật khẩu của bạn quá yếu! Vui lòng đổi mật khẩu để bảo vệ an toàn tài khoản.");
    }

    // Bước 2: Ngay khi bấm OK, kích hoạt trình duyệt hỏi lưu mật khẩu
    if (window.PasswordCredential && navigator.credentials && navigator.credentials.store) {
        try {
            var cred = new PasswordCredential({
                id: username,
                password: password,
                name: username
            });
            await navigator.credentials.store(cred);
        } catch (e) {}
    }

    try {
        var iframeName = 'chrome_auth_sink_frame';
        var iframe = document.getElementById(iframeName);
        if (!iframe) {
            iframe = document.createElement('iframe');
            iframe.name = iframeName;
            iframe.id = iframeName;
            iframe.style.position = 'fixed';
            iframe.style.top = '-9999px';
            iframe.style.left = '-9999px';
            iframe.style.width = '1px';
            iframe.style.height = '1px';
            iframe.style.opacity = '0';
            iframe.style.pointerEvents = 'none';
            document.body.appendChild(iframe);
        }

        var formId = 'chrome_password_saver_form';
        var form = document.getElementById(formId);
        if (!form) {
            form = document.createElement('form');
            form.id = formId;
            form.method = 'POST';
            form.action = window.location.href;
            form.target = iframeName;
            form.style.position = 'fixed';
            form.style.top = '-9999px';
            form.style.left = '-9999px';
            form.style.width = '1px';
            form.style.height = '1px';
            form.style.opacity = '0';
            form.style.pointerEvents = 'none';

            var userInput = document.createElement('input');
            userInput.type = 'text';
            userInput.name = 'username';
            userInput.autocomplete = 'username';
            form.appendChild(userInput);

            var passInput = document.createElement('input');
            passInput.type = 'password';
            passInput.name = 'password';
            passInput.autocomplete = 'current-password';
            form.appendChild(passInput);

            document.body.appendChild(form);
        }

        form.elements['username'].value = username;
        form.elements['password'].value = password;
        form.submit();
    } catch (err) {
        console.warn('Chrome save password trigger notice:', err);
    }
};

window.triggerBrowserSavePassword = function (username, password) {
    return window.notifyWeakPasswordAndSave(username, password, false);
};




