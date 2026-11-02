# Company Finance integration demo

This Expo app demonstrates an external application checking an authenticated user's access through Safe Passage. It sends no real financial data.

## Run the demo

1. Start the Safe Passage API and admin portal. The API seeds the `Company Finance Test App` application with ID `66666666-6666-6666-6666-666666666666`.
2. In the admin portal, open **Applications → Manage access** for that app and assign the user or group you want to test.
3. Configure the API URL for the device running Expo:

   ```powershell
   $env:EXPO_PUBLIC_API_BASE_URL = "http://localhost:5143"
   ```

   For an Android emulator, `http://10.0.2.2:5143` is used by default. For a physical phone, use the computer's LAN IP and make sure the phone can reach port `5143`.
4. Start the app:

   ```powershell
   npm ci
   npx expo start
   ```

5. Sign in with an account that has an enrolled Safe Passage MFA device. The seeded development account is `john.doe` / `Password123!`.

The app waits for mobile MFA approval, then calls `POST /api/integrations/access/verify` with the returned bearer token and the demo application ID. Assigned users with any required trusted device are allowed into the demonstration dashboard; other users see the denial reason.
