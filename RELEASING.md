# Releasing StrataFx

Releases are built and published by [.github/workflows/release.yml](.github/workflows/release.yml) when a `v*` tag is
pushed. The steps below are the ones only a maintainer can do: nuget.org accounts, credentials, tags, and the
checks after a release. Steps 1–4 are needed once; steps 5–9 for every release.

Packages published to nuget.org **cannot be deleted**, only unlisted, and a version number can never be reused.
Do a dry run (step 6) before the first real release.

## One-time setup

### 1. Create a nuget.org account

1. Go to <https://www.nuget.org> and choose **Sign in**. Sign in with a Microsoft account (create one if needed)
   and pick your nuget.org **username**. Write it down: the workflow needs it (step 4).
2. Turn on two-factor authentication for the Microsoft account. nuget.org requires it to publish.

### 2. Create the `stratafx` organization

1. On nuget.org, click your username → **Organizations** → **Add new**.
2. Name: `stratafx`. Give it an e-mail address that is not already used by a nuget.org account (for example a
   project address). You become its administrator; add other maintainers as members later.
3. Publish all StrataFx packages with the organization as owner, not your personal account. The prefix reservation
   (step 9) is requested for this owner.

### 3. Set up trusted publishing (recommended) or an API key

**A. Trusted publishing (no stored key).** GitHub Actions gets a one-hour nuget.org key per run through OIDC.

1. On nuget.org, click your username → **Trusted Publishing** → add a new policy:
   - **Policy owner**: the `stratafx` organization (you must stay an active member, or the policy becomes inactive).
   - **Repository Owner**: `stratafx`
   - **Repository**: `StrataFx`
   - **Workflow File**: `release.yml` (the file name only, without `.github/workflows/`)
   - **Environment**: `nuget` (the release workflow's publish job runs in this environment)
   - **Scopes**: allow publishing new packages and new versions, limited to `StrataFx.*` if the page offers a pattern.
2. The policy may show as *temporarily active for 7 days* (usual for private repositories) until the first
   successful publish fills in GitHub's repository and owner IDs. If the 7 days pass without a publish, restart the
   window from the same page.

**B. API key (fallback, if trusted publishing isn't available to you).**

1. On nuget.org, click your username → **API Keys** → **Create**:
   - **Key name**: `StrataFx GitHub release`
   - **Package owner**: `stratafx`
   - **Scopes**: **Push** → *Push new packages and package versions* (no unlist permission)
   - **Glob pattern**: `StrataFx.*`
   - **Expiration**: 365 days (put a reminder in your calendar to renew it)
2. Copy the key; it is shown only once. Store it as `NUGET_API_KEY` (step 4). When that secret exists, the
   workflow uses it instead of trusted publishing.

### 4. Configure the GitHub repository

In <https://github.com/stratafx/StrataFx> → **Settings**:

1. **Environments** → **New environment** → `nuget`. Recommended: under *Deployment branches and tags*, allow only
   tags matching `v*`; optionally add yourself as a required reviewer so every publish waits for your approval.
2. In the `nuget` environment, **Add environment secret**:
   - `NUGET_USER`: your nuget.org **username** (profile name) from step 1, not your e-mail address and not the
     organization name. Needed for trusted publishing.
   - `NUGET_API_KEY`: only if you chose option B.
3. Nothing else is needed: the GitHub release job uses the built-in `GITHUB_TOKEN`.

## Every release

### 5. Prepare the release commit

1. `Directory.Build.props`: `<VersionPrefix>` must be the version you release (`0.1.0`). The release workflow fails
   if the tag and `VersionPrefix` disagree (a prerelease tag such as `v0.2.0-preview.1` must match `0.2.0`).
2. `CHANGELOG.md`: change `## [0.1.0] - Unreleased` to today's date, e.g. `## [0.1.0] - 2026-11-10`. The text of
   this section becomes the GitHub release notes.
3. Commit, push to `main`, and wait for CI (build, test, pack) to pass.

### 6. Dry run (recommended)

On GitHub: **Actions** → **Release** → **Run workflow**, branch `main`, version `0.1.0`, **publish unticked**. It
builds, tests and packs exactly as a release does. Download the `packages` artifact and check it like step 8
(README, icon, dependencies), without anything reaching nuget.org.

### 7. Tag and push

```bash
git checkout main
git pull
git tag -a v0.1.0 -m "StrataFx 0.1.0"
git push origin v0.1.0
```

Watch **Actions** → **Release**. If you set up a required reviewer, approve the `nuget` deployment. The workflow
pushes the five packages (and the four libraries' symbol packages) with `--skip-duplicate` (so a re-run doesn't fail on packages
that are already there), then creates the GitHub release `v0.1.0` with the packages attached.

If the publish job fails on credentials, fix the policy or secret and use **Re-run failed jobs**; don't create a
new tag.

### 8. Check the packages

1. <https://www.nuget.org/profiles/stratafx> lists StrataFx.Core, StrataFx.EFCore, StrataFx.Blazor,
   StrataFx.Identity and StrataFx.Templates. New packages are validated and indexed first, which can take from a
   few minutes to about an hour before they appear in search and restore.
2. On each package page: the README renders, the icon shows, the license is MIT and the dependencies are as
   expected (StrataFx.* 0.1.0, EF Core and MudBlazor). Open a library package in NuGet Package Explorer
   (<https://nuget.info/packages/StrataFx.Core/0.1.0>): its health panel should show Source Link, deterministic
   build and symbols as valid.
3. In an empty folder, outside the repository:

   ```bash
   dotnet new install StrataFx.Templates
   dotnet new stratafx -n ReleaseCheck
   cd ReleaseCheck
   dotnet user-secrets set "AdminUser:UserName" "admin"
   dotnet user-secrets set "AdminUser:Password" "<a password>"
   dotnet run
   ```

   Sign in, open Companies and Contacts, then try `dotnet new stratafx -n ReleaseCheckNoAuth --auth false`.
4. The GitHub release exists with the changelog text and the packages attached.

### 9. Reserve the `StrataFx.*` ID prefix (after the first release)

nuget.org reserves prefixes on request, and the request is much stronger once packages under the prefix are
published by the requesting owner, so do this after step 8:

1. Send an e-mail to **account@nuget.org** with:
   - the owner display name on nuget.org: `stratafx` (the organization), and
   - the prefix you are requesting: `StrataFx.*`, and that it should not be public (only the owner may push).
   - Helpful context for the criteria nuget.org applies: the prefix clearly identifies the owner (the StrataFx
     project, <https://github.com/stratafx/StrataFx>, and the published StrataFx.* packages); it isn't a common or
     generic word; and without a reservation others could publish look-alike StrataFx.* packages.
2. nuget.org may ask further questions to confirm who you are, then tells you whether the reservation was accepted
   (with the reasons if it was not). No review time is published.
3. Once reserved, StrataFx.* packages from `stratafx` show the reserved-prefix check mark on nuget.org and in
   Visual Studio, and nuget.org rejects StrataFx.* packages pushed by anyone else. Packages that others already
   own under the prefix stay as they are, without the mark.

This follows nuget.org's [ID prefix reservation](https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation)
page, which was last updated in 2022. Check it for changes before you send the request.

### 10. After the release

1. Bump `<VersionPrefix>` in `Directory.Build.props` to the next version (e.g. `0.2.0`).
2. Add a new `## [0.2.0] - Unreleased` section at the top of `CHANGELOG.md`.
3. Commit and push.
