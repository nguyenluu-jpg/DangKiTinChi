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

// Kích hoạt hỏi lưu mật khẩu trên trình duyệt Google Chrome / Chromium
var _lastBrowserSaveTrigger = 0;
window.triggerBrowserSavePassword = async function (username, password) {
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
        window.authStorage.saveCredentials(username, password, true);
    } catch (e) { }

    // 3. Chuẩn Credential Management API của W3C / Chromium (Google Chrome & Edge)
    // Giúp Google Chrome nhận diện đăng nhập thành công và bật thông báo "Save password?"
    var credentialStored = false;
    if (window.PasswordCredential && navigator.credentials && navigator.credentials.store) {
        try {
            var cred = new PasswordCredential({
                id: username,
                password: password,
                name: username
            });
            await navigator.credentials.store(cred);
            credentialStored = true;
        } catch (e) {
            console.debug('Credential store status:', e);
        }
    }

    // 4. Fallback form submit nếu Credential Management API chưa được hỗ trợ
    if (!credentialStored) {
        try {
            var currentUrl = window.location.href;
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
                form.action = currentUrl;
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

                var submitBtn = document.createElement('button');
                submitBtn.type = 'submit';
                form.appendChild(submitBtn);

                document.body.appendChild(form);
            } else {
                form.action = currentUrl;
            }

            form.elements['username'].value = username;
            form.elements['password'].value = password;
            if (typeof form.requestSubmit === 'function') {
                form.requestSubmit();
            } else {
                form.submit();
            }
        } catch (err) {
            console.debug('Browser save trigger notice:', err);
        }
    }
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






