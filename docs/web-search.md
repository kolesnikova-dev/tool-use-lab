# Web search

Web search is off by default because each search costs about one cent and sends Claude's queries off your machine.

## Turning it on

Type `/web on` at the prompt. `/web status` shows how many searches were used out of the session limit (20 by default), and `/web off` turns it off again.

## Limits

Claude may run at most 3 searches per request. Only an allowlist of domains is searched: learn.microsoft.com, platform.claude.com, anthropic.com, github.com, developer.mozilla.org and wikipedia.org. Override the list with `WEB_SEARCH_ALLOWED_DOMAINS` in `.env`.
