# Safe Passage Browser Extension

Chrome / Edge Manifest V3 extension that lets users register custom sites (treated like Safe Passage applications), wait for Safe Passage authentication + MFA approval, and autofill login forms only after API verification succeeds.

## Install Locally (Unpacked)

1. Open `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode**.
3. Click **Load unpacked** and select `safe_passage/extension`.

## Usage

1. Click the Safe Passage extension icon and sign in against `https://safe-passage-api.onrender.com` (approve MFA on mobile if prompted).
2. Navigate to any custom site's login page, open the extension popup, fill in your site username/password (encrypted locally via WebCrypto AES-GCM), and click **Save Custom Site**.
3. Whenever you visit that site's login page, Safe Passage prompts to authenticate and autofills after API approval.
