window.safePassagePayPal = (function () {
    let activeListener = null;
    let activePollTimer = null;
    let activePopup = null;

    function cleanup() {
        if (activeListener) {
            window.removeEventListener("message", activeListener);
            activeListener = null;
        }
        if (activePollTimer) {
            window.clearInterval(activePollTimer);
            activePollTimer = null;
        }
        activePopup = null;
    }

    return {
        openCheckoutPopup(approvalUrl, expectedSubscriptionId, dotNetRef) {
            cleanup();

            const width = 520;
            const height = 720;
            const left = Math.max(0, Math.round(window.screenX + (window.outerWidth - width) / 2));
            const top = Math.max(0, Math.round(window.screenY + (window.outerHeight - height) / 2));
            const features = `width=${width},height=${height},left=${left},top=${top},resizable=yes,scrollbars=yes`;

            activePopup = window.open(approvalUrl, "safePassagePayPalCheckout", features);
            if (!activePopup) {
                return false;
            }

            let settled = false;
            activeListener = function (event) {
                if (event.origin !== window.location.origin) {
                    return;
                }
                const data = event.data;
                if (!data || data.type !== "paypal-subscription-return") {
                    return;
                }
                settled = true;
                cleanup();
                const subId = data.subscriptionId || expectedSubscriptionId;
                if (data.status === "success") {
                    dotNetRef.invokeMethodAsync("OnPayPalApproved", subId);
                } else {
                    dotNetRef.invokeMethodAsync("OnPayPalCancelled", subId);
                }
            };

            window.addEventListener("message", activeListener);

            activePollTimer = window.setInterval(function () {
                if (!activePopup || activePopup.closed) {
                    cleanup();
                    if (!settled) {
                        dotNetRef.invokeMethodAsync("OnPayPalPopupClosed", expectedSubscriptionId);
                    }
                }
            }, 500);

            return true;
        },

        notifyOpenerAndClose(status, subscriptionId) {
            if (window.opener && !window.opener.closed) {
                window.opener.postMessage(
                    {
                        type: "paypal-subscription-return",
                        status: status,
                        subscriptionId: subscriptionId || ""
                    },
                    window.location.origin
                );
                window.close();
                return true;
            }
            return false;
        },

        dispose() {
            cleanup();
        }
    };

    window.safePassageReceipt = {
        printHtml(html) {
            const iframe = document.createElement("iframe");
            iframe.style.position = "fixed";
            iframe.style.right = "0";
            iframe.style.bottom = "0";
            iframe.style.width = "0";
            iframe.style.height = "0";
            iframe.style.border = "0";
            document.body.appendChild(iframe);

            const doc = iframe.contentWindow.document;
            doc.open();
            doc.write(html);
            doc.close();

            setTimeout(() => {
                try {
                    iframe.contentWindow.focus();
                    iframe.contentWindow.print();
                } finally {
                    setTimeout(() => iframe.remove(), 1000);
                }
            }, 150);
        }
    };
})();
