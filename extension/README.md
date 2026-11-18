# Safe Passage Browser Extension

Chrome / Edge Manifest V3 extension that offers Safe Passage approval on detected sign-in pages for saved applications. It does not require the protected site to support OIDC.

## Install Locally (Unpacked)

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select `safe_passage/extension`.

## Usage

1. Click the Safe Passage extension icon and sign in against `https://safe-passage-api.onrender.com`. Approve this initial sign-in on a trusted Safe Passage device.
2. Sign in to the extension, open a site, and save its name, URL, username, password, and optional Base32 2FA secret. The extension synchronizes these entries with the backend; secrets are encrypted in the API database.
3. New sites are pending until an administrator verifies them from **Admin Dashboard → Applications → Websites awaiting verification**. Only verified sites can use saved credentials or 2FA codes.
4. On a verified site's login page, the extension shows an inline Safe Passage prompt. Click **Request approval**, approve on your trusted mobile device, and the extension fills saved username/password fields. If the site requests a TOTP code later, the extension gets a fresh code from the backend and fills the detected 2FA field. Sites added through the web **Credential Vault** are synchronized to the extension, and vice versa.
4. If the approval request is pending, use **Resend request** in the prompt.

The extension detects common password and email sign-in forms, sign-in controls, and signed-out messages. Some federated login flows redirect to a different domain; add that login domain as an application too if the prompt does not appear there.

This is a browser-side gate. It does not prevent access through another browser or when the extension is disabled.
