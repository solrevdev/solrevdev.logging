# Maintaining solrevdev.logging

Release process and repository decisions. Consumer documentation is in
[README.md](README.md). Rules for changing the code are in [AGENTS.md](AGENTS.md).

## Publishing

Releases go to nuget.org from [.github/workflows/publish.yml](.github/workflows/publish.yml)
using NuGet trusted publishing. There is no long-lived API key in this repository and none
should be added.

The workflow requests a short-lived GitHub OIDC token, nuget.org validates it against a policy
registered on their site, and returns a temporary API key valid for one hour and one push. That
is why the `Log in to NuGet` step sits immediately before the push rather than at the top of
the job, and why the publish job carries `id-token: write` alongside the `contents: write` it
needs for the tag and the release. The pull-request `validate` job does not publish and does
not get `id-token: write`.

### One-time nuget.org setup

This cannot be scripted. On nuget.org, click the username, choose Trusted Publishing, and add a
policy:

| Field | Value |
| --- | --- |
| Package owner | `solrevdev` |
| Publisher | GitHub Actions |
| Repository owner | `solrevdev` |
| Repository | `solrevdev.logging` |
| Workflow file | `publish.yml`, the file name only, not the `.github/workflows/` path |
| Environment | leave empty; this workflow uses no GitHub environment |
| Package glob | `solrevdev.Logging.*` |

Scope it to allow pushing new packages, not only new versions, if the package does not exist on
nuget.org yet. A policy scoped to versions only cannot create a package. It can be narrowed
after the first release.

## Cutting a release

The `<Version>` in the csproj is the release number. A push to `master` publishes it as it
stands if it has no tag yet. Once that version is tagged, a later push bumps the patch, and a
`workflow_dispatch` chooses minor or major.

The job runs in this order:

1. Work out the version and, if the current one is already tagged, bump it.
2. Check the release state. If the target tag exists this is a retry. If not, refuse when
   `origin/master` has already moved.
3. When it is a retry, check out the tag so the pack uses the source the tag names.
4. Restore, build, test, pack, and check the expected `.nupkg` exists.
5. Commit the version bump, tag, and push branch and tag atomically.
6. Log in to NuGet and push the package.
7. Create the GitHub Release.

### Why the tag is pushed before the package

nuget.org and Git are separate systems and nothing here makes them atomic. A published version
cannot be recalled, and a published version whose source nobody can identify is the outcome
worth avoiding. So the order is chosen to decide which side is left dangling when a release
does not complete.

Steps 5 and 6 used to be the other way round. That left two ways to get a package nobody can
tie to a commit. If `master` advanced during the build, the package uploaded and then the
atomic push failed, because the branch was no longer a fast-forward. If a later run computed a
version that already had a tag, `--skip-duplicate` passed over the existing package while the
job packed and tagged a different tree.

With the tag first, a failure between the two leaves a tag with no package. Re-running checks
out that tag, packs the same source again, and `--skip-duplicate` makes the second push
harmless. The atomic branch and tag push still fails if `master` moved, and that failure now
costs a run rather than a wrong published version.

Both transitions were reproduced locally against a bare repository, with the NuGet push
replaced by an echo. With the new order, the run where `master` moved mid-build refused at the
git push and uploaded nothing. The retry case packed the tagged tree rather than the newer
`master`.

The early `origin/master` check in step 2 is a cheap pre-flight rather than a guarantee. It
fails a doomed run in seconds instead of after a build, and `master` can still move during the
build that follows. The atomic push is what protects the release.

## Why these test packages

`xunit.v3` 3.2.2 with `xunit.runner.visualstudio` 3.1.5 and `Microsoft.NET.Test.Sdk` 18.9.0.
That is the combination the sibling applications run on the .NET 10 SDK, so a fix proved here
can be pasted into either without a framework argument. The xunit v4 packages move to
Microsoft.Testing.Platform, which is a migration in its own right and not one to make inside a
library that other applications depend on.

`TreatWarningsAsErrors` and `GenerateDocumentationFile` are both on, so a public member without
XML docs fails the build. This package exists to carry reasons between applications, and a
member shipped without its reason is the drift it was written to stop.

Two test classes swap the process-global `Serilog.Log.Logger` to capture events, so they share
one xUnit collection and never run at the same time. Collections are the unit of parallelism.
A new test class that captures Serilog output belongs in that collection too, or it will see an
empty sink at random.

## Where the code came from

| Piece | Source |
| --- | --- |
| `ClientAddress.Describe` | Two of the three applications, byte-identical in both |
| Forwarded-for reconstruction | Two of the three |
| Referer sanitising | The same two |
| `RequestLogLevel` | All three, merged |
| The two-middleware split | Two of the three, with the third's trace id |
| `LoggingContract` | New, replacing three sets of hand-typed constants |

The package is the union of the three copies rather than the smallest common part. Everything
in it was measured in one of those applications first, which is why the comments record what
was observed rather than what the code does.
