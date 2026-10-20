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
- **pdfium.dll** is Apache-2.0 (the NuGet package declares it). PDFium itself statically includes further third-party code (for example FreeType, lcms2, OpenJPEG, libjpeg-turbo, zlib, ICU). Their license texts are not in the NuGet package, so they were taken from the `pdfium-win-x64.tgz` release archive of https://github.com/bblanchon/pdfium-binaries for the same version (152.0.7961). The archive's `licenses/` folder names: abseil (Apache-2.0), AGG 2.3 (Anti-Grain Geometry, its own short permissive notice), fast_float (MIT), FreeType (FreeType License), ICU (Unicode License v3), lcms2 (MIT), libjpeg-turbo (BSD-style and IJG), OpenJPEG (BSD 2-clause), libpng (PNG Reference Library License), libtiff (permissive, BSD-like), llvm-libc (Apache-2.0 with LLVM Exceptions), simdutf (MIT), zlib (zlib License), and PDFium itself (BSD 3-clause text followed by the Apache-2.0 text). The archive's own `LICENSE` (the pdfium-binaries build scripts, MIT, Copyright Benoit Blanchon) is included too. Which of these are actually linked into `pdfium.dll` is decided by the PDFium build; this file only repeats what the archive lists. The FreeType credit line from the SkiaSharp section applies here too. Because only executable code is distributed, the IJG license also requires this statement: *This software is based in part on the work of the Independent JPEG Group.*
- **PdfPig** is Apache-2.0. It started as a port of Apache PDFBox. Its repository `NOTICES.txt` (tag v0.1.16) states: derived from Apache Software Foundation software (PDFBox and FontBox, Copyright (c) 2002-2007 www.pdfbox.org), and includes the Adobe Glyph List and the Zapf Dingbats Glyph List (Copyright Adobe Systems Incorporated). The Adobe list license (BSD 3-clause) was taken from https://github.com/adobe-type-tools/agl-aglfn (default branch, no release tag).
- **PDFsharp 6.2.4 and PdfSharp.WPFonts.dll** are MIT per the nuspec. PDFsharp's Pkcs dependency (`System.Security.Cryptography.Pkcs.dll`) is part of the .NET runtime (MIT).

## What the license summary shows

Everything is MIT, Apache-2.0 or BSD-style, except the third-party code listed inside SkiaSharp's native notices (FreeType credit line, Adobe DNG SDK, the MPL-licensed gif code) and the code bundled in PDFium (AGG, libtiff, FreeType, IJG and other permissive but non-standard texts, listed above). Nothing in the package is GPL-only or requires Swivra to be open-sourced beyond what it already is.

## License texts

The files copied into the `licenses/` folder of the release zip are of two kinds.

From the NuGet cache: WebView2 `LICENSE.txt` and `NOTICE.txt`, SkiaSharp `LICENSE.txt` and the native `THIRD-PARTY-NOTICES.txt`, the Microsoft.Extensions (Logging.Abstractions and DependencyInjection.Abstractions) `LICENSE.TXT`, ONNX Runtime `LICENSE` and `ThirdPartyNotices.txt`, QRCoder `LICENSE.txt`, and the .NET `LICENSE.txt` and `ThirdPartyNotices.txt`.

From the `third-party-licenses/` folder of this repository (copied as a whole by `scripts/publish.ps1`, which fails if the folder is missing). These packages carry no license file in their NuGet package, so the texts were taken from the upstream repositories:

| File | Component | Source |
| --- | --- | --- |
| `PdfPig-LICENSE.txt`, `PdfPig-NOTICES.txt` | PdfPig 0.1.16 (Apache-2.0) | UglyToad/PdfPig, tag v0.1.16 |
| `AdobeGlyphList-LICENSE.md` | Adobe Glyph List, used by PdfPig (BSD 3-clause) | adobe-type-tools/agl-aglfn, default branch |
| `PDFsharp-LICENSE.txt` | PDFsharp 6.2.4 and PdfSharp.WPFonts (MIT) | empira/PDFsharp, tag v6.2.4 |
| `PDFtoImage-LICENSE.txt` | PDFtoImage 5.4.0 (MIT) | sungaila/PDFtoImage, tag v5.4.0 |
| `OpenXml-LICENSE.txt` | DocumentFormat.OpenXml 3.5.1 (MIT) | dotnet/Open-XML-SDK, tag v3.5.1 |
| `SharpZipLib-LICENSE.txt` | SharpZipLib 1.4.2 (MIT) | icsharpcode/SharpZipLib, tag v1.4.2 |
| `U-2-Net-LICENSE.txt` | U-2-Net weights (Apache-2.0) | xuebinqin/U-2-Net, default branch (master) |
| `rembg-LICENSE.txt` | rembg ONNX export (MIT) | danielgatis/rembg, default branch (main) |
| `pdfium-LICENSE.txt`, `pdfium-binaries-LICENSE.txt`, `pdfium-third-party/*` | `pdfium.dll` 152.0.7961 and the code it bundles | `pdfium-win-x64.tgz` of bblanchon/pdfium-binaries release chromium/7961 |

The Apache-2.0 files from PdfPig and U-2-Net are the unmodified standard text, as published upstream, with the appendix template unfilled (no project-specific copyright line exists in those files).
