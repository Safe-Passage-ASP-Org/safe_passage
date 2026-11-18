# Safe Passage Browser Extension

Chrome / Edge Manifest V3 extension that offers Safe Passage approval on detected sign-in pages for saved applications. It does not require the protected site to support OIDC.

## Install Locally (Unpacked)

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select `safe_passage/extension`.

## Usage

1. Click the Safe Passage extension icon and sign in against `https://safe-passage-api.onrender.com`. Approve this initial sign-in on a trusted Safe Passage device.
2. Open a site, open the extension popup, and add an application with its name, login/web URL, and category. Description and autofill credentials are optional. Passwords are encrypted locally with WebCrypto AES-GCM.
3. On a saved site, the extension shows a small **Authenticate with Safe Passage?** prompt when it detects a sign-in form or a signed-out state. It stays hidden on ordinary signed-in pages. Click **Request approval**, approve on your trusted Safe Passage device, then continue signing in. Saved credentials are filled after approval.
4. If the approval request is pending, use **Resend request** in the prompt.

The extension detects common password and email sign-in forms, sign-in controls, and signed-out messages. Some federated login flows redirect to a different domain; add that login domain as an application too if the prompt does not appear there.

This is a browser-side gate. It does not prevent access through another browser or when the extension is disabled.
