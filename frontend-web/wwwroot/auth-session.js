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
    getAdminEmailChangeToken() {
        return new URLSearchParams(window.location.hash.slice(1)).get("emailToken");
    },
    getBackupEmailToken() {
        const token = new URLSearchParams(window.location.hash.slice(1)).get("backupEmailToken");
        if (token) window.history.replaceState(null, "", window.location.pathname + window.location.search);
        return token;
    },
    toLocalDateTimeInput(value) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return "";
        const pad = part => String(part).padStart(2, "0");
        return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
    },
    getUtcOffsetMinutes(value) {
        const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);
        if (!parts) throw new Error("The browser could not read the selected local date and time.");
        const [, year, month, day, hour, minute, second = "0"] = parts;
        const date = new Date(Number(year), Number(month) - 1, Number(day), Number(hour), Number(minute), Number(second));
        if (date.getFullYear() !== Number(year)
            || date.getMonth() !== Number(month) - 1
            || date.getDate() !== Number(day)
            || date.getHours() !== Number(hour)
            || date.getMinutes() !== Number(minute))
            throw new Error("Choose a real local date and time.");
        return -date.getTimezoneOffset();
    },
    downloadTextFile(filename, content) {
        const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    async copyText(text) {
        if (!navigator.clipboard || !navigator.clipboard.writeText) {
            return false;
        }
        await navigator.clipboard.writeText(text);
        return true;
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
