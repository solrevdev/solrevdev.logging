# AGENTS.md

Instructions for coding agents working in this repository.

The rules live in [CLAUDE.md](CLAUDE.md) beside this file, so Codex, Claude Code and any
other agent read the same ones. Read it before changing anything.

The two that matter most, repeated here because they are the ones that cause real damage
quietly:

- **This package must never grow rate limiting, throttling, a `ForwardedHeadersOptions`
  policy, or anything that keys off an address for a purpose other than a log property.**
  Those are the consuming application's decisions.
- **`ClientAddress.Describe` is for log properties only.** Its output is reshaped to be
  readable and is therefore wrong for a partition key, a cache key, an authorisation decision
  or a stored column. For every routable address the reshaped and raw forms are identical, so
  the mistake survives testing.

Build and test:

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

Tests must pass before you commit. Commits use Conventional Commits.
