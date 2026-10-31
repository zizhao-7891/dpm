# dpm

`dpm` is a small C# command-line front end for Debian `.deb` repositories. It
downloads repository indexes and package archives itself using .NET HTTP APIs;
`dpkg` performs package installation/removal and integrity checks. Repository
metadata is authenticated with `gpgv`, and downloaded indexes and packages are
checked against SHA-256 hashes from the signed Release metadata.

## Build and install

Build a self-contained Debian package for `arm64` (default), `amd64`, or
`armhf`:

```sh
./build-deb.sh
sudo dpkg -i dist/dpm_0.1.43_arm64.deb
```

The build requires the .NET 8 SDK and `dpkg-deb`. Set `ARCH` to select a
supported architecture or `VERSION` to customize the package:

```sh
ARCH=amd64 VERSION=0.2.0 ./build-deb.sh
ARCH=armhf VERSION=0.2.0 ./build-deb.sh
```

The package includes the .NET runtime and an optional Tsinghua mirror example
at `/usr/share/doc/dpm/examples/tuna.list`; it does not enable any repository
source by default. Configure your own `.list` file in
`/etc/dpm/sources.list.d/` before running repository operations. At runtime,
`dpkg`, `gpgv`, `xz-utils`, and `gzip` are Debian package dependencies. Every
invocation requires effective UID 0. `dpm` never invokes `sudo`, `su`, `pkexec`,
or PolicyKit and does not attempt to elevate privileges.
Operational commands take an exclusive `/run/lock/dpm.lock` lock so concurrent
dpm processes cannot race on package indexes, caches, or dpkg operations. The
kernel releases the lock automatically when the process exits.

## Sources

`dpm` reads standard Debian `.list` files from `/etc/dpm/sources.list.d/` and
does not select a mirror on your behalf. The optional Tsinghua example is
available at `/usr/share/doc/dpm/examples/tuna.list`; copy and edit it to
configure that mirror, or create a file with your preferred mirror. Repository
files use the same `deb URI suite component...` format as APT:

```text
deb https://mirrors.tuna.tsinghua.edu.cn/debian/ trixie main contrib non-free non-free-firmware
deb https://mirrors.tuna.tsinghua.edu.cn/debian/ trixie-updates main contrib non-free non-free-firmware
deb https://mirrors.tuna.tsinghua.edu.cn/debian-security/ trixie-security main contrib non-free non-free-firmware
```

Traditional `.list` entries beginning with `deb` are supported, including flat
repositories using `./` as the suite, for example:

```text
deb https://d.spark-app.store/aarch64-store ./
```

Both clear-signed `InRelease` and detached `Release.gpg` metadata are verified;
unsigned repositories are rejected. `.sources` files and `deb-src` entries are
not supported. Binary `.gpg` signing keyrings must already be available in
`/etc/apt/trusted.gpg`, `/etc/apt/trusted.gpg.d/`, or `/usr/share/keyrings/`.
Run `dpm check-sources` to assess configured sources before installing. It
verifies signed Release metadata, checks the configured suite against the
repository's Suite/Codename, and confirms the current architecture and requested
components have signed package indexes. Flat repositories are checked for a
signed package index.

Indexes are stored in `/var/cache/dpm/list`; verified `.deb` files are stored
in `/var/cache/dpm/deb`.

## Commands

```text
dpm update
dpm up                    # alias for update
dpm check-sources         # checks configured sources against signed Release metadata
dpm install PACKAGE [PACKAGE ...] # refreshes indexes, then installs all requested packages
dpm install ./FILE.deb    # installs a local package and resolves dependencies
dpm fix                   # repairs missing dependencies and configures pending packages
dpm -f                    # alias for fix
dpm -fix                  # alias for fix
dpm -f install PACKAGE... # repairs dependencies and installs requested packages
dpm reinstall PACKAGE     # reinstalls the installed version from the repositories
dpm remove PACKAGE [PACKAGE ...]
dpm uninstall PACKAGE [PACKAGE ...] # alias for remove
dpm purge PACKAGE [PACKAGE ...] # removes packages and their configuration files
dpm upgrade [PACKAGE]
dpm upgrade-dpm           # upgrades dpm from the latest GitHub release
dpm check                 # dpkg --audit
dpm check-filesystem      # dpkg --verify
dpm download PACKAGE
dpm clear                 # clear downloaded .deb cache (keeps indexes)
dpm search TERM
```

