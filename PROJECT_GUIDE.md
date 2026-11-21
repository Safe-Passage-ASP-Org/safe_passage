# Safe Passage Project Guide

## 1. Project Description

Safe Passage is an identity and access security project. Users sign in through the web portal or mobile app, manage trusted devices, and review their saved applications. A trusted mobile device can approve sign-in requests. Administrators manage registered applications and decide which users or groups can access them. The browser extension can use a user's saved site credentials after mobile approval.

## 2. Project Stack

| Part | Stack | What it does |
| --- | --- | --- |
| Backend API (`backend/`) | C#, ASP.NET Core 10, Entity Framework Core, OpenIddict | Handles sign-in, MFA, devices, application access, saved sites, and API data. |
| Web portal (`frontend-web/`) | Blazor Server, .NET 10 | Provides user and administrator pages and sends requests to the backend API. |
| Mobile app (`mobile/`) | Expo, React Native, JavaScript | Provides mobile sign-in, trusted-device setup, MFA approvals, activity, and settings. |
| Browser extension (`extension/`) | JavaScript, Chrome/Edge Manifest V3 | Syncs users' saved sites and provides approval-gated autofill on supported login pages. |
| Database | PostgreSQL on Supabase for the deployed app; SQLite can be used locally | Stores accounts, devices, application assignments, activity, and saved sites. |

## 3. How Vercel and Render Connect to the Project

- **Render hosts the backend API** at `https://safe-passage-api.onrender.com`. It handles requests from the web portal, mobile app, and browser extension, and reads and writes project data in the configured Supabase PostgreSQL database.
- **Render also hosts the web portal** at `https://safe-passage-web.onrender.com`. The portal calls the Render API; it does not keep a separate copy of account or application data.
- **Vercel hosts the mobile web build** at `https://safepassage-mobile.vercel.app/`. The web build uses `EXPO_PUBLIC_API_URL` to call the Render API. For production, that setting should point to `https://safe-passage-api.onrender.com`.
- **The mobile app and browser extension also use the API.** The native mobile build gets its API address from `EXPO_PUBLIC_API_URL` or the development defaults in `mobile/config.js`. The extension connects to the configured Safe Passage API.

In short, Vercel and the web/mobile clients provide user interfaces; Render runs the API that connects them to the shared database. Signing in or saving an application through one client uses the same backend account data as the other clients.

## 4. Sign In to the Web Portal

### User account

1. Open [Safe Passage Web](https://safe-passage-web.onrender.com/) and choose **User Login** if prompted.
2. Enter the account's email address or username and password, then select **Sign in**.
3. If the account is new, activate it first using the link sent to its email address.
4. If Safe Passage requests additional verification, open Safe Passage Mobile on a different trusted device and approve the sign-in. Keep the browser page open while it checks for approval.
5. After sign-in, the portal opens the user's dashboard. **My Applications** shows applications assigned to the user and sites saved in their vault.

### Administrator account

1. Open [Safe Passage Admin Login](https://safe-passage-web.onrender.com/admin-login).
2. Enter the verified email address for the administrator account and request a verification link. If needed, choose **Other verification method** to use the email OTP option.
3. Open the email and follow the one-time link, or enter the OTP on the admin verification page.
4. Approve the sign-in on a different trusted mobile device when prompted.
5. After approval, the administrator portal opens. Admin accounts must use **Admin Login**; the regular user login is for user accounts.

## 5. Sign In to the Mobile App

1. Open Safe Passage Mobile from its installed Android/iOS app, or open the [mobile web app](https://safepassage-mobile.vercel.app/).
2. Enter the account's email address or username and password, then select **Sign in**. New accounts must first be activated through the email link.
3. For a first-time phone setup, use **Set up this phone** if shown and enter the same account credentials. Keep the phone's device key in its secure storage.
4. If sign-in is waiting for approval, open Safe Passage on a different trusted mobile device and approve or deny the request. The phone starting the sign-in cannot approve its own request.
5. Once sign-in completes, use the dashboard and tabs to view approvals, devices, activity, and settings.

## 6. Add an Application with an Administrator Account

1. Sign in through **Admin Login** and complete email verification and mobile approval.
2. Open **Applications** in the administrator portal and select **Add application**.
3. Enter the **Application Name**, **Login/Web URL**, and **Category**. Add a description or autofill username/password if needed, then select **Save application**.
4. In the application's card, select **Manage access**.
5. Select the users and/or groups that should receive access, then save the assignments.
6. Those assignments determine which users see the application under **My Applications**. Adding an application by itself does not assign it to every account.

## 7. Add a Custom Site with a User Account

1. Install the extension in Chrome or Edge. Open the browser's extensions page, turn on **Developer mode**, select **Load unpacked**, and choose the project's `extension/` folder. If you already installed it, skip this step.
2. Open the Safe Passage extension from the browser toolbar. Enter the Safe Passage API URL (`https://safe-passage-api.onrender.com`), your Safe Passage username or email, and password; select **Sign in & Wait for MFA**. Approve this extension sign-in on a trusted mobile device if requested.
3. Open the website you want to save, then open the extension popup again. The **Login/Web URL** is filled with the current site's address; edit it if the site's login page uses a different URL. Enter a **Label** and choose a **Category**.
4. Optionally add a description, your website username/email, password, and Base32 2FA secret. Select **Save credentials for this site**.
5. The saved site syncs to **My Applications** and the user's Credential Vault. When you visit its supported login page, the extension shows a Safe Passage prompt. Request approval and approve it on a trusted mobile device before the extension fills the saved credentials or 2FA code.

The site and its credentials belong to the user who saved them. They do not create an administrator-wide application or grant other accounts access. Saved passwords and 2FA secrets are stored encrypted by the backend. Sites can also be added from **Credential Vault** in the web portal; the extension and portal sync the same user sites.
