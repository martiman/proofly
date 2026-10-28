---
name: dev-server
description: Start the Proofly web dev server on a free port and verify it is really Proofly before anyone opens it in a browser. Use before any visual check or design comparison.
---

# Dev server

Other projects on this machine often hold port 5173, so never assume it.

1. Pick a free port: try 5199, then 5200, 5201 … (`lsof -iTCP:<port> -sTCP:LISTEN` must print nothing).
2. From `apps/web`: `npm run dev -- --port <port> --strictPort` in the background, log to the scratchpad directory.
3. Verify: `curl -s http://localhost:<port>/ | grep -o "<title>[^<]*"` must print `<title>Proofly`. If not, kill your process and pick another port.
4. Tell any agent that will use it the exact URL and that it must not start or stop it.
5. Stop it when the work is finished (`pkill -f "port <port>"`) and confirm the port is free.
