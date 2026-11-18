# Backend email activation setup

Account invitations are sent through SMTP. Configure the mail server and sender using .NET user-secrets for local development or a secret manager/environment variables for deployment. Do not commit SMTP credentials.

From the `backend` directory, set the SMTP values:

```powershell
dotnet user-secrets set "Smtp:Host" "smtp.example.com"
dotnet user-secrets set "Smtp:Port" "587"
dotnet user-secrets set "Smtp:EnableSsl" "true"
dotnet user-secrets set "Smtp:FromAddress" "no-reply@example.com"
dotnet user-secrets set "Smtp:Username" "your-smtp-username"
dotnet user-secrets set "Smtp:Password" "your-smtp-password"
```

Set `AccountActivation:PublicUrl` to the reachable web portal URL ending in `/activate`. Development defaults to `http://localhost:5092/activate`; deployment URLs must use HTTPS. The configured SMTP server must support STARTTLS when `EnableSsl` is enabled.

Administrator web sign-in uses a one-time email link followed by mandatory approval from an active trusted mobile device. The link expires after `AdminLogin:TokenLifetimeMinutes` (default 10; allowed range 5–30 minutes), can be used once, and is sent only to active administrators with verified email addresses. Set `AdminLogin:PublicUrl` to the HTTPS web portal URL in production; if omitted, the API derives the portal origin from `AccountActivation:PublicUrl`.

Application autofill passwords are encrypted in the API database with AES-GCM. Set a stable `ApplicationCredentials:EncryptionKey` secret in every API environment (the JWT signing key is used as a fallback); changing the encryption key makes existing saved autofill passwords unreadable.

Create a user from the admin dashboard. The account remains inactive until the user opens the single-use invitation link and sets a password. Activation tokens expire after 60 minutes by default. Local testing requires an SMTP test server or a valid SMTP account; missing or failed SMTP configuration returns an explicit error and does not create an active account.

## OpenID Connect provider

The API exposes an OpenID Connect issuer at `OpenIddict:Issuer` (development default `http://localhost:5143`). Discovery is available at `/.well-known/openid-configuration`. Configure `OpenIddict:LoginUrl` to the web portal sign-in URL. Relying parties use authorization code with PKCE; credentials are entered only on the Safe Passage sign-in page.

In **Applications**, create an OIDC client and enter each callback URL exactly as the relying party provides it. For Grafana OSS on `http://localhost:3000`, use `http://localhost:3000/login/generic_oauth` and scopes `openid profile email`. Copy the client secret displayed after saving; OpenIddict stores its verifier and Safe Passage shows the value only once. Saving the OIDC configuration rotates that secret.

Configure Grafana with `GF_AUTH_GENERIC_OAUTH_ENABLED=true`, `GF_AUTH_GENERIC_OAUTH_NAME=Safe Passage`, `GF_AUTH_GENERIC_OAUTH_CLIENT_ID=<client-id>`, `GF_AUTH_GENERIC_OAUTH_CLIENT_SECRET=<secret>`, `GF_AUTH_GENERIC_OAUTH_SCOPES=openid profile email`, `GF_AUTH_GENERIC_OAUTH_AUTH_URL=<issuer>/connect/authorize`, `GF_AUTH_GENERIC_OAUTH_TOKEN_URL=<issuer>/connect/token`, `GF_AUTH_GENERIC_OAUTH_API_URL=<issuer>/connect/userinfo`, and `GF_AUTH_GENERIC_OAUTH_ALLOW_SIGN_UP=false`. Assign a user or one of their groups to the Grafana app in the Safe Passage admin portal. Unassigned users are denied at authorization.

Production requires separate signing and encryption PFX certificates configured with `OpenIddict:SigningCertificatePath`, `OpenIddict:SigningCertificatePassword`, `OpenIddict:EncryptionCertificatePath`, and `OpenIddict:EncryptionCertificatePassword`. Provide passwords through a secret manager or environment variables. Use HTTPS for the issuer and portal URLs in production.

## Facebook sign-in

Create an app at [Meta for Developers](https://developers.facebook.com/apps/) and add the **Facebook Login** product. Enable web OAuth login and register the API callback URL exactly as:

```text
http://localhost:5143/signin-facebook
```

For a deployed API, register its public HTTPS callback instead. Then configure the Meta app ID and secret locally with user-secrets:

```powershell
dotnet user-secrets set "Facebook:AppId" "your-meta-app-id"
dotnet user-secrets set "Facebook:AppSecret" "your-meta-app-secret"
dotnet user-secrets set "Facebook:FrontendUrl" "http://localhost:5092"
```

The API asks Facebook for the `email` permission plus the default public profile fields. Facebook may omit email when it has no valid address or the user does not grant permission. Facebook sign-in matches an existing active Safe Passage account with a verified matching email, links that Facebook ID, and continues through Safe Passage MFA when policy requires it. It does not create an account automatically. While the Meta app is in development mode, add your Facebook account as an app tester/developer to sign in.

## GitHub sign-in

Create a GitHub OAuth App under **GitHub Settings → Developer settings → OAuth Apps → New OAuth App**. Use `http://localhost:5092` as the homepage URL and register this authorization callback URL exactly:

```text
http://localhost:5143/signin-github
```

Configure the GitHub OAuth app credentials locally from the `backend` directory with user-secrets:

```powershell
dotnet user-secrets set "GitHub:ClientId" "your-github-client-id"
dotnet user-secrets set "GitHub:ClientSecret" "your-github-client-secret"
dotnet user-secrets set "GitHub:FrontendUrl" "http://localhost:5092"
```

Restart the API and web portal. **Continue with GitHub** appears on the Safe Passage sign-in page. Safe Passage requests only the GitHub profile and email permissions, reads the verified primary email, and links it to an existing active, email-verified Safe Passage account. New accounts are not created automatically; successful authentication continues through Safe Passage MFA.

This makes GitHub an upstream sign-in provider for Safe Passage. It does not make GitHub.com use Safe Passage as its login provider: GitHub's managed-user OIDC sign-in requires GitHub Enterprise Cloud and Microsoft Entra ID, while standard GitHub organizations use Enterprise SAML SSO. See [GitHub's OIDC setup requirements](https://docs.github.com/en/enterprise-cloud@latest/admin/managing-iam/configuring-authentication-for-enterprise-managed-users/configuring-oidc-for-enterprise-managed-users) and [GitHub's SAML SSO documentation](https://docs.github.com/en/enterprise-cloud@latest/organizations/managing-saml-single-sign-on-for-your-organization/about-identity-and-access-management-with-saml-single-sign-on).
