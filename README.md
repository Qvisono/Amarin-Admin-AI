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

Any earlier answer, even one from the very start of a long chat, can be answered point by point:
select a passage, press **Reply**, and the model gets exactly those words, marked as the part you
are responding to.

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
page holds a single file, `Amarin-Admin-AI-v<version>-win-x64.exe`. No need to install .NET — it is
inside.

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
> The program finds new versions on GitHub by itself and installs them.

---

## Safety

The model works with a live system, so it has limits — and they are on by default.

**Anything dangerous is asked about.** Writing to the registry, managing services, installing and
removing software, firewall rules, disk cleanup — only with explicit consent and a description of
what exactly will change. A refusal is an ordinary answer for the model: it sees it and looks for
another way.

**A second model checks the intent.** A list of dangerous cmdlets catches what a command touches,
but not why it was written: a script that collects passwords and sends them out contains not a
single suspicious cmdlet. That is why every agent round is read by a separate guard before it
runs.

**Changes can be rolled back.** Before an edit, the state of services, scheduled tasks and the
affected registry keys is captured — the agent can put it back. Where there is no rollback
(firewall rules, Windows features), the program says so honestly in the request itself.

**The limits are hard.** Deleting files through PowerShell and the file tool is forbidden.
Downloads come only from allow-listed domains. The current user can't be disabled and the last
administrator can't be removed. BitLocker keys and passwords are never handed out.

**The key goes nowhere.** On disk it is encrypted with Windows' own means — or it stays in an
environment variable altogether, if that's what you chose. It is not in `appsettings.json`, it
never lands in a conversation, it is cut out of crash reports, and it is sent only to the
provider it belongs to.

---

## Where your data lives

Everything is in `%APPDATA%\Amarin Admin AI`: conversations, settings, attachments, interface
translations. The program has neither a cloud nor accounts. Several people can share one
computer: each profile has its own chats and settings, and a profile can be locked with a password.

Data is exported as a single archive and imported back; a single chat — as JSON or as a string you
can forward. All of these are plain unencrypted files: the profile password locks entry to the
program, not the data folder.

---

## Appearance

Some forty palettes, dark and light, each shown as a card with a preview. On top of the palette —
your own accent colour and a background behind the interface: a moving gradient or your own
picture, over which the panels turn translucent. Interface scale from 80 to 250 %, bundled fonts,
chat column width, grain over the window.

The interface comes in Russian and English; the program translates itself into any other language
— with one button, by a model, and from then on it lives next to the others.

---

## Building from source

You need the .NET 10 SDK and Windows.

```powershell
dotnet build "Amarin Admin AI/Amarin Admin AI.csproj"
```

```powershell
dotnet test Amarin.AdminAI.Tests/Amarin.AdminAI.Tests.csproj
```

There are more than seventeen hundred tests, and they are green — including those that bring up
real WPF and check the window, the chat markup and the order in which popups close, without
showing anything on screen.

The release build is a single self-contained file:

```powershell
dotnet publish "Amarin Admin AI/Amarin Admin AI.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release/publish
```

Check the tools without a single API call (no key needed):

```powershell
& ".\Amarin Admin AI.exe" --smoke-tools
```

---

## License

[GNU General Public License v3.0](LICENSE.txt). Use, modify and distribute it — provided that
derivative works stay under the same license. The program comes "as is", without warranty.
