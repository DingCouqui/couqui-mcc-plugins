# Chat log rotator

Writes received chat to text files that rotate by day, ISO week or month. It keeps the chat-log feature set and adds a rotation period that a command changes and persists.

## Settings

In `plugins/userdata/chat-log-rotator/settings.toml`:

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to stop logging without unloading the plugin. |
| `Add_DateTime` | `true` | Prepend `yyyy-MM-dd HH:mm:ss` to every logged line. Also `/chat-log-rotator timestamp <on/off>`. |
| `Rotation` | `"day"` | `day`, `week` or `month`. Prefer the command when the client is running. |
| `Log_Directory` | `"../../chat-log/data"` | Default resolves to the original chat-log data folder. `"."` is this plugin's own data folder. Relative paths resolve against this plugin's data folder. |
| `Log_File` | `"chatlog-%username%-%serverip%-%period%.txt"` | File name template. `%username%`, `%serverip%` and `%period%` are expanded. `%period%` is added before the extension when missing. |
| `Filter` | `"messages"` | `all`, `messages`, `chat`, `private_chat` or `internal_msg`. Also `/chat-log-rotator filter <value>`. |

`internal_msg` is accepted for compatibility but records nothing: the new command system does not expose internal command results to plugins.

## Commands

```text
/chat-log-rotator
/chat-log-rotator status
/chat-log-rotator period day
/chat-log-rotator period week
/chat-log-rotator period month
/chat-log-rotator filter all
/chat-log-rotator filter messages
/chat-log-rotator filter chat
/chat-log-rotator filter private_chat
/chat-log-rotator filter internal_msg
/chat-log-rotator timestamp on
/chat-log-rotator timestamp off
```

`period`, `filter` and `timestamp` write the new value to `settings.toml` immediately, so it survives a restart. The next message follows the new value; no timer or restart is involved. `status` prints the rotation, filter, timestamp state, directory, current bucket and the file the next message would use.

## Rotation buckets

| Rotation | `%period%` | Example |
| --- | --- | --- |
| `day` | `yyyy-MM-dd` | `chatlog-Steve-mc.example.com-25565-2026-10-20.txt` |
| `week` | ISO 8601 week, Monday start | `chatlog-Steve-mc.example.com-25565-2026-W43.txt` |
| `month` | `yyyy-MM` | `chatlog-Steve-mc.example.com-25565-2026-10.txt` |

Recommended file name formats:

- Keep chat-log's ordering, period last: `chatlog-%username%-%serverip%-%period%.txt`
- Period first, so a directory listing groups one server's files: `chatlog-%period%-%username%-%serverip%.txt`
- Group by server first: `%serverip%/chatlog-%username%-%period%.txt` is not supported, because the file name template must stay one path segment. Set `Log_Directory` instead.
- If another tool expects a date in front, use `%period%-chatlog-%username%-%serverip%.txt`.

## Log record format

The lines match chat-log:

```text
2026-10-20 18:30:05 Chat bob: hello
2026-10-20 18:30:09 Private alice: secret
2026-10-20 18:31:00 Other: <the raw classified line>
```

## Migration from chat-log

The default `Log_Directory` points at `userdata/chat-log/data`, so this plugin writes where chat-log wrote. Disable or uninstall chat-log (without `purge`) to avoid two plugins recording the same chat into different file names.

## Requirements and limits

- A chat message is classified by MCC and then written with `File.AppendAllText`. A failed write logs a warning and does not stop the session.
- The file is chosen from the message's own wall-clock time, so a suspended process, a reconnect, or a late timer cannot move a line into the wrong period.
- No size cap and no automatic deletion. Remove old files yourself or schedule cleanup outside MCC.
