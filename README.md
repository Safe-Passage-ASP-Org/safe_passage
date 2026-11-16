# Safe Passage

Identity, MFA, and device trust platform consisting of an ASP.NET Core 10 API (`backend/`), an ASP.NET Core Blazor Server Admin/Web Portal (`frontend-web/`), and an Expo React Native Mobile/Web client (`mobile/`).

## Deployed Services

- **Backend API (Render):** `https://safe-passage-api.onrender.com` (Health check: `https://safe-passage-api.onrender.com/api/status`)
- **Mobile Web App (Vercel):** `https://safepassage-mobile.vercel.app/`
- **Database:** Supabase PostgreSQL (Session Pooler)

## Deploying the Admin Web Portal (`frontend-web`) on Render

`frontend-web/` is a Blazor Server (.NET 10) application, so it must be deployed as a **Docker Web Service** on Render (not Vercel).

1. In Render, click **New → Web Service** and connect `Safe-Passage-ASP-Org/safe_passage`.
2. Configure the service:
   - **Name:** `safe-passage-web`
   - **Language:** `Docker`
   - **Branch:** `development` (or `main`)
   - **Region:** `Singapore (Southeast Asia)`
   - **Root Directory:** *(leave blank)*
   - **Dockerfile Path:** `frontend-web/Dockerfile`
3. Add Environment Variables:
   - `ASPNETCORE_ENVIRONMENT=Production`
   - `Api__BaseUrl=https://safe-passage-api.onrender.com/`
4. Deploy the service. Once live, add its URL to `safe-passage-api` CORS in Render:
   - `Cors__AllowedOrigins__10=https://safe-passage-web.onrender.com`
   - `Cors__AllowedOrigins__11=https://safepassage-mobile.vercel.app`
