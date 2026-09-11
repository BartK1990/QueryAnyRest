# Query Any REST

A Blazor Server app that runs saved REST queries, reshapes the answers with a bit of
JavaScript, and drops timestamped files where another system can pick them up. It runs
as a Windows service, so the exports keep happening whether or not anyone is logged in.

Built for .NET 8.

## What is where

```
QueryAnyRest.csproj          net8.0 web project, two NuGet references
Program.cs                   host setup, DI, Kestrel, Windows service lifetime
appsettings.json             port and data directory
Models/                      configuration shape and run state
Services/                    the actual work
Components/                  Blazor UI
deploy/install-service.ps1   publish, register, start
deploy/configurations.sample.json   a worked OneView example
```

The three services worth knowing:

- `OAuthTokenService` — password grant, token cache, refresh-token rotation.
- `JobRunner` — one execution end to end. Both the Run button and the scheduler use it,
  so a manual run behaves exactly like a scheduled one.
- `SchedulerService` — a `BackgroundService` that polls every 20 seconds and runs what is due.

## Running it

```powershell
dotnet run --project QueryAnyRest.csproj
```

Then open <http://localhost:5290>. In development, data goes to the folder named in
`appsettings.json`; change `QueryAnyRest:DataDirectory` if `C:\ProgramData` is awkward
while you are experimenting.

## Installing as a service

From an elevated PowerShell prompt:

```powershell
cd deploy
.\install-service.ps1 -Publish -Install -Start
```

That publishes to `C:\Program Files\QueryAnyRest`, creates the `QueryAnyRest` service under
`NT AUTHORITY\NetworkService`, grants that account write access to `C:\ProgramData\QueryAnyRest`,
reserves the URL with http.sys, registers the event log source, sets restart-on-failure, and
opens the port to the local subnet only.

To push a new build over an installed service:

```powershell
.\install-service.ps1 -Publish -Restart
```

To remove it: `.\install-service.ps1 -Uninstall` — files and data are left alone.

Two things that bite when hosting ASP.NET Core as a service, both handled in the code:

- The working directory is `C:\Windows\System32`, so `ContentRootPath` is pinned to
  `AppContext.BaseDirectory` in `Program.cs`.
- A non-SYSTEM service account cannot bind a port without a URL reservation, which is why
  the script runs `netsh http add urlacl`.

## The configuration file

Everything lives in `C:\ProgramData\QueryAnyRest\configurations.json`:

```json
{
  "Version": 1,
  "Configurations": [ { "Id": "...", "Name": "...", "Auth": {}, "Request": {}, "Transform": {}, "Output": {}, "Schedule": {} } ]
}
```

Three more files sit beside it:

| File | Holds |
| --- | --- |
| `tokens.json` | the live access and refresh tokens, one entry per configuration |
| `jobstate.json` | last run, last error, next due time |
| `runs.json` | the recent run history shown in the UI |

Writes go to a temp file and are then moved into place, so a power cut cannot leave half a
configuration behind. If a file is ever unreadable it is renamed to `*.broken-<timestamp>`
rather than being silently replaced.

### How credentials are stored

Passwords, client secrets and the live tokens are encrypted at rest. They cannot be hashed,
because the app has to replay them at the token endpoint, so the aim is to make the files
useless on their own.

- Values are encrypted through ASP.NET Core Data Protection, with a key ring in
  `C:\ProgramData\QueryAnyRest\keys` that is itself protected by DPAPI. A copied
  `configurations.json` is worthless without the key ring, and the key ring only works on
  the machine that created it.
- Encryption is per property: `[JsonConverter(typeof(SecretStringConverter))]` marks a field
  as secret, and it is encrypted on the way to disk and decrypted on the way back. Everything
  in memory stays plaintext, so no other code changes.
- Encrypted values carry an `enc:v1:` prefix. A file written before this was switched on
  still loads, and gets encrypted on the next save.
- The installer removes the inherited ProgramData permissions from the data folder and grants
  SYSTEM, Administrators and the service account only, then prints the resulting ACL. This is
  what actually protects the key ring, so check that output.
- The editor never sends a stored secret to the browser. Secret boxes show whether a value
  exists and are write-only: leave one blank to keep what is stored.

If a secret cannot be decrypted — different machine, restored backup, deleted key ring — it
reads as empty and shows as "Not set" in the editor, and the error goes to the event log.
Re-enter it; there is no recovery path, by design.

**Two things this does not solve.** Administrators on the box can still recover every
credential, so treat local admin as equivalent to knowing the passwords. And the key ring is
machine-bound, so a bare-metal restore to different hardware means re-entering credentials
rather than restoring them.

**Where to go further,** roughly in order of effort:

