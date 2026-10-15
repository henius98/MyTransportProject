function safeGetItem(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

function safeSetItem(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch {
        // Ignore storage access errors in restricted environments
    }
}

window.themeManager = {
    setTheme: function (isDarkMode, key) {
        if (isDarkMode) {
            document.body.classList.remove('theme-light');
            document.documentElement.setAttribute('data-bs-theme', 'dark');
            safeSetItem('theme-mode-' + key, 'dark');
            safeSetItem('theme-mode-global', 'dark');
        } else {
            document.body.classList.add('theme-light');
            document.documentElement.setAttribute('data-bs-theme', 'light');
            safeSetItem('theme-mode-' + key, 'light');
            safeSetItem('theme-mode-global', 'light');
        }
    },
    initialize: function (key) {
        var savedTheme = safeGetItem('theme-mode-' + key) || safeGetItem('theme-mode-global');
        var isDark = savedTheme !== 'light'; // Default to dark
        if (isDark) {
            document.body.classList.remove('theme-light');
            document.documentElement.setAttribute('data-bs-theme', 'dark');
        } else {
            document.body.classList.add('theme-light');
            document.documentElement.setAttribute('data-bs-theme', 'light');
        }
        return isDark;
    }
};
