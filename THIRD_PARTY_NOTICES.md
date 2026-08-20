# Third-party notices

GO includes or depends on the following third-party components. Exact versions are centrally pinned in `Directory.Packages.props`; transitive dependencies remain governed by their own notices and license files from the restored packages.

| Component | Version | License |
|---|---:|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| DocumentFormat.OpenXml | 3.5.1 | MIT |
| Microsoft.Data.Sqlite | 10.0.10 | MIT |
| Microsoft.Extensions libraries | 10.0.10 | MIT |
| Microsoft Windows App SDK | 2.3.1 | Microsoft package license (`license.txt` in the NuGet package) |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4948 | Microsoft package license |
| PdfPig | 0.1.15 | Apache-2.0 |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.4 | Apache-2.0 |
| SQLite | bundled transitively by SQLitePCLRaw | Public domain |
| KaTeX | 0.16.10 | MIT |
| ONNX Runtime GPU | 1.23.2 | MIT |
| Supertonic Python SDK | 1.3.1 | MIT |
| Supertone/supertonic-3 | `3cadd1ee6394adea1bd021217a0e650ede09a323` | OpenRAIL-M |
| xUnit.net and test runner | 2.9.3 / 3.1.5 | Apache-2.0 |

KaTeX is bundled locally under `src/GoWinUI.App/Assets/Web/vendor/katex/0.16.10`; its unmodified MIT license text is retained beside the distribution. The assistant web UI does not load libraries from a CDN.

The pinned Supertonic-3 assets are installed offline before deployment. No model files are fetched while a speech worker is running.

Release and source redistributors must preserve this notice, the bundled KaTeX license, and the license/notices shipped by the corresponding NuGet packages.