1. Switch `ProtectKeysWithDpapi(protectToLocalMachine: false)` in `Program.cs` to bind the key
   ring to the service account's profile rather than the machine. Tighter, but it breaks if
   the account changes or its profile does not load.
2. Replace it with `ProtectKeysWithCertificate`, using a non-exportable key in the machine
   store. That survives account changes, can be backed by a TPM, and gives you something to
   audit and rotate.
3. Drop the stored password entirely. Once a refresh token with `offline_access` is held, many
   servers never need the password again — the standing secret becomes a rotating token that
   can be revoked server-side. Re-authenticating then means typing the password once in the UI.
4. Move the credentials to a real secret store — Windows Credential Manager, CyberArk, Vault —
   and keep only a reference in `configurations.json`.

### Importing your settings block

The Configurations page takes the appsettings shape you already have. Paste this and it
becomes a configuration:

```json
"OneView": {
  "AuthUrl": "http://10.64.2.142/oneview4/auth",
  "ApiUrl": "http://10.64.2.142/oneview4/api/nbi",
  "Username": "Remote",
  "Password": "Remote",
  "ClientId": "oneview.public",
  "ClientSecret": "1m7s68ogxdt3znp9ovdm",
  "Scope": "public.webapi offline_access"
}
```

Several named blocks at once work too. `AuthUrl` switches the configuration to the OAuth 2.0
password grant; `ApiUrl` becomes the endpoint. Refresh-token use is turned on automatically
when `offline_access` is in the scope, because that is normally what makes the server issue
a refresh token at all.

## How the OAuth handler behaves

1. First call posts `grant_type=password` with the user name, password, client id, client
   secret and scope. `client_id`/`client_secret` go in the form body by default, or in an
   HTTP Basic header if you switch the placement.
2. The access token is cached with its `expires_in`. Later calls reuse it until it is within
   the refresh skew (60 seconds by default) of expiry.
3. At that point it posts `grant_type=refresh_token`. If the server returns a new refresh
   token — which rotating servers do, invalidating the old one immediately — the new value
   is written to `tokens.json` before anything else uses it.
4. A rejected refresh falls back to the password grant once, rather than failing the run.
5. Refreshes are serialised per configuration. A rotated refresh token is single-use, so two
   parallel refreshes would invalidate each other.

The token panel in the editor shows which grant produced the current token, when it expires,
and how many times the refresh token has rotated — useful for proving rotation actually works.

## Transforms

Without a script: JSON is re-indented, CSV flattens the first array it finds in the payload
(`items`, `data`, `results`, `value` and `rows` are checked first), nested objects become
dotted column names, and Raw passes the body through untouched.

With a script, your JavaScript runs in [Jint](https://github.com/sebastienros/jint) with a
time limit and a memory budget, and no access to the file system, the network or the host.
Jint counts every byte allocated during the run rather than what is alive, so the budget is
256 MB plus 64 bytes per character of the response; a flat cap would reject ordinary scripts
on multi-megabyte responses.
It receives:

| Name | Is |
| --- | --- |
| `response` | the raw body text |
| `json` | the parsed body, or `null` when the body is not JSON |
| `log(x)` | writes a line to the run log |

Return a string, or an object that gets stringified. The script is stored in the
configuration alongside everything else, so it travels with the endpoint definition.

CSV files are written with a UTF-8 BOM so Excel opens them with the right encoding.

## Scheduling

Every N minutes, hourly at a chosen minute, daily, weekly on a chosen day, or monthly on a
chosen day. Times are machine local, so an 06:00 daily export stays at 06:00 across a DST
change. A day-of-month past the end of a short month falls back to that month's last day.

If the machine was off when a run was due, "catch up a missed run" decides whether it fires
once at start-up or waits for the next slot.

Each run writes `<name>-<yyyyMMdd-HHmmss>.<ext>` into the output folder. The timestamp format
is configurable, and a second run inside the same second gets a `_2` suffix rather than
overwriting the first.

## Things worth knowing

- The scheduler runs jobs one after another, not in parallel. For a handful of exports that
  is the right trade; if you end up with dozens of minute-level jobs, that loop is the first
  thing to revisit.
- Nothing authenticates the web UI itself, and that is now the weakest link. Anyone who can
  reach the page can point a configuration at a server they control and have the service send
  the credentials there — encryption at rest does not help against that. It is bound to
  localhost in `appsettings.json` and the firewall rule only opens the local subnet. Before
  exposing it any wider, put Windows authentication or a reverse proxy in front of it.
- Give the API account itself the least privilege that does the job. A read-only NBI user
  limits what a leaked credential is worth, which is usually cheaper than hardening storage.
- `AllowInvalidCertificates` disables certificate validation for one configuration only. It
  exists for appliances with self-signed certificates and should stay off otherwise.
