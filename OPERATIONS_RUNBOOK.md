# Safe Passage Operations & Recovery Runbook

## 1. Health & Readiness Endpoints

| Endpoint | Purpose | Expected Response |
| --- | --- | --- |
| `GET /health` / `GET /health/live` | Process liveness probe (Render / uptime monitors) | `200 OK` `{ "status": "Healthy", "check": "liveness" }` |
| `GET /health/ready` | Dependency readiness probe (verifies PostgreSQL/SQLite connectivity without exposing connection details) | `200 OK` (`Ready`) or `503 Service Unavailable` (`Unhealthy`) |
| `GET /api/status` | Deployment metadata & commit verification for Status Hub | `200 OK` with `status`, `system`, `commit`, `timestamp` |

## 2. Monitoring, Structured Logging & Alerting

- **Uptime & Keep-Alive Monitors**:
  - `cron-job.org` pings `https://safe-passage-api.onrender.com/health/ready` (or `/api/status`) and `https://safe-passage-web.onrender.com/` every 5 minutes to prevent cold starts and alert on non-2xx responses.
  - `https://safepassage-status-hub.vercel.app/` aggregates live GitHub Actions CI/CD, Vercel, and Render status.
- **Privacy-Safe Structured Logging**:
  - Application logs record structured events (`EventId`, HTTP status, exception type, user/device GUIDs where required) without logging plaintext passwords, JWTs, activation tokens/URLs, OTPs, or database connection strings.
- **Actionable Alert Conditions**:
  - **API Availability / DB Connectivity**: Any `503` on `/health/ready` or 2+ consecutive failed monitor pings triggers an alert to on-call maintainers.
  - **Email Delivery (Brevo)**: Monitor Render API logs for `IActivationEmailSender` non-2xx responses (such as `401` or `429`) and check Brevo transactional logs.
  - **MFA & Authentication Failures**: Alert on spikes in `401`/`403`/`503` responses on `/api/auth/login`, `/api/mfa/*`, and `/mfaHub`.

## 3. Database Backup, Retention & Restore Rehearsal

- **Primary Database**: Supabase managed PostgreSQL (encrypted at rest with AES-256, TLS in transit via Session Pooler).
- **Retention & Recovery Objectives**:
  - **RPO (Recovery Point Objective)**: <= 24 hours (daily automated backups; <= 1 hour if Supabase Point-in-Time Recovery is enabled).
  - **RTO (Recovery Time Objective)**: <= 30 minutes for schema + data restore into a clean database instance.
  - **Retention**: Minimum 7 daily encrypted snapshots (30 days for production/audit compliance), access restricted to project owners.
- **Backup & Clean-Environment Restore Rehearsal Procedure**:
  1. Export an encrypted logical snapshot from the source database:
     ```bash
     pg_dump "$SOURCE_DATABASE_URL" --format=custom --no-owner --no-acl -f safepassage_backup.dump
     ```
  2. Provision a clean target database (or local PostgreSQL container) and restore:
     ```bash
     pg_restore --dbname="$TARGET_DATABASE_URL" --clean --if-exists --no-owner --no-acl safepassage_backup.dump
     ```
  3. Point a staging `safe-passage-api` instance at `$TARGET_DATABASE_URL`, run `GET /health/ready` (expect `200 OK` / `Ready`), and verify row counts in `"Users"`, `"Devices"`, and `"ActivityLogs"`.
  4. Delete temporary local `.dump` files after verification.

## 4. Incident Response & Credential Rotation

1. **Triage**: Check Status Hub, Render service logs (`safe-passage-api`, `safe-passage-web`), Vercel logs (`safepassage-mobile`), and `GET /health/ready`.
2. **Containment & Credential Rotation**:
   - **JWT Signing Key (`Jwt__SigningKey`)**: Rotate in Render environment variables and redeploy `safe-passage-api` (invalidates active sessions; users re-authenticate).
   - **Brevo API Key (`Brevo__ApiKey`)**: Generate a new key in Brevo SMTP & API settings, update Render `Brevo__ApiKey`, redeploy, and revoke the old key.
   - **Database Credentials (`ConnectionStrings__DefaultConnection`)**: Rotate password in Supabase Dashboard -> update Render `ConnectionStrings__DefaultConnection` -> redeploy.
   - **PayPal / OAuth Secrets**: Rotate in provider dashboard, update Render environment variables, and redeploy.
3. **Data Retention & Deletion**: Follow [DATA_DELETION.md](./DATA_DELETION.md) and [PRIVACY.md](./PRIVACY.md) for user erasure requests and audit log retention.

## 5. Release Rollback Procedure

1. Identify the last known-good commit SHA in `main` or `development` via GitHub Actions or `/api/status`.
2. In Render (`safe-passage-api` / `safe-passage-web`), trigger **Manual Deploy** on the previous healthy deploy, or revert the offending PR on GitHub (`git revert -m 1 <merge-sha>`) and push to `development`/`main`.
3. Confirm `GET /health/ready` returns `200 OK` and `GET /api/status` reports the expected commit SHA.
