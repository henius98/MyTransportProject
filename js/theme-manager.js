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
    setTheme: function (isDarkMode) {
        var nextTheme = isDarkMode ? 'dark' : 'light';
        var shouldBeLight = !isDarkMode;
        if (document.body.classList.contains('theme-light') !== shouldBeLight) {
            document.body.classList.toggle('theme-light', shouldBeLight);
        }
        if (document.body.classList.contains('theme-dark') !== !!isDarkMode) {
            document.body.classList.toggle('theme-dark', !!isDarkMode);
        }
        if (document.documentElement.getAttribute('data-bs-theme') !== nextTheme) {
            document.documentElement.setAttribute('data-bs-theme', nextTheme);
        }
        if (safeGetItem('theme-mode-global') !== nextTheme) {
            safeSetItem('theme-mode-global', nextTheme);
        }
    }
};
