window.safePassageAuthSession = {
    getPendingMfa() {
        return window.sessionStorage.getItem("safePassage.pendingMfaSession");
    },
    savePendingMfa(token) {
        window.sessionStorage.setItem("safePassage.pendingMfaSession", token);
    },
    clearPendingMfa() {
        window.sessionStorage.removeItem("safePassage.pendingMfaSession");
    },
    replaceUrl(url) {
        window.history.replaceState(null, "", url);
    }
};
