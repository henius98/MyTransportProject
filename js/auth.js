// Auto-close if we are in the auth popup and navigate back to the main app
if (window.name === 'auth_popup' && !window.location.pathname.includes('/authentication/')) {
    // Small delay to allow the app to potentially sync state if needed
    setTimeout(() => {
        if (window.opener && !window.opener.closed) {
            window.opener.location.reload();
        }
        window.close();
    }, 90);
}
