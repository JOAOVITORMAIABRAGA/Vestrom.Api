# Vestrom API

ASP.NET Core backend for Vestrom Database. Designed to run inside Debian 13/PRoot on the Galaxy S9 while PostgreSQL runs natively in Termux.

## Configuration

Set these environment variables outside the repository:

- `ConnectionStrings__PostgreSQL`
- `Vestrom__JwtKey` (random secret, 32+ characters)
- `Vestrom__PublicHostName` (optional)
- `Vestrom__TermuxBatteryCommand` (optional)
- `Vestrom__CorsOrigins__0` (for example `http://localhost:5173`)

## First admin

After configuring the PostgreSQL connection and JWT key, run:

```text
dotnet run -- --create-admin
```

The command creates/updates the `vestrom_users` table and stores only a password hash. It never stores the plaintext password.

## Run

```text
dotnet run
```

The frontend can then use `VITE_API_URL=http://localhost:5229`.

## API

Public: `GET /api/public/health`, `GET /api/public/status`

Authenticated: `POST /api/auth/login`, `GET /api/auth/me`, `POST /api/auth/logout`, `GET /api/dashboard`, `GET /api/logs`

Admin: `GET /api/databases`, `GET /api/databases/{id}`, `POST /api/databases/query`

SQL execution is limited to one statement per request, 20k characters and 30 seconds, with a 500-row response cap. Authorization is enforced server-side.
