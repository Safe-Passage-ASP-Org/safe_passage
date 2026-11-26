# Company Finance integration demo

Company Finance is a fictional finance dashboard used to demonstrate Safe Passage application access. It contains sample figures only and does not store or process real financial data. The launchable web demo is hosted as `/finance-demo` in the Safe Passage web portal so it uses the same deployment and API configuration.

## Launch from Safe Passage

1. Sign in to the Safe Passage web portal with a user account.
2. Open **My Applications** and find **Company Finance Test App**. If it is not assigned yet, open **Browse applications**, request access, and ask an administrator to approve the request (or have the administrator assign the app directly).
3. Select **Launch**. The app asks you to sign in again and approve the request on your trusted mobile device.
4. After approval, the backend checks the account, app assignment, and device policy. The demo dashboard opens only when the check passes.

The app is seeded with its fixed ID `66666666-6666-6666-6666-666666666666`, published in Browse applications, and configured to allow access requests. The API updates its launch URL at startup. Local development uses `http://localhost:5092/finance-demo`; Render uses the Safe Passage web portal URL.

## Run the companion Expo app locally

The `App.js` Expo client is available for demonstrations on an emulator or device:

1. Start the Safe Passage API and mobile approval app.
2. From this directory, install and start Expo:

   ```powershell
   npm ci
   $env:EXPO_PUBLIC_API_BASE_URL = "http://localhost:5143"
   npx expo start
   ```

   Android emulators use `http://10.0.2.2:5143` by default. For a physical phone, set `EXPO_PUBLIC_API_BASE_URL` to the computer's LAN IP and make sure the phone can reach the API.
3. Sign in with an account assigned to Company Finance, approve the sign-in on its trusted device, and confirm that the client verifies access before showing sample financial data.
