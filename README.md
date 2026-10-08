<div align="center">

# Amarin Admin AI

**A chat with an AI model that manages this computer.**

Ask in plain words, and the program does the work: it reads the Windows logs, edits the registry,
fixes services and disks, installs and updates software. Before anything that can't be undone, it
asks you and waits for your answer.

[![Release](https://img.shields.io/github/v/release/Qvisono/Amarin-Admin-AI?label=release&color=0078D6)](https://github.com/Qvisono/Amarin-Admin-AI/releases/latest)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D6?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/GPL--3.0-blue)

<img src="Amarin%20Admin%20AI/Assets/Guide/app-01-chat.png" width="860" alt="Main window with a conversation">

</div>

---

## What it is

A chat window on your desktop. It is a single `.exe` that needs no installation. Answers come from
[Venice.ai](https://venice.ai) and [OpenRouter](https://openrouter.ai) models, paid with your own
key. You can use both services at once and give different tasks to different models. Conversations
stay on your disk.

Unlike a chat in the browser, the model here does more than write text. It has about forty tools
on this computer: PowerShell, the registry, services and scheduled tasks, disks and SMART, the
network and the firewall, event logs, `winget`, SFC and DISM, restore points, screenshots, the
clipboard, web search and reading web pages. So instead of advice like "open Device Manager and
take a look", you get the job done and a report on what was done.

When a task needs more than one answer, the chat hands it to an agent. The agent works step by
step, picks its own tools and comes back with the result. Every tool call is listed in the
conversation, and you can expand it to see the details. The program chooses between a light model
and a strong one by the task itself, not by how long the message is.

Asking again doesn't throw away the previous answer. **Retry** and editing your question keep the
earlier answer, and everything after it, as a separate version. The ‹ 2/3 › arrows under the
message switch between versions.

You can reply to any part of an earlier answer, even one from the start of a long chat: select a
passage and press **Reply**. The model receives exactly those words, marked as the part you are
answering.

You can teach it things once. **Settings → Instructions** holds your own notes on specific topics:
a name, a few trigger words and the text. The chat model sees only the names and trigger words.
When a message touches one of these topics, the model reads the instruction and follows it, and the
reply shows which instruction was used. Instructions are plain Markdown files. You can turn them
off, export and import them.

It can also work without being asked. With **scheduled tasks**, the agent checks the computer
daily, weekly or at startup, in read-only mode, and leaves a report as a chat. If something needs
attention, you get a notification. The **PC health** panel shows disks, memory, protection, recent
errors and pending updates on one screen. An action you want to repeat can be saved as a
**recipe** and run again without a model. For bigger jobs, the agent can first send a **plan** for
you to approve. A chat can work on **another computer** over PowerShell Remoting, and you can add
tools from **MCP servers** to the built-in ones.

Hundreds of chats stay easy to manage: folders, tags and an archive, full-text search across all
chats, export to Markdown, HTML or PDF, per-chat settings, a separate draft for each chat, voice
input, and the cost of the whole chat next to an estimate for the next answer.

---

## What it looks like

<div align="center">

<img src="Amarin%20Admin%20AI/Assets/Guide/app-02-confirm.png" width="780" alt="Confirmation request">

<sup>The agent reports every step and stops before anything that can't be undone. It explains in
plain words what is about to happen, and the exact command is under the arrow.</sup>

<img src="Amarin%20Admin%20AI/Assets/Guide/app-03-models.png" width="780" alt="Choosing a model">

<sup>The model and the reasoning level are in the row under the input field, next to the account
balances and the context fill. Every answer shows its price, so spending never comes as a
surprise.</sup>

</div>

---

## Installation

**1. Download the program.** The [Releases](https://github.com/Qvisono/Amarin-Admin-AI/releases/latest)
page has one file, `Amarin-Admin-AI-v<version>-win-x64.exe`, and `SHA256SUMS` with its checksum.
You don't need to install .NET: it is built in.

**2. Give the program a key.** A Venice key, an OpenRouter key or both will do. Paste them into
**Settings → Keys & spending**. The key is stored on disk encrypted by Windows, so only your user
account can read it. You can add as many keys as you like, name them and assign each task its own
key. The same page shows the account balances and a chart of spending by day.

If you don't want the program to store the key, set an environment variable instead. The program
reads it at startup and never saves it anywhere.

```powershell
[Environment]::SetEnvironmentVariable('VENICE_API_KEY', 'your-key-here', 'User')
[Environment]::SetEnvironmentVariable('OPENROUTER_API_KEY', 'your-key-here', 'User')
```

Variables are read only at startup, so restart the program after running these commands. A key
pasted on the settings page works right away.

**3. Start the program and send a message.** That's all the setup.

> Where to get a key, what it costs and how to limit spending: the program has a guide with
> screenshots in **Settings → About → Getting started**, five steps from sign-up to the first
> answer.
>
> The program finds new versions on GitHub and installs them, but only after checking the SHA-256
> checksum and, if your copy is signed, the publisher's signature. Before updating, it shows the
> release notes. The previous version is kept, and one button in **Settings → About** brings it
> back. Pre-releases come only if you turn on the beta channel.

---

## Safety

The model works on a real system, so it has limits, and they are on by default.

**Dangerous actions need your approval.** Registry changes, service management, installing and
removing software, firewall rules and disk cleanup run only after you agree, and the request says
exactly what will change. If you refuse, the model sees the refusal and looks for another way. The
chat's own tools go through the same checks as the agent's: only a new file in Downloads or on the
Desktop is written without asking, and nothing can write into the program's own data folder.

**You decide how much it can do.** **Settings → Security** has four access modes: normal, ask
about everything, read only, and approve everything automatically. You can also turn off
individual tools; the model doesn't even see a tool that is off. When you approve an action, you
can allow the tool until the end of the reply or for the whole chat.

**You see what will happen.** For the agent levels you choose, the agent first looks around in
read-only mode and sends a plan for approval. Only the approved steps run without asking again.
When a script's commands support it, the request shows a `-WhatIf` dry run of the changes.
**Stop** ends running scripts and programs immediately.

**PowerShell is parsed, not guessed.** The program analyses each script's syntax tree. Only
scripts that just read run without asking, and the request lists what the script will change.

**A second model checks the intent.** A list of dangerous cmdlets shows what a command touches,
but not why it was written. A script that collects passwords and sends them away may not contain a
single suspicious cmdlet. So a separate guard model reads every agent step before it runs.

**Changes can be undone.** Before a change, the program saves the state of services, scheduled
tasks, startup entries and the affected registry keys, value by value. A rollback restores what
was changed and removes what was added, and shows you its plan first. Where rollback is not
possible (firewall rules, Windows features), the request says so.

**Everything is logged.** Every call that changes the system and every refusal goes into an audit
log: the chat, the arguments (without secrets), who allowed it and what the guard said. The log
stays when chats are deleted and can be exported to CSV and JSON.

**Some limits can't be lifted.** Deleting files through PowerShell or the file tool is forbidden.
Downloads come only from allowed domains. The current user can't be disabled, and the last
administrator can't be removed. Critical Windows processes and services can't be stopped. Browser
password stores, SSH keys, password managers and the Windows credential stores are never read.
BitLocker keys and passwords are never shown.

**Spending has limits too.** You can set daily and monthly limits for the profile or for a single
key, and a cap per reply. The program warns you when spending gets close to a limit and asks
before going past it. Scheduled runs have their own cap and never ask.

**Your key stays private.** On disk it is encrypted by Windows, or it stays in an environment
variable if you chose that. It is not in `appsettings.json`, never appears in a conversation, is
removed from crash reports, and is sent only to the provider it belongs to.

---

## Where your data lives

Everything is stored in `%APPDATA%\Amarin Admin AI`: conversations (attachments are in a folder
next to each chat), settings, drafts, recipes and scheduled tasks, interface translations, and
your instructions (`instructions\*.md`, one file each). The program has no cloud and no accounts.
Several people can share one computer: each profile has its own chats and settings, and a profile
can be locked with a password.

Conversations can be stored encrypted by Windows (**Settings → Security**), so only your Windows
account can read them. A profile with a password can lock itself after a few minutes of
inactivity, or when you ask.

All data can be exported to one archive and imported back, with an optional password. A single
chat can be saved as JSON or shared as a code you can send to someone. **Delete all data** erases
the current profile and starts it fresh.

Backups can run automatically, daily or weekly, into a folder you choose (keys are not included,
and backups are not encrypted). Old chats can be archived or deleted after a set number of days.
The **Data** page shows what takes up space and cleans up logs, old snapshots and exports.
Settings, profiles and keys keep a spare copy: if a file is damaged, it is set aside and the last
good copy is used.

---

## Working with Windows

A tray icon shows whether the program is answering or waiting for you, and the close button can
hide the window to the tray. A global shortcut (Win+Shift+A by default) shows or hides the window
from anywhere; two more start a chat with the clipboard text or a screenshot. The program can
start with Windows directly to the tray, adds **Ask Amarin** to the Explorer menu for files and
folders, and keeps recent chats in its taskbar jump list. Notifications appear as the program's
own card or as Windows notifications, whichever you choose.

---

## Appearance

About fifty palettes, dark and light, each shown as a card with a preview. On top of a palette
you can set your own accent colour and a background: a moving gradient or your own picture, with
the panels turning translucent over it. Interface scale from 80 to 250%, built-in fonts, chat
column width, and film grain over the window.

The interface is in Russian and English. With one button, a model translates it into any other
language, which then appears in the list next to the others. You can correct a machine
translation line by line, and your corrections are kept when you translate again.

Every button and switch has a name for screen readers, the keyboard focus is drawn in the accent
colour, Windows high contrast is applied automatically, and you can change the chat text size and
the code font.

---

## Building from source

You need the .NET 10 SDK and Windows.

```powershell
dotnet build "Amarin Admin AI/Amarin Admin AI.csproj"
```

```powershell
dotnet test Amarin.AdminAI.Tests/Amarin.AdminAI.Tests.csproj
```

There are more than three thousand tests. Some of them start real WPF and check the window, the
chat layout and the order in which popups close, without showing anything on screen.

The release build is a single self-contained file:

```powershell
dotnet publish "Amarin Admin AI/Amarin Admin AI.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release/publish
```

Check the tools without a single API call (no key needed):

```powershell
& ".\Amarin Admin AI.exe" --smoke-tools
```

`--smoke-report <file>` writes the same run to a file as a Markdown table instead of a console
window. This is how CI runs it; tools whose Windows component is missing are marked `SKIP`.

Found a problem? **Report a problem** on the About page (or in the crash window) opens a GitHub
issue with the report filled in. Your profile path and user name are removed from it.

---

## License

[GNU General Public License v3.0](LICENSE.txt). You can use, modify and distribute it, as long as
derivative works stay under the same license. The program is provided "as is", without warranty.
