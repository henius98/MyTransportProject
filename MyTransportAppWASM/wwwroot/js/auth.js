window.openAuthPopup = (url) => {
    const width = 550;
    const height = 700;
    const left = (window.screen.width / 2) - (width / 2);
    const top = (window.screen.height / 2) - (height / 2);
    
    const popup = window.open(url, 'auth_popup', 
        `width=${width},height=${height},left=${left},top=${top},menubar=no,toolbar=no,location=no,status=no,resizable=yes,scrollbars=yes`);
    
    if (popup) {
        popup.focus();
    }
};

window.closeWindow = () => {
    if (window.opener && !window.opener.closed) {
        window.opener.location.reload();
    }
    window.close();
};

window.syncMainState = () => {
    if (window.opener && !window.opener.closed) {
        window.opener.location.reload();
    }
};

// Auto-close if we are in the auth popup and navigate back to the main app
if (window.name === 'auth_popup' && !window.location.pathname.includes('/authentication/')) {
    // Small delay to allow the app to potentially sync state if needed
    setTimeout(() => {
        if (window.opener && !window.opener.closed) {
            window.opener.location.reload();
        }
        window.close();
    }, 1000);
}
