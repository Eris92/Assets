# Assets Portal preview

Static UI preview developed in Sites. Users, hierarchy, equipment, reports and API responses are demo data in browser memory. Reloading resets them. No production Jira, Entra or AD services are contacted.

Run: `python -m http.server 8080 --bind 127.0.0.1` from this directory, then open http://127.0.0.1:8080.

Includes all Sites UI changes: filtered equipment lists, manager hierarchy and reports, counters, grouped navigation, handover and permissions placeholders, Jira settings and Entra placeholder.

The ASP.NET Core application at repository root remains separate. Production backend integration is still required.
