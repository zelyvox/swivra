# Third-party notices

Swivra itself is MIT-licensed (see [LICENSE](LICENSE)). The release package also contains the software below. Versions and licenses were read from the NuGet packages that `dotnet publish` restores (nuspec `license` field and the license files inside the packages), not from memory.

Copies of the license texts that ship with these packages are placed in the `licenses/` folder of the release zip.

## Packages

| Component | Version | License | Link |
| --- | --- | --- | --- |
| Microsoft.Web.WebView2 (SDK: `Microsoft.Web.WebView2.*.dll`, `WebView2Loader.dll`) | 1.0.4258.31 | Microsoft WebView2 SDK license (BSD-style, see below) | https://aka.ms/webview |
| PdfPig (`UglyToad.PdfPig*.dll`) | 0.1.16 | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| PDFsharp (`PdfSharp*.dll`) | 6.2.4 | MIT | https://docs.pdfsharp.net/ |
| PDFtoImage (`PDFtoImage.dll`) | 5.4.0 | MIT | https://github.com/sungaila/PDFtoImage |
| bblanchon.PDFium.Win32 (`pdfium.dll`, native PDFium build) | 152.0.7961 | Apache-2.0 | https://github.com/bblanchon/pdfium-binaries |
| SkiaSharp (`SkiaSharp.dll`) | 4.150.1 | MIT | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.Win32 (`libSkiaSharp.dll`, native Skia build) | 4.150.1 | MIT, plus bundled native libraries (see below) | https://github.com/mono/SkiaSharp |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | https://dot.net/ |
| .NET 10 runtime and Windows Forms (self-contained, all `System.*.dll`, `coreclr.dll`, `msquic.dll` and similar) | 10.0.8 | MIT | https://github.com/dotnet/runtime |
| Microsoft.ML.OnnxRuntime (`Microsoft.ML.OnnxRuntime.dll`, native `onnxruntime.dll`) | 1.31.0 | MIT | https://github.com/microsoft/onnxruntime |
| U-2-Net model weights, u2netp (`models/u2netp.onnx`, ONNX export from rembg) | rembg release v0.0.0, SHA-256 `309C8469258DDA742793DCE0EBEA8E6DD393174F89934733ECC8B14C76F4DDD8` | Apache-2.0 (weights, U-2-Net); MIT (rembg export) | https://github.com/xuebinqin/U-2-Net, https://github.com/danielgatis/rembg |
| DocumentFormat.OpenXml and DocumentFormat.OpenXml.Framework | 3.5.1 | MIT | https://github.com/dotnet/Open-XML-SDK |
| SharpZipLib (`ICSharpCode.SharpZipLib.dll`) | 1.4.2 | MIT | https://github.com/icsharpcode/SharpZipLib |
| QRCoder (`QRCoder.dll`) | 1.8.1 | MIT | https://github.com/Shane32/QRCoder |

The WebView2 **Runtime** (the Edge-based browser engine) is not part of the package. It is installed separately by Windows or by the user and is covered by its own Microsoft license.

## Not plain MIT / Apache / BSD: please read

- **Microsoft.Web.WebView2 SDK.** The license text (`LICENSE.txt` in the package) is BSD-style: redistribution of source and binary forms is allowed with the copyright notice kept. Its `NOTICE.txt` also lists Antlr3.Runtime 3.5.2-rc1 and StringTemplate4 4.0.9-rc1 (both BSD 3-Clause) and carries a Microsoft source-offer paragraph. Both are included in `licenses/WebView2-NOTICE.txt`.
- **libSkiaSharp.dll bundles native libraries.** The package's own `THIRD-PARTY-NOTICES.txt` lists: ANGLE, HarfBuzz, Skia, etc1, gif, libpng, DNG SDK, expat, FreeType, ICU, imgui, jsoncpp, libjpeg-turbo, libwebp, libmicrohttpd, piex, sdl, sfntly, SPIR-V Headers, SPIR-V Tools and zlib. Most are BSD, MIT, Apache or similar. These need attention:
  - **FreeType** is dual-licensed (FreeType License or GPLv2). Swivra uses it under the **FreeType License**, which requires a credit line: *Portions of this software are copyright (c) The FreeType Project (www.freetype.org). All rights reserved.*
  - **DNG SDK** (Adobe) is under a custom Adobe license agreement, not an open-source license. Its terms were not reviewed in detail; the full text is in `licenses/SkiaSharp-native-THIRD-PARTY-NOTICES.txt`.
  - **gif** (a library section that is tri-licensed MPL 1.1 / GPL 2.0 / LGPL 2.1) is used under the **MPL 1.1** option.
  - **ICU** uses the ICU (Unicode) license, which is permissive.
  Which of these are actually linked into the Windows build is decided by the SkiaSharp maintainers; this file only repeats what their notices list.
- **pdfium.dll** is Apache-2.0 (the NuGet package declares it). PDFium itself statically includes further third-party code (for example FreeType, lcms2, OpenJPEG, libjpeg-turbo, zlib, ICU). Their license texts are in the PDFium source tree and in the release archives of https://github.com/bblanchon/pdfium-binaries, not in the NuGet package, so they were **not** verified here.
- **PdfPig** is Apache-2.0. It started as a port of Apache PDFBox. The NuGet package ships no separate notice file for any third-party data it embeds, so that was **not** verified here.
- **PDFsharp 6.2.4 and PdfSharp.WPFonts.dll** are MIT per the nuspec. PDFsharp's Pkcs dependency (`System.Security.Cryptography.Pkcs.dll`) is part of the .NET runtime (MIT).

## What the license summary shows

Everything is MIT, Apache-2.0 or BSD-style, except the third-party code listed inside SkiaSharp's native notices (FreeType credit line, Adobe DNG SDK, the MPL-licensed gif code) and the PDFium bundled code that was not checked. Nothing in the package is GPL-only or requires Swivra to be open-sourced beyond what it already is.

## License texts

MIT and Apache-2.0 texts are in the respective projects. The files copied into the release zip are: WebView2 `LICENSE.txt` and `NOTICE.txt`, SkiaSharp `LICENSE.txt` and the native `THIRD-PARTY-NOTICES.txt`, the Microsoft.Extensions `LICENSE.TXT`, and the .NET `LICENSE.txt` and `ThirdPartyNotices.txt`.
