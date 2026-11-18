# Safe Passage Browser Extension

Chrome / Edge Manifest V3 extension that places a browser-side approval gate on saved sites. Opening a saved site requests Safe Passage mobile approval, then unlocks the page and optionally autofills saved login credentials. The protected site does not need to support OIDC.

## Install Locally (Unpacked)

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select `safe_passage/extension`.

## Usage

1. Click the Safe Passage extension icon and sign in against `https://safe-passage-api.onrender.com`. Approve this initial sign-in on a trusted Safe Passage device.
2. Open a site you want protected, then open the extension popup and click **Save Custom Site**. Site credentials are optional and encrypted locally with WebCrypto AES-GCM.
3. Whenever you open a saved site, the extension covers the page and sends an approval request to Safe Passage Mobile. The page unlocks after approval. If you saved site credentials and the page has a login form, the extension autofills them after approval.
4. If you signed in to Safe Passage Mobile after the request started, use **Resend approval request** on the gate.

The optional Safe Passage app ID checks the user's assigned access before requesting MFA. It does not configure the site's login or require OIDC. Without an app ID, the extension still requests MFA but does not check a Safe Passage app assignment.

This is a browser-side gate. It does not prevent access through another browser or when the extension is disabled.
