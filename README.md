This project is to create a reminder with Claude via tool use.

The language of choice is C#, this is for learning purposes. I am most comfortable with TypeScript, and I believe it provides me with a good launchpad to pick up C#. 

## Current state of things
![alt text](image.png)

## The loop
```text
User: "Remind me 3 days after April 3, 2025 to call the dentist"
  │
  ▼
[you] client.messages.create(model, messages, tools=ToolSchemas.All)
  │
  ▼
Claude sees:  tools = [ add_duration_to_datetime {input_schema...}, set_reminder {...} ]
  │
  ▼
Claude returns:  stop_reason = "tool_use"
  content = [
    { type: "tool_use", name: "add_duration_to_datetime",
      input: { datetime_str: "2025-04-03", duration: 3, unit: "days" },
      id: "toolu_01..." }
  ]
  │
  ▼
[you] look up name → call Tools.AddDurationToDatetime("2025-04-03", 3, "days")
  → DurationResult.Ok(2025-04-06)
  │
  ▼
[you] send back:
  messages += { role: "user", content: [
      { type: "tool_result", tool_use_id: "toolu_01...",
        content: "New datetime: Sunday, April 06, 2025 12:00:00 AM" }
  ]}
  │
  ▼
Claude now knows the answer, emits second tool_use: set_reminder(content, timestamp)
  ... loop again ...
  │
  ▼
Claude emits final text: "I've set a reminder for April 6."
```

## Add a new tool

A tool lives in four places. Each step below must use the same tool name and the same argument keys, or Claude's call will fall through to `Unknown tool`.

1. **Name it** in `ToolNames` (`ChatService/Tools.cs`). Add a `const string` in `snake_case`, e.g. `public const string DeleteReminder = "delete_reminder";`. It must be a `const` because it is used as a `case` label.
2. **Implement it** in the `Tools` class (`ChatService/Tools.cs`) as a `static` method. Return a `string` (or a result type you convert to a string). Throwing is fine: `ExecuteTool` catches exceptions and reports them to Claude as an error result.
3. **Describe it** by adding a `new Tool { ... }` to `ToolSchemas.All` (`ChatService/ToolSchemas.cs`):
   - `Name = ToolNames.YourTool`
   - `Description`: what it does and when Claude should use it. This is what Claude reads to decide whether to call it.
   - `InputSchema.Properties`: one entry per argument, with a `type` and a `description`.
   - `Required`: only the arguments Claude must always send (an empty list is fine).
4. **Dispatch it** by adding a `case ToolNames.YourTool:` to `ExecuteTool` (`ChatService/ChatService.cs`). Read the arguments with `GetString` / `GetDouble` using the exact property names from step 3, call your `Tools` method, and return `(content, isError)`.
5. **Try it**: run the app and ask for something that should trigger the tool. Check that Claude calls it, that the arguments arrive as expected, and that the final answer uses the result.

Things worth knowing:
- Serialization is handled by the SDK. Tool input arrives already parsed, and the `content` string you return is wrapped in a `tool_result` block for you. Only call `JsonSerializer.Serialize` if you want to return structured data as a string.
- Missing optional arguments come back as `null` (strings) or the fallback (numbers), so give them defaults in the `Tools` method.
- Return plain, readable text. Claude uses it to write the final reply.
- If Claude never calls the tool, improve the `Description` first.
## Web search (opt-in)
Anthropic's `web_search_20250305` server tool. Anthropic runs the search; the app declares it, shows the sources, and contains the risk.

### How to use web search tool
Web search is **off until you turn it on**: every search costs money and sends Claude's queries off your machine.

1. At the `you>` prompt, type `/web on`. You should see `Web search ON…`, the allowed domains and `Searches used: 0/20.`
2. Ask something that needs current information, e.g. `What is the latest stable .NET release according to Microsoft Learn?`
3. After the answer you get `Searched: "…"` (the query Claude ran) and a `Sources:` list of the pages it cited.
4. `/web status` shows how many searches you have used; `/web off` turns search off again.

Don't ask Claude whether it can search the web: with search off it will truthfully say no, and with it on its answer is only a guess. Use `/web status` to see the real state.

If `/web on` says search is **unavailable**, look at the start of the session for `Web search disabled: …`. It names the bad setting, usually a malformed `WEB_SEARCH_*` value in `.env`. With no such values the defaults apply and nothing is needed.

Defaults: results only come from `learn.microsoft.com`, `platform.claude.com`, `anthropic.com`, `github.com`, `developer.mozilla.org` and `wikipedia.org`; at most 3 searches per request and 20 per session. A question those sites cannot answer finds nothing.

| Command | Effect |
| --- | --- |
| `/web on` / `/web off` | Turn search on or off (off at startup) |
| `/web status` | Searches used out of the session limit |

Optional `.env` settings: `WEB_SEARCH_ALLOWED_DOMAINS` (comma list, replaces the default allowlist), `WEB_SEARCH_MAX_USES` (per request, default 3), `WEB_SEARCH_SESSION_LIMIT` (default 20). An invalid value disables search and says why.

**Cost and privacy:** $10 per 1,000 searches. Claude writes the queries, so conversation content can leave your machine; each query is printed. No location is sent.

**Safety:** only allowlisted, ASCII-only domains. Search results are untrusted: once any page has been read, file edits and reminders show a warning and ask first, and all model/web text is stripped of terminal control codes before printing. Residual risk: pages on allowed sites (e.g. GitHub, Wikipedia) can still contain injected instructions, so read the confirmation prompts.

Every domain you allow must be crawlable by Anthropic: one blocked domain (such as `stackoverflow.com`) makes the API reject the whole request.
