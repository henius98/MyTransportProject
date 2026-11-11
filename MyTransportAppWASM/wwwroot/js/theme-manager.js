function safeSetItem(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch {
        // Ignore storage access errors in restricted environments
    }
}

window.themeManager = {
    setTheme: function (isDarkMode) {
        document.body.classList.toggle('theme-light', !isDarkMode);
        document.documentElement.setAttribute('data-bs-theme', isDarkMode ? 'dark' : 'light');
        safeSetItem('theme-mode-global', isDarkMode ? 'dark' : 'light');
    }
};
