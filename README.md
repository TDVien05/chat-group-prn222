# Chat Group Solution

This repository now follows the `AGENTS.md` architecture:

- `src/ChatClient.Wpf`: presentation layer with WPF views, view models, and commands
- `src/ChatClient.Business`: business layer with interfaces, models, validation, and application service orchestration
- `src/ChatClient.Infrastructure`: infrastructure layer with TCP networking, file history repository, DTOs, and config
- `src/ChatServer.Console`: console host for the standalone server

## Run the WPF client

```powershell
dotnet run --project .\src\ChatClient.Wpf\ChatClient.Wpf.csproj
```

## Run the console server

```powershell
dotnet run --project .\src\ChatServer.Console\ChatServer.Console.csproj
```

Optional custom port:

```powershell
dotnet run --project .\src\ChatServer.Console\ChatServer.Console.csproj -- 6000
```

## Features

- host a room server and share the device IP
- connect to a host by IP and port
- messenger-style room UI in WPF
- text and icon messages
- room history persisted to `%LocalAppData%\ChatGroupServer\History`

## Protocol

The transport is newline-delimited JSON over TCP.

Join:

```json
{"type":"join","room":"general","user":"alice"}
```

Text:

```json
{"type":"message","content":"hello everyone"}
```

Icon:

```json
{"type":"icon","content":":+1:","icon":"thumbs-up"}
```
