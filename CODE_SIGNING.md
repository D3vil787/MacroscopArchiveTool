# Code signing policy

Windows releases of MacroscopArchiveTool (`MacroscopArchiveTool.exe`, `mat.exe`) are going to be signed so that users
can check that a file comes from this project and has not been changed.

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).
*(Status: the SignPath Foundation asked us to re-apply once the project has more public visibility. Until then releases are
unsigned — verify downloads with the SHA-256 shown on the release page.)*

## What is signed and how

- Only files built by GitHub Actions from this public repository
  ([workflow](.github/workflows/build.yml)) are submitted for signing. Nothing built on a personal computer is ever signed.
- The build output is uploaded as a GitHub Actions artifact and sent to SignPath, which verifies its origin
  (repository, branch, workflow) before signing. The signing key is kept by SignPath in a hardware security module.
- Every signing request is approved manually by an approver (see below).
- Signed files are published only on [GitHub Releases](https://github.com/D3vil787/MacroscopArchiveTool/releases).

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [D3vil787](https://github.com/D3vil787) |
| Approvers | [D3vil787](https://github.com/D3vil787) |

Changes from other people are accepted only through pull requests reviewed by a committer.
All team members use multi-factor authentication for GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it. In particular:

- **Update check.** When the window opens, the program asks `api.github.com` whether a newer release of this project exists.
  No data about the computer, its cameras or archive is sent. Disable it with `"checkUpdates": false` in `mat.json`.
- **Several servers.** Only when the user adds other servers to the report, the program connects to those servers
  in the user's own network to scan their archive. Stored passwords are encrypted with Windows DPAPI and never leave the computer.

Reports are written only to the local `reports` folder.

## Verifying a file

Right-click the file → *Properties* → *Digital Signatures*, or in PowerShell:

```powershell
Get-AuthenticodeSignature .\MacroscopArchiveTool.exe | Format-List Status, SignerCertificate
```

The SHA-256 of every release zip is shown on the release page on GitHub.

## Reporting a problem

If a file claims to be from this project but its signature is missing or invalid, do not run it and
[open an issue](https://github.com/D3vil787/MacroscopArchiveTool/issues) with the file's SHA-256 and where it was downloaded.
