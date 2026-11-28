# Safe Passage Browser Extension

Chrome / Edge Manifest V3 extension that offers Safe Passage approval on detected sign-in pages for saved applications. It does not require the protected site to support OIDC.

## Install Locally (Unpacked)

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select `safe_passage/extension`.

## Usage

1. Click the Safe Passage extension icon to open its side panel and sign in against `https://safe-passage-api.onrender.com`. Approve this initial sign-in on a trusted Safe Passage device. The side panel stays open as you switch browser tabs.
2. Sign in to the extension, open a site, and save its name, URL, username, password, and optional Base32 2FA secret. The extension synchronizes these entries with the backend; secrets are encrypted in the API database.
3. Sites you add are synced to your account immediately; administrator review is not required. Credentials and 2FA codes remain protected by mobile approval at sign-in.
4. On a verified site's login page, a Safe Passage lock screen blocks the site's controls and keyboard focus. Request approval from the lock screen or side panel, approve on your trusted mobile device, and the extension fills and submits saved credentials. If the site requests a saved TOTP code, the extension retrieves and submits it. Otherwise, enter the site's one-time code into the Safe Passage lock screen.
5. The gate unlocks when the extension detects the site has completed sign-in. If the login fails or the site's sign-in flow is not detected, the page remains locked. Save credentials in the side panel to use them for that site.

The extension detects common password, email, and one-time-code sign-in forms, sign-in controls, and signed-out messages. Some federated login flows redirect to a different domain; add that login domain as an application too if the gate does not appear there. Sign-in success detection depends on common site signals such as a sign-out control or a successful redirect; unusual flows may need site-specific detection.

This is a browser-side gate. It does not prevent access through another browser or when the extension is disabled.
