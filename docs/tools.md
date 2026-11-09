# ToolUseLab tools

ToolUseLab is a console chat with Claude that can call tools. Each tool is declared in `ToolSchemas.cs`, named in `ToolNames`, and executed by `ChatService.ExecuteTool`.

## Reminders

The `set_reminder` tool stores a reminder with a message and an ISO 8601 timestamp. If web pages were read earlier in the conversation, the app asks for confirmation before setting a reminder, because a web page could have tried to talk Claude into it.

## Dates

`get_current_datetime` returns the current time in a .NET custom date format such as `yyyy-MM-dd`. `add_duration_to_datetime` adds seconds, minutes, hours, days, weeks, months or years to a datetime. Month and year arithmetic handles leap years.

## Text editor

Claude can view, create and edit files through Anthropic's text editor tool. Every write asks for confirmation (`Apply this edit? [y/N]`). A backup of the original is kept in `.backups/`, and `/undo <path>` restores it. `/clean-backups` deletes all backups.
