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
