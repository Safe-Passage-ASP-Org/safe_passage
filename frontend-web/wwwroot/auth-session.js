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
    },
    getAdminEmailToken() {
        return new URLSearchParams(window.location.hash.slice(1)).get("emailToken");
    }
};
