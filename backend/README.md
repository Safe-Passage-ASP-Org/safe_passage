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

Create a user from the admin dashboard. The account remains inactive until the user opens the single-use invitation link and sets a password. Activation tokens expire after 60 minutes by default. Local testing requires an SMTP test server or a valid SMTP account; missing or failed SMTP configuration returns an explicit error and does not create an active account.
