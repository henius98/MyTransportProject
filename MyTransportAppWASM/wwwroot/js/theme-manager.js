window.themeManager = {
    setTheme: function (isDarkMode, key) {
        if (isDarkMode) {
            document.body.classList.remove('theme-light');
            document.documentElement.setAttribute('data-bs-theme', 'dark');
            localStorage.setItem('theme-mode-' + key, 'dark');
            localStorage.setItem('theme-mode-global', 'dark');
        } else {
            document.body.classList.add('theme-light');
            document.documentElement.setAttribute('data-bs-theme', 'light');
            localStorage.setItem('theme-mode-' + key, 'light');
            localStorage.setItem('theme-mode-global', 'light');
        }
    },
    initialize: function (key) {
        var savedTheme = localStorage.getItem('theme-mode-' + key) || localStorage.getItem('theme-mode-global');
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
