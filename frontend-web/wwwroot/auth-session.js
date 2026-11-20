window.safePassageAuthSession = {
    _mfaKey(mode) {
        return mode === "admin" ? "safePassage.pendingMfaSession.admin" : "safePassage.pendingMfaSession.user";
    },
    getPendingMfa(mode) {
        return window.sessionStorage.getItem(this._mfaKey(mode));
    },
    savePendingMfa(token, mode) {
        window.sessionStorage.setItem(this._mfaKey(mode), token);
    },
    clearPendingMfa(mode) {
        window.sessionStorage.removeItem(this._mfaKey(mode));
    },
    replaceUrl(url) {
        window.history.replaceState(null, "", url);
    },
    getAdminEmailToken() {
        return new URLSearchParams(window.location.hash.slice(1)).get("emailToken");
    }
};
