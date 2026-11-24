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
    },
    getAdminEmailChangeToken() {
        return new URLSearchParams(window.location.hash.slice(1)).get("emailToken");
    },
    getBackupEmailToken() {
        const token = new URLSearchParams(window.location.hash.slice(1)).get("backupEmailToken");
        if (token) window.history.replaceState(null, "", window.location.pathname + window.location.search);
        return token;
    }
};

window.safePassageAppearance = {
    _keys: {
        user: "safePassage.userTheme",
        admin: "safePassage.adminTheme"
    },
    _keyFor(isAdmin) {
        return isAdmin ? this._keys.admin : this._keys.user;
    },
    getTheme(isAdmin = false) {
        const theme = window.sessionStorage.getItem(this._keyFor(isAdmin));
        return theme === "dark" ? "dark" : "light";
    },
    setTheme(theme, isAuthenticated, isAdmin = false) {
        if (!isAuthenticated || (theme !== "light" && theme !== "dark")) {
            this.reset();
            return;
        }
        window.sessionStorage.setItem(this._keyFor(isAdmin), theme);
        document.documentElement.dataset.spTheme = theme;
    },
    initialize(isAuthenticated, isAdmin = false) {
        if (!isAuthenticated) {
            this.reset();
            return "light";
        }
        const theme = this.getTheme(isAdmin);
        document.documentElement.dataset.spTheme = theme;
        return theme;
    },
    reset() {
        window.sessionStorage.removeItem(this._keys.user);
        window.sessionStorage.removeItem(this._keys.admin);
        document.documentElement.dataset.spTheme = "light";
    }
};
