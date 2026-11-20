# Third-Party Notices

AuraAudio includes or links against the following third-party open-source components. Each component remains subject to its respective license terms.

## Direct Runtime Dependencies (`AuraAudio.csproj`)

| Component | Version | License | Authors / Copyright | Project URL |
| :--- | :---: | :--- | :--- | :--- |
| **BunLabs.NAudio.Flac** | `2.0.1` | `MS-PL` (Microsoft Public License) | Vivelin | https://github.com/BunLabs/NAudio.Flac |
| **Microsoft.Data.Sqlite** | `10.0.13` | `MIT` | Microsoft | https://docs.microsoft.com/dotnet/standard/data/sqlite/ |
| **NAudio.Wasapi** | `3.1.0` | `MIT` | Mark Heath | https://naudio.github.io/NAudio/ |
| **System.Drawing.Common** | `10.0.13` | `MIT` | Microsoft | https://github.com/dotnet/winforms |
| **TagLibSharp** | `2.3.0` | `LGPL-2.1-only` | Brian Nickel, Gabriel Burt, Stephen Shaw, et al. | https://github.com/mono/taglib-sharp |

## Transitive Runtime Dependencies

| Component | Version | License | Authors / Copyright |
| :--- | :---: | :--- | :--- |
| **Microsoft.Data.Sqlite.Core** | `10.0.13` | `MIT` | Microsoft |
| **Microsoft.Win32.SystemEvents** | `10.0.13` | `MIT` | Microsoft |
| **NAudio.Core** | `3.1.0` | `MIT` | Mark Heath |
| **SQLitePCLRaw.bundle_e_sqlite3** | `2.1.13` | `Apache-2.0` | Eric Sink |
| **SQLitePCLRaw.core** | `2.1.13` | `Apache-2.0` | Eric Sink |
| **SQLitePCLRaw.lib.e_sqlite3** | `2.1.13` | `Apache-2.0` | Eric Sink |
| **SQLitePCLRaw.provider.e_sqlite3** | `2.1.13` | `Apache-2.0` | Eric Sink |
| **System.Memory** | `4.5.3` | `MIT` *(per legacy `licenseUrl` pointing to `dotnet/corefx` `LICENSE.TXT`)* | Microsoft |
| **System.Numerics.Tensors** | `9.0.0` | `MIT` | Microsoft |
| **.NET 9 Desktop / WPF Runtime** *(bundled in self-contained builds)* | `9.0.x` | `MIT` | Microsoft |
| **SQLite (`e_sqlite3`)** *(bundled via `SQLitePCLRaw.lib.e_sqlite3`)* | — | Public Domain | SQLite Consortium |

## Development / Test-Only Dependencies (`tests/AuraAudio.Tests`)

Not included in published standalone release binaries:

| Component | Version | License |
| :--- | :---: | :--- |
| **Microsoft.NET.Test.Sdk** | `17.12.0` | `MIT` |
| **xunit** | `2.9.2` | `Apache-2.0` |
| **xunit.runner.visualstudio** | `2.8.2` | `Apache-2.0` |
| **Xunit.SkippableFact** | `1.5.85` | `MS-PL` |
| **coverlet.collector** | `6.0.2` | `MIT` |

## Licensing Notes

- **LGPL-2.1-only (`TagLibSharp`):** `TagLibSharp` is licensed under the GNU Lesser General Public License v2.1 only. Complete buildable source code and build instructions for AuraAudio are provided in this repository so recipients can rebuild and relink AuraAudio against a modified or updated version of `TagLibSharp`.
- **MS-PL (`BunLabs.NAudio.Flac`):** Distributed portions of `BunLabs.NAudio.Flac` remain subject to the Microsoft Public License (MS-PL), including preservation of all copyright and license notices.
