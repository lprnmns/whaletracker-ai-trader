# WhaleTracker Mission Control UI

React/Vite frontend for the live WhaleTracker universe view.

## Runtime

The UI does not use mock data. It expects the API to be running on `http://localhost:5090` and proxies:

- `/api/*`
- `/hubs/mission-control`
- `/login.html`
- `/js/*`
- `/css/*`

## Development

```bash
npm install
npm run dev -- --host 127.0.0.1
```

Open `http://127.0.0.1:5173`, log in with the API admin credentials, then use the mission control screen.

## Verification

```bash
npm run build
```
