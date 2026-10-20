# FileWheel

Drag a file, hold Shift, and choose an action from a desktop wheel. FileWheel is a Windows x64 app written in C# with WPF for converting and editing images, PDFs, documents, audio, video, and archives. Available actions depend on the file type and installed engines.

## Download

Download the Windows ZIP from [Releases](https://github.com/xrobinx/FileWheel/releases/latest), extract it, and run `FileWheel.exe`. The public package includes the app and third-party notices. Install FFmpeg separately to enable audio and video features; see below.

## Build from source

Requirements: Windows x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/xrobinx/FileWheel.git
cd FileWheel
dotnet restore FileWheel.csproj
dotnet build FileWheel.csproj -c Release
dotnet publish FileWheel.csproj -c Release -r win-x64 --self-contained true -o publish
```

Run `publish\FileWheel.exe`. The project publishes a self-contained Windows executable; end users do not need to install .NET. The complete app source, WPF resources, icons, native Windows integration, and developer tools are in this repository. NuGet restores the third-party libraries declared in `FileWheel.csproj`.

## Optional engines

### FFmpeg: audio and video

FFmpeg is an independent executable that FileWheel invokes through command-line arguments. It is not included in the public source or Windows package and is not licensed under this project's MIT license.

1. Download a Windows x64 build from [Gyan's FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/). The original local app used [FFmpeg 7.1 essentials](https://github.com/GyanD/codexffmpeg/releases/tag/7.1).
2. Extract the upstream download, retaining its license and notices.
3. Copy `ffmpeg.exe` into `engines\ffmpeg.exe` beside your built `FileWheel.exe`. For the published app this is `publish\engines\ffmpeg.exe`.

For development, place it in this repository's `engines` directory before building; the project copies it to the output. Engine executables are ignored by Git.

The Gyan build uses GPLv3. Its upstream download, licensing, source reference, and build details are provided by its publisher. If you redistribute an engine, meet that engine's license requirements separately; adding this repository's MIT license does not relicense it.

### LibreOffice: additional Office conversions

Some Office document conversions use a separately installed [LibreOffice](https://www.libreoffice.org/download/download-libreoffice/). FileWheel detects it when available. LibreOffice is not bundled.

## Source layout

| Path | Contents |
| --- | --- |
| `Program.cs`, `Controller.cs`, `Settings.cs` | App startup, action routing, settings |
| `Core/` | Converters, file operations, image/PDF/media/document handling |
| `UI/` | WPF windows, wheel, editors, theme |
| `Native/` | Windows input, drag-and-drop, shell and tray integration |
| `Assets/` | App icons |
| `Cli.cs`, `Snapshots.cs`, `DragTest.cs`, `tools/` | Developer utilities |

The optional icon-generation utility `tools/make_icon.py` requires Python and Pillow; they are not needed to build the app because the icons are already included.

## License

FileWheel's original code and assets are licensed under the [MIT License](LICENSE), copyright 2026 xrobinx. Third-party components keep their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [`licenses/`](licenses/).
