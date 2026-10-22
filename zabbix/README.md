# Zabbix monitoring for MacroscopArchiveTool

After every archive scan (`mat.exe scan`, the scheduled scan, or «Сделать отчёт» in the window) the tool writes
`C:\ProgramData\MacroscopArchiveTool\metrics.json`. The Zabbix agent reads that file; the template turns it into items,
disk discovery and triggers. Works with Zabbix agent and agent 2, Zabbix server 7.0+.

## Setup on each Macroscop server

1. Daily scan (metrics are as fresh as the last scan):
   `mat.exe schedule --day DAILY --time 05:30`
2. Agent: copy [`macroscop.conf`](macroscop.conf) into the agent's include folder
   (`zabbix_agentd.d` for agent, `zabbix_agent2.d` for agent 2 — see the `Include=` line of the agent config)
   or add its `UserParameter` line to the config, then restart the agent service.
   Check: `zabbix_agentd.exe -t macroscop.metrics` (agent) / `zabbix_agent2.exe -t macroscop.metrics` (agent 2).

## Setup in Zabbix

1. *Data collection → Templates → Import* → [`macroscop_archive_template.yaml`](macroscop_archive_template.yaml).
2. Link **Macroscop Archive by MacroscopArchiveTool** to the hosts of the Macroscop servers.
3. With a weekly instead of daily scan, set the host macro `{$MACROSCOP.SCAN.MAXAGE}` to `8d`.

## What is monitored

| Item | Trigger |
|---|---|
| Last scan time | scan older than `{$MACROSCOP.SCAN.MAXAGE}` (2 days) |
| Video in archive, written per day, max depth | — |
| Free space on active disks, days until full | fewer than `{$MACROSCOP.HEADROOM.MIN}` (14) days |
| Cameras over retention | more than 0 (info) |
| Heavy cameras, scan warnings | — |
| Lost cameras (list) | — (shown as operational data of the triggers below) |
| Cameras without any recording within retention | more than 0: removed/replaced camera still uses a licence |
| Cameras that stopped recording | more than 0 |
| Cameras switched off in Macroscop | more than 0 (info) |
| Removed cameras with archive left | — |
| Per disk (discovery): used %, free, video, last recording, empties-by date | archive disk received no recordings during the last day |