`install`, `reinstall`, and `upgrade` resolve `Pre-Depends`, `Depends`, and
`Recommends` recursively, including version constraints, alternative
dependencies, and virtual packages declared by `Provides`. This follows apt's
default of installing recommended packages; `Suggests` are not installed
automatically. Already-installed satisfying packages are reused; missing
dependencies are selected from the signed package indexes, included in the
confirmation list, downloaded, and passed to `dpkg` together with the requested
packages.
Already-installed metapackages encountered in this dependency tree are also
expanded, so rerunning an install can fill in missing recommended packages
without reinstalling the metapackage itself.
Packages are ordered so selected `Pre-Depends` are installed before their
dependents. Package indexes are parsed once per command, and dependency lookups
use package-name and virtual-provider indexes to keep large package plans
responsive.

`fix`, `-f`, `-fix`, and `--fix-broken` scan installed packages for unsatisfied
`Pre-Depends` and `Depends`, select available providers from the configured
repositories, and include them in the normal reviewed installation plan. They
also run `dpkg --configure --pending` when dpkg reports packages awaiting
configuration. Unavailable or conflicting repairs are reported rather than
bypassing dependency safety checks.

`upgrade-dpm` compares the installed version with the latest stable GitHub
release and exits only when they match. Otherwise, GitHub's release is treated
as authoritative: dpm displays the publisher and release notes and asks before
downloading. After confirmation it verifies the package SHA256 digest and
Debian metadata, then installs it with dpkg. If the installed version is newer,
dpkg is explicitly allowed to downgrade to the GitHub release. When the
effective `LC_ALL`, `LC_MESSAGES`, or `LANG` locale is `zh_CN.UTF-8`, the GitHub
download URL is fetched through `https://gh-proxy.org/`.

`install` also accepts a local `.deb` path. Its package metadata is read with
`dpkg-deb`; the package is included in the normal confirmation and installation
plan, while any missing dependencies are resolved from the configured indexes.
The local package architecture must match the system or be `all`.

`reinstall` requires the package to be installed and its exact installed version
to be available in the configured repositories. It reinstalls that version
rather than silently upgrading it.

For a full `upgrade`, dpm first tries to plan every available package update
together. If the combined plan has dependency conflicts, it retries in smaller
batches and holds back only candidates that still cannot be resolved alongside
the compatible updates. Held-back packages and the dependency error are shown
before confirmation.

When removing or purging a package, `dpm` also detects unreachable automatic
packages and offers to remove them in the same transaction. Only dependencies
that dpm itself installed are eligible; they are recorded in
`/var/lib/dpm/auto-installed`. Other packages, including packages managed by
APT, are never classified or removed as dpm orphans. Dependencies still
reachable from any other installed package, including through `Recommends`, are
kept. Before removal, dpm checks installed `Depends` and `Pre-Depends` and
refuses to remove a package if any remaining package would be left with an
unsatisfied dependency. Orphans are shown separately in the confirmation list.

Before installing, reinstalling, upgrading, or removing packages, `dpm` lists
the package names and versions that will change and asks for confirmation.
Enter `y` (or `是`) to continue; all other answers cancel. Non-interactive
input defaults to cancellation. Use `-y`/`--yes` to automatically confirm or
`-n`/`--no` to automatically cancel confirmation prompts; either option can
appear before or after the command. They cannot be combined.

During install, reinstall, upgrade, and removal, the progress display tracks
only packages in the current transaction, based on dpkg's machine-readable
status events. Trigger processing and unrelated packages do not advance it.
The terminal display updates in place; redirected output emits only a final
summary. Status events are read from a dedicated descriptor so maintainer
script output continues to use the terminal normally. If an install fails,
dpm retries `dpkg --configure --pending` once and verifies that every planned
package reached its requested version before treating the transaction as
recovered.
dpkg does not report a byte-level percentage for unpacking/configuring a single
package, so the bar can remain still while that package is being processed; it
does not invent intermediate progress.

During `update`, the index progress bar advances only after each configured
component index has been downloaded, signature/hash-verified, and installed in
the local cache. A slow index download therefore does not advance the bar
prematurely. On every update, the signed Release file is checked first; if its
index path and SHA256 match the cached index metadata, the large package index
is not downloaded again.

Package downloads show the per-package transfer progress plus an overall
`总共[已完成/总数]` progress line. After each package finishes, its progress
line is cleared and reused for the next package. The overall bar uses signed
package sizes, with aggregate measured speed and estimated remaining time; if
package sizes are unavailable, it falls back to completed-package progress
rather than inventing a byte percentage. The per-package line shows
received-byte percentage and estimated remaining seconds.

`dpm clear` shows the number and total size of cached `.deb` archives and asks
for confirmation before deleting them. Package indexes are kept; `dpm clear
-y` confirms the cleanup and `dpm clear -n` cancels it.
