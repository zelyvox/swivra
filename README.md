# Swivra

[Türkçe](README.tr.md)

An offline file wheel for Windows. Drag a file, press Shift, Ctrl or Alt, and drop it on a slice. Nothing leaves your PC.

![The Swivra wheel](docs/images/wheel.png)

> Status: early release (0.2.0). Every slice works. Some results depend on optional software; see the notes below.

## Features

| Key | Wheel | What it does today |
| --- | --- | --- |
| Shift | Convert | Images: PNG, JPG, BMP, WebP, PDF. PDF: to PNG, JPG (one image per page, 200 dpi), plain text or Word. Documents (Word, text, Markdown): to PDF, text or Word |
| Ctrl | Tools | Images: crop, compress, cut out the background, rotate, resize to 1080 px wide, remove location and camera data. PDF: compress, hide sensitive data, split, merge, rotate, lock with a password. Other files: zip, lock in an encrypted zip, hide sensitive data in text and Word files |
| Alt | Send | WhatsApp, Mail, to your phone over Wi-Fi with a QR code, copy the file path |

- **Chains.** Rest on a slice for about half a second and it joins a chain. When you drop, every step runs in order (for example rotate, then remove location, then JPG).
- **Sensitive-data radar.** While you drag, Swivra scans the file. If it finds location data, a camera model, a Turkish ID number (TC kimlik no), an IBAN, an e-mail address or a phone number, the edge of the wheel turns orange. For PDFs it scans the first 20 pages.
- **Crop panel.** Dropping an image on Crop opens a panel where you set the crop area.
- **PDF tools.** Compress, split into pages, merge several PDFs in selection order, rotate, convert to images or text.
- **PDF lock.** Protects a PDF with a password, using AES-256. Other files go into an AES-256 encrypted zip (open it with 7-Zip; Windows Explorer cannot open AES zips).
- **Hide sensitive data.** In PDFs, Turkish ID numbers, IBANs, e-mail addresses and phone numbers are blacked out for real: the pages are turned into images, so the hidden text cannot be copied back. The output's text is no longer selectable. Text inside scanned images is not detected yet (no OCR). In text and Word files the items are replaced with ████.
- **Cut out.** Removes the background of an image offline with a small AI model (U-2-Net, u2netp). Good on simple subjects; hair and fine edges are not studio quality.
- **Send.** WhatsApp: the file (images downscaled, location data removed) is put on the clipboard and WhatsApp opens; press Ctrl+V in a chat. Mail: opens a new message in your default mail program with the file attached; without one, the file goes to the clipboard and a new Gmail message opens in your browser; paste or drag the file into it. To phone: shows a QR code; scan it with a phone on the same Wi-Fi and the file downloads straight from your PC. The link has a random token and stops working when you close the panel or after 10 minutes.
- **Multiple files.** Drag several files together. Dropping PDFs on Merge makes one file.
- **Originals stay untouched.** The result is saved next to the original under a new name, for example `Holiday-clean.jpg`.
- **Offline.** No account, no upload, nothing goes to the internet. Everything runs on your machine; only To phone uses your local network, and only while its panel is open.

Notes:
- **Word output and documents to PDF** keep the layout only if [LibreOffice](https://www.libreoffice.org/) is installed (Swivra finds it by itself). Without it you get the text only, and the result says so. Old `.doc`, `.rtf` and `.odt` files need LibreOffice.
- **To phone**: the first time, Windows Firewall asks whether Swivra may use the network. Allow it, or phones cannot connect.

![The crop panel](docs/images/crop.png)

## Download

1. Go to [GitHub Releases](../../releases) and download `Swivra-<version>-win-x64.zip`.
2. Unzip it anywhere.
3. Run `Swivra.exe`. It lives in the system tray.

Requirements: Windows 10 or 11 (64-bit) and the WebView2 Runtime. Windows 11 already has it. You do not need to install .NET.

The app is not code-signed, so Windows SmartScreen may warn you. Click "More info", then "Run anyway".

## Usage

1. Start dragging a file in Explorer or on the desktop.
2. Press **Shift** (convert), **Ctrl** (tools) or **Alt** (send). The wheel opens around the cursor.
3. Move over a slice. The center shows what will happen.
4. Drop the file on the slice.

- To chain steps, rest on a slice until it gets a number, then move to the next one.
- To cancel, drop the file in the center of the wheel.
- In the crop and lock panels, **Enter** applies and **Esc** cancels.
- Language: right-click the tray icon and use **Dil / Language**. The first run follows your Windows display language (Turkish or English).

The tray menu also has "Show the wheel (demo)", "Open logs" and "Exit". Logs are written to `%LOCALAPPDATA%\Swivra\swivra.log`.

## Build from source

You need Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and the WebView2 Runtime.

```powershell
cd src/Swivra.App
dotnet run
```

To build the release package (self-contained, win-x64):

```powershell
pwsh scripts/publish.ps1
```

This writes `publish/Swivra-<version>-win-x64.zip`. The app is shipped as a folder, not a single file: `web/` and the native PDFium and SkiaSharp libraries sit next to `Swivra.exe`.

### Developer flags

| Flag | Use |
| --- | --- |
| `--demo --shot out.png` | Show the wheel with a fake file and save a screenshot |
| `--demo-tool crop\|lock\|tel <file>` | Open a tool panel on a real file without dragging |
| `--run step,step <file> [...]` | Run steps without UI, for example `--run png,rot file.pdf`. Optional `--password pw` and `--crop x,y,w,h` |
| `--radar <file>` | Scan a file and log what the radar finds |
| `--lang tr\|en` | Use a language for this run only |

Results of `--run` and `--radar` go to the log file.

## How it works

- `InputHook` listens to the keyboard and mouse system-wide. It never swallows a key.
- `OverlayForm` is a round, always-on-top window that does not take focus. WebView2 draws the wheel (`web/`). The glass look is a blurred picture of the area behind the wheel, taken just before it opens.
- `CatcherForm` is a nearly invisible window over the wheel and is the real drop target. It reads the file path as soon as the drag starts (for the radar). It always sets the drop effect to copy, so Explorer never moves the original.
- `FileActions`, `ImageActions`, `PdfActions`, `PdfRedact`, `PdfWord`, `DocActions`, `SendActions`, `PhoneShare` and `Radar` do the work and the scanning. Images use GDI+ and SkiaSharp (WebP). PDFs use PDFium (via PDFtoImage) for rendering, PdfPig for text and PDFsharp for rotate, split, merge, compress, lock and redaction output. Word files use the Open XML SDK, encrypted zips SharpZipLib, background removal ONNX Runtime, QR codes QRCoder.

Library and license research: [docs/kutuphane-arastirmasi.md](docs/kutuphane-arastirmasi.md) (in Turkish).

## License

[MIT](LICENSE). Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
