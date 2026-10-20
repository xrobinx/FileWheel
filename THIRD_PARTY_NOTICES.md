# Third-party components

The root MIT license applies to original FileWheel code and assets. It does not replace the licenses of dependencies or optional external programs. Dependencies are restored through NuGet; their upstream source, notices, and licenses remain applicable.

| Component | Version | License / source |
| --- | --- | --- |
| Magick.NET-Q8-x64 and Magick.NET.Core | 15.0.0 | [Apache-2.0; upstream source](https://github.com/dlemstra/Magick.NET). ImageMagick and its native libraries have additional notices in `licenses/Magick.NET-Notice.txt`. |
| PDFsharp | 6.2.4 | [MIT; versioned upstream source](https://github.com/empira/PDFsharp/tree/v6.2.4). The included license is from this version, not the newer upstream license. |
| PdfPig | 0.1.16 | [Apache-2.0; upstream source](https://github.com/UglyToad/PdfPig) |
| bblanchon.PDFium.Win32 | 157.0.8100 | [Versioned packaging source](https://github.com/bblanchon/pdfium-binaries/tree/5b4d6b2ede1c1b0e17f5dc7b996c42d68d534ca8). NuGet metadata declares Apache-2.0; the packaging repository's license is MIT. Both texts are included. PDFium uses BSD-style terms and includes third-party components; see the native package notices. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | [MIT; upstream source](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | [MIT; upstream source](https://github.com/dotnet/runtime) |
| .NET / WPF runtime | 10.0 | [MIT and third-party notices; upstream source](https://github.com/dotnet). Included notice files are under `licenses/`. |

The exact NuGet dependencies are declared in `FileWheel.csproj`; restore resolves their transitive dependencies. License files copied from the installed packages are in `licenses/`.

## Optional external programs

- **FFmpeg** is launched as a separate command-line program and is not distributed in the public FileWheel package. The original local installation used Gyan's FFmpeg 7.1 essentials build, which reports **GPL-3.0-or-later** (`--enable-gpl --enable-version3`). Obtain it from [the upstream build release](https://github.com/GyanD/codexffmpeg/releases/tag/7.1). That release identifies [FFmpeg source commit b08d7969c5](https://github.com/FFmpeg/FFmpeg/commit/b08d7969c5). See [FFmpeg licensing](https://ffmpeg.org/legal.html). Its license is separate from FileWheel's MIT license.
- **LibreOffice**, when separately installed, provides additional document conversions. Its code and licenses are maintained by [The Document Foundation](https://www.libreoffice.org/about-us/licenses/); it is not distributed here.

When creating your own binary distribution, preserve notices for every included component and satisfy its corresponding license terms, including source requirements where applicable.
