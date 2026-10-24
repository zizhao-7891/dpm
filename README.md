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
sudo dpkg -i dist/dpm_0.1.22_arm64.deb
```

The build requires the .NET 8 SDK and `dpkg-deb`. Set `ARCH` to select a
supported architecture or `VERSION` to customize the package:

```sh
ARCH=amd64 VERSION=0.2.0 ./build-deb.sh
ARCH=armhf VERSION=0.2.0 ./build-deb.sh
```

The package includes the .NET runtime and installs the Tsinghua mirror source
as the dpkg-managed conffile `/etc/dpm/sources.list.d/tuna.list`. At runtime,
`dpkg`, `gpgv`, `xz-utils`, and `gzip` are Debian package dependencies. Every
invocation requires effective UID 0. `dpm` never invokes `sudo`, `su`, `pkexec`,
or PolicyKit and does not attempt to elevate privileges.

## Sources

`dpm` reads standard Debian `.list` files from `/etc/dpm/sources.list.d/`.
The included `tuna.list` uses the same `deb URI suite component...` format as
APT:

```text
deb https://mirrors.tuna.tsinghua.edu.cn/debian/ trixie main contrib non-free non-free-firmware
deb https://mirrors.tuna.tsinghua.edu.cn/debian/ trixie-updates main contrib non-free non-free-firmware
deb https://mirrors.tuna.tsinghua.edu.cn/debian-security/ trixie-security main contrib non-free non-free-firmware
```

Only traditional `.list` entries beginning with `deb` are supported; `.sources`
files and `deb-src` entries are not supported. Binary `.gpg` signing keyrings
must already be available in `/etc/apt/trusted.gpg`, `/etc/apt/trusted.gpg.d/`,
or `/usr/share/keyrings/`. Unsigned repositories are rejected.

Indexes are stored in `/var/cache/dpm/list`; verified `.deb` files are stored
in `/var/cache/dpm/deb`.

## Commands

```text
dpm update
dpm up                    # alias for update
dpm install PACKAGE       # refreshes indexes before selecting/downloading the package
dpm reinstall PACKAGE     # reinstalls the installed version from the repositories
dpm remove PACKAGE
dpm uninstall PACKAGE     # alias for remove
dpm purge PACKAGE         # remove package and its configuration files
dpm upgrade [PACKAGE]
dpm check                 # dpkg --audit
dpm check-filesystem      # dpkg --verify
dpm download PACKAGE
dpm clear                 # clear downloaded .deb cache (keeps indexes)
dpm search TERM
```

`install`, `reinstall`, and `upgrade` resolve `Pre-Depends` and `Depends`,
including version constraints, alternative dependencies, and virtual packages
declared by `Provides`. Already-installed satisfying packages are reused;
missing dependencies are selected from the signed package indexes, included in
the confirmation list, downloaded, and passed to `dpkg` together with the
requested packages. `Recommends` and `Suggests` are not installed automatically.

`reinstall` requires the package to be installed and its exact installed version
to be available in the configured repositories. It reinstalls that version
rather than silently upgrading it.

When removing or purging a package, `dpm` also detects unreachable automatic
packages and offers to remove them in the same transaction. Only dependencies
that dpm itself installed are eligible; they are recorded in
`/var/lib/dpm/auto-installed`. Other packages, including packages managed by
APT, are never classified or removed as dpm orphans. Dependencies still
reachable from any other installed package are kept. Orphans are shown
separately in the confirmation list.

Before installing, reinstalling, upgrading, or removing packages, `dpm` lists
the package names and versions that will change and asks for confirmation.
Enter `y` (or `是`) to continue; all other answers cancel. Non-interactive
input defaults to cancellation. Use `-y`/`--yes` to automatically confirm or
`-n`/`--no` to automatically cancel confirmation prompts; either option can
appear before or after the command. They cannot be combined.

During install, reinstall, upgrade, and removal, the progress display is based
on dpkg's machine-readable package status events: completed packages advance
the bar.
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
