<div align="center">

# Amarin Admin AI

**A chat with a model that runs this computer.**

Ask in plain words, and the program itself reads the Windows logs, edits the registry,
sorts out services and disks, installs and updates software. Anything irreversible it asks
about separately and waits for your answer.

[![Release](https://img.shields.io/github/v/release/Qvisono/Amarin-Admin-AI?label=release&color=0078D6)](https://github.com/Qvisono/Amarin-Admin-AI/releases/latest)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D6?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/GPL--3.0-blue)

<img src="Amarin%20Admin%20AI/Assets/Guide/app-01-chat.png" width="860" alt="Main window with a conversation">

</div>

---

## What it is

An ordinary chat window on your desktop — a single `.exe` that installs nothing and registers
itself nowhere. Answers come from [Venice.ai](https://venice.ai) and
[OpenRouter](https://openrouter.ai) models on your own key — you can keep both accounts at once
and give different tasks different models; the conversations stay on your disk.

What sets it apart from a chat in the browser is that the model here does more than write text.
It has some forty tools on this very machine: PowerShell, the registry, services and scheduled
tasks, disks and SMART, the network and the firewall, event logs, `winget`, SFC and DISM,
restore points, screenshots, the clipboard, web search and page reading. So instead of "open
Device Manager and have a look" you get the work done and an account of how it went.

When a task can't be solved in a single answer, the chat hands it to an agent: it works step by
step, picks its own tools and comes back with the result, and a report on every call is right
there in the conversation — expand it and read it. Who takes the job — a light model or a
strong one — the program decides by itself, by the task, not by how long it is worded.

An answer you didn't like is not lost when you ask again: **Retry** and editing your question keep
the earlier answer — with everything that followed it — as a version, and the ‹ 2/3 › arrows under
the message switch between them.

Any earlier answer, even one from the very start of a long chat, can be answered point by point:
select a passage, press **Reply**, and the model gets exactly those words, marked as the part you
are responding to.

What you already know, you can teach it once. **Settings → Instructions** holds your own notes on
particular topics: a name, a few trigger words and the text itself. The chat model sees only the
names and trigger words, and when a message touches one of those topics it opens the instruction
and follows it before answering; the reply is marked with the instruction it used. Instructions are
plain Markdown files — they can be switched off, exported and imported.

It also works when you are not asking. **Scheduled tasks** let the agent check the machine daily,
weekly or at startup — strictly read-only — and leave a report as a chat, with a notification when
something needs attention. The **PC health** panel shows disks, memory, protection, recent errors
and pending updates at a glance. An action you liked can be saved as a **recipe** and run again
without a model. For bigger jobs the agent can first send a **plan** for you to approve, a chat can
work on **another computer** over PowerShell Remoting, and tools of **MCP servers** can be plugged
in next to the built-in ones.

Chats stay manageable when there are hundreds of them: folders, tags and an archive, free search
through the text of every chat, export to Markdown, HTML or PDF, per-chat settings and templates,
a draft kept per chat, voice input, and the cost of the whole chat next to an estimate for the
next answer.

---

## What it looks like

<div align="center">

<img src="Amarin%20Admin%20AI/Assets/Guide/app-02-confirm.png" width="780" alt="Confirmation request">

<sup>The agent reports on every step and stops before anything irreversible: in plain words —
what is about to happen, under the arrow — the exact command.</sup>

<img src="Amarin%20Admin%20AI/Assets/Guide/app-03-models.png" width="780" alt="Choosing a model">

<sup>The model and the reasoning effort sit in the row under the input field, next to the account
balances and how full the context is. The price of every answer is written above it, so the
money never runs out mid-task unnoticed.</sup>

</div>

---

## Installation

**1. Download the program.** The [Releases](https://github.com/Qvisono/Amarin-Admin-AI/releases/latest)
page holds a single file, `Amarin-Admin-AI-v<version>-win-x64.exe`, and `SHA256SUMS` with its
checksum. No need to install .NET — it is inside.

**2. Give the program a key.** A Venice key, an OpenRouter key or both will do — paste them into
**Settings → Key & Info**. The key is stored on disk encrypted with Windows' own means: only your
user account can read it. You can keep as many keys as you like, name them your way and assign
each task its own; next to them are the account balances and a chart of spending by day.

If you'd rather not hand the key over for safekeeping, set an environment variable instead: the
program reads it at startup and never writes it anywhere.

```powershell
[Environment]::SetEnvironmentVariable('VENICE_API_KEY', 'your-key-here', 'User')
[Environment]::SetEnvironmentVariable('OPENROUTER_API_KEY', 'your-key-here', 'User')
```

The variables are read once at startup, so restart the program after running this command. A key
pasted on the Key & Info page works right away.

**3. Launch it and write something.** An answer is coming — everything is in place.

> Where to get the key itself, what it costs and how to cap your spending — the program has its
> own guide with screenshots: **Settings → Info**, five steps from sign-up to the first answer.
> The program finds new versions on GitHub by itself and installs them — only after checking the
> SHA-256 checksum, and, if your copy is signed, the publisher's signature. The update question
> shows the release notes first; the previous version is kept, and one button in
> **Settings → Updates** brings it back. Pre-releases come only if you turn on the beta channel.

---

## Safety

The model works with a live system, so it has limits — and they are on by default.

**Anything dangerous is asked about.** Writing to the registry, managing services, installing and
removing software, firewall rules, disk cleanup — only with explicit consent and a description of
what exactly will change. A refusal is an ordinary answer for the model: it sees it and looks for
another way. The chat's own tools pass the same gate as the agent's: only a new file in Downloads
or on the Desktop is written without a question, and nothing may write into the program's own data
folder.

**You choose how much it may do.** **Settings → Security** offers four access modes — normal, ask
about everything, read only, and approve everything automatically — and switches individual tools
off; a switched-off tool is not even shown to the model. A confirmation can allow a tool until the
end of the reply or for the whole chat.

**You can see it coming.** For the agent tiers you choose, the agent first explores read-only and
sends a plan to approve; only the approved steps run without another question. Where a script's
commands support it, the confirmation shows a `-WhatIf` dry run of what will change. **Stop** ends
running scripts and programs at once.

**PowerShell is read, not guessed.** A script is analysed by its syntax tree, and only reading runs
without a question; the confirmation lists what the script is going to change.

**A second model checks the intent.** A list of dangerous cmdlets catches what a command touches,
but not why it was written: a script that collects passwords and sends them out contains not a
single suspicious cmdlet. That is why every agent round is read by a separate guard before it
runs.

**Changes can be rolled back.** Before an edit, the state of services, scheduled tasks, startup
entries and the affected registry keys is captured, value by value. A rollback returns what was
changed and removes what was added, and shows you what it will do before it does it. Where there is
no rollback (firewall rules, Windows features), the program says so honestly in the request itself.

**Everything is on record.** Every call that changes the system and every refusal lands in an audit
log — with the chat, the arguments (secrets removed), who allowed it and what the guard said. The
log outlives deleted chats and exports to CSV and JSON.

**The limits are hard.** Deleting files through PowerShell and the file tool is forbidden.
Downloads come only from allow-listed domains. The current user can't be disabled and the last
administrator can't be removed. Critical Windows processes and services can't be stopped. Browser
password stores, SSH keys, password vaults and the Windows credential stores are never read.
BitLocker keys and passwords are never handed out.

**Money has limits too.** Spending limits per day and month — for the profile or a single key — and
a cap per reply: reaching one asks before going on. Scheduled runs have their own cap and never ask.

**The key goes nowhere.** On disk it is encrypted with Windows' own means — or it stays in an
environment variable altogether, if that's what you chose. It is not in `appsettings.json`, it
never lands in a conversation, it is cut out of crash reports, and it is sent only to the
provider it belongs to.

---

## Where your data lives

Everything is in `%APPDATA%\Amarin Admin AI`: conversations (attachments sit in a folder next to
each chat), settings, drafts, recipes and scheduled tasks, interface translations, and your
instructions (`instructions\*.md`, one file each). The program has neither a cloud nor accounts. Several people can share one
computer: each profile has its own chats and settings, and a profile can be locked with a password.

Conversations can be stored encrypted with Windows' own means (**Settings → Security**): then only
your Windows account can read them. A profile with a password can lock itself after a few idle
minutes, or on request.

Data is exported as a single archive and imported back, optionally protected with a password; a
single chat — as JSON or as a string you can forward. **Delete all data** wipes the current profile
and starts it from a clean slate.

Backups can be made automatically, daily or weekly, into a folder of your choice (keys are not
included, and the backups are not encrypted). Old chats can be archived or deleted after a number
of days, and the data page shows what takes space and cleans up logs, old snapshots and exports.
Settings, profiles and keys keep a spare copy: a damaged file is set aside and the last good copy
takes its place.

---

## Working with Windows

A tray icon shows whether the program is answering or waiting for you, and the window can close or
minimise into it. A global shortcut (Win+Shift+A by default) shows or hides the window from
anywhere; two more start a chat with the clipboard text or a screenshot. The program can start with
Windows straight into the tray, adds **Ask Amarin** to Explorer's menu for files and folders, and
keeps the latest chats in its jump list. Notifications come as the program's own card or as
Windows notifications — your choice.

---

## Appearance

Some forty palettes, dark and light, each shown as a card with a preview. On top of the palette —
your own accent colour and a background behind the interface: a moving gradient or your own
picture, over which the panels turn translucent. Interface scale from 80 to 250 %, bundled fonts,
chat column width, grain over the window.

The interface comes in Russian and English; the program translates itself into any other language
— with one button, by a model, and from then on it lives next to the others. A machine translation
can be corrected line by line, and your corrections survive the next translation.

Screen readers get a name for every button and switch, the keyboard focus is drawn in the accent
colour, Windows high contrast is followed automatically, and the chat text size and code font are
adjustable.

---

## Building from source

You need the .NET 10 SDK and Windows.

```powershell
dotnet build "Amarin Admin AI/Amarin Admin AI.csproj"
```

```powershell
dotnet test Amarin.AdminAI.Tests/Amarin.AdminAI.Tests.csproj
```

There are more than three thousand tests — including those that bring up real WPF and
check the window, the chat markup and the order in which popups close, without showing anything on
screen.

The release build is a single self-contained file:

```powershell
dotnet publish "Amarin Admin AI/Amarin Admin AI.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release/publish
```

Check the tools without a single API call (no key needed):

```powershell
& ".\Amarin Admin AI.exe" --smoke-tools
```

`--smoke-report <file>` writes the same run as a Markdown table into a file instead of a console
window — that is how CI runs it; tools whose Windows component is missing are marked `SKIP`.

Found a problem? **Report a problem** on the Info page (or in the crash window) opens a GitHub
issue with the report filled in and your profile path and user name removed.

---

## License

[GNU General Public License v3.0](LICENSE.txt). Use, modify and distribute it — provided that
derivative works stay under the same license. The program comes "as is", without warranty.
