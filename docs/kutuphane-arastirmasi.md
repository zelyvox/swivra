# Swivra: Kütüphane ve Araç Araştırması

Araştırma tarihi: 2026-10-19. Hedef: C# / .NET 10, WinForms + WebView2, Windows 10/11, tamamen çevrimdışı, MIT lisanslı açık kaynak (ileride ücretli katman ihtimali).

Doğrulama yöntemi: NuGet API (nuspec `license` alanı, yayın tarihleri), GitHub API (SPDX, son release), resmi repo LICENSE dosyaları, resmi site sayfaları ve yerel deneme (FFmpeg build'inin `-encoders` / `-buildconf` çıktısı, Windows OCR dil listesi). Doğrulayamadığım yer "doğrulanamadı" diye işaretli. Boyutlar tahmindir; NuGet paket boyutu çok platformlu olduğu için installer'a giren gerçek boyuttan büyüktür. Bu belge hukuki danışmanlık değildir.

**En önemli yeni bulgu:** PDFsharp 6.5.0 ve 7.0 önizlemeleri 2026-10-13'te AGPL-3.0 + ticari çift lisansa geçti (NuGet nuspec: `AGPL-3.0-only`; docs.pdfsharp.net: "published under AGPL and Commercial License"). 6.2.4 ve öncesi MIT. Sürümü **6.2.4'e sabitle**, floating sürüm veya otomatik güncelleme kullanma.

---

## 1. PDF → DOCX (düzen korunarak)

En zor kalem. Bedava ve izinli lisanslı, yüksek kaliteli bir çözüm yok.

| Aday | Lisans | .NET'ten kullanım | Boyut | Çevrimdışı | Not |
|---|---|---|---|---|---|
| LibreOffice headless ([lisans sayfası](https://www.libreoffice.org/about-us/licenses/)) | MPL-2.0 (resmi site doğrulandı) | Subprocess: `soffice --headless --infilter="writer_pdf_import" --convert-to docx` | Kurulu haliyle yüzlerce MB (kesin rakam doğrulanamadı) | Evet | PDF'i Writer'a metin çerçeveleri olarak alır. Basit belgelerde kabul edilebilir, karmaşık sütun/tablo/taramada düzen bozulur (deneyime dayalı, ölçmedim). MPL-2.0: uygulamamız MIT kalabilir, LibreOffice'e kendi değişikliğimiz olursa o dosyalar MPL altında yayınlanır. Kullanıcıda kuruluysa bulup kullanmak (bundle etmeden) en hafif yol. |
| pdf2docx ([repo](https://github.com/dothinking/pdf2docx)) | Repo MIT (GitHub SPDX), **ama bağımlılığı `PyMuPDF>=1.26.7` = AGPL-3.0** (requirements.txt ve PyMuPDF SPDX doğrulandı) | Gömülü Python + subprocess | Python + PyMuPDF + OpenCV + numpy: 100+ MB (tahmin) | Evet | **Tuzak**: bundle edersek AGPL bileşen dağıtmış oluruz. README: "no longer actively maintained by Artifex", son release v0.5.13 (2026-05-01). Tavsiye edilmez. |
| Ticari SDK'lar | Ticari | NuGet | - | Evet | Docotic.Pdf: uygulama başına $995, Ultimate $3.495 ([bitmiracle.com](https://bitmiracle.com/pdf-library/licenses/)); PDF→Word desteği doğrulanamadı. Aspose.PDF 26.10.0, Spire.PDF 12.10.0, Syncfusion.Pdf 35.1.40: ticari, fiyat doğrulanamadı. Açık kaynak MIT ürüne uymaz (lisans anahtarını repoya koyamayız). |
| PdfPig + DocumentFormat.OpenXml ile kendi dönüştürücümüz | Apache-2.0 + OpenXml (lisansını doğrulamadım, MIT olduğunu hatırlıyorum) | NuGet | ~5 MB | Evet | Kelime konumlarından paragraf/tablo çıkarıp DOCX üretmek. "Düzen korunmuş" değil, "metin akışı korunmuş". Büyük emek. |

**Öneri:** v1'e PDF→DOCX koyma (ya da "beta, kullanıcıda LibreOffice varsa" olarak koy). Sebep: izinli lisanslı tek gerçekçi motor LibreOffice, yüzlerce MB bundle etmeye değmez; pdf2docx AGPL tuzağı.

---

## 2. PDF → PNG/JPG sayfa render

| Aday | Lisans | Kullanım | Boyut | Not / bakım |
|---|---|---|---|---|
| **PDFtoImage** ([repo](https://github.com/sungaila/PDFtoImage)) | MIT | NuGet `PDFtoImage` 5.4.0 (release 2026-08-16; 6.0.0-preview 2026-09-28). PDFium + SkiaSharp üzerine | nupkg 0,5 MB; yük PDFium + Skia native | Aktif. DPI ayarı, şifreli PDF desteği. Skia'ya bağımlı (görüntü işleme için zaten gerekli). |
| **PDFium binary** ([bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)) | PDFium Apache-2.0 (nuspec), repo MIT | `bblanchon.PDFium.Win32` 157.0.8100 (2026-10-12) P/Invoke ile, ya da PDFiumCore (Apache-2.0, 4688.0.0) | pdfium.dll x64 ~5-6 MB (tahmin; nupkg 10,5 MB çok mimarili) | Çok aktif. |
| Docnet.Core ([repo](https://github.com/GowenGit/docnet)) | MIT | NuGet `Docnet.Core` 2.6.0 | nupkg 17,6 MB | 2.7.0-alpha.1 var; son kararlı sürümün tarihi doğrulanamadı. PDFium'u kendi paketler, güncellemesi PDFtoImage'a göre yavaş. |

**Öneri:** PDFtoImage (+ PDFium). Sebep: MIT, aktif, tek paketle bitiyor.

---

## 3. PDF → düz metin

| Aday | Lisans | Kullanım | Boyut | Not |
|---|---|---|---|---|
| **PdfPig** ([repo](https://github.com/UglyToad/PdfPig)) | Apache-2.0 | NuGet `PdfPig` 0.1.16 (release 2026-08-22; günlük alpha'lar var) | nupkg 15,9 MB (çok hedefli); asıl DLL birkaç MB | Saf C#, çevrimdışı. 1.0 öncesi: API sürümler arası değişebilir. `page.Text` okuma sırası olmayabilir, `ContentOrderTextExtractor` kullan. Kelime koordinatı verir (redaksiyon ve radar için de lazım). Taranmış PDF'te metin yok, OCR gerekir. |
| PDFium `FPDFText_*` | Apache-2.0 | P/Invoke | ek yok | Doğru ama sarmalayıcıyı biz yazarız. |

**Öneri:** PdfPig. Sebep: izinli lisans, saf C#, kelime konumları radar/redaksiyon için de gerekli.

---

## 4. PDF sıkıştır / böl / birleştir / döndür / parola

| Aday | Lisans | Kullanım | Boyut | Not |
|---|---|---|---|---|
| **QPDF** ([repo](https://github.com/qpdf/qpdf)) | Apache-2.0 (7.0 öncesi Artistic-2.0) | Subprocess `qpdf.exe`, 12.4.2 (2026-09-27; msvc64 zip 26 MB, kurulum exe 12 MB) | ~10-25 MB (tahmin) | Böl, birleştir, döndür, AES-256 şifre, linearize, nesne akışı sıkıştırma. **Görüntü yeniden örnekleme yapmaz**: "sıkıştırma" yalnız yapısal (genelde küçük kazanç). Render/metin çıkarma yok. NuGet `QpdfNet` 1.6.0'ın nuspec lisansı boş: doğrulanamadı, subprocess kullan. |
| **PDFsharp 6.2.4** ([docs](https://docs.pdfsharp.net/)) | **MIT** (6.2.4 nuspec doğrulandı); **6.5.0 / 7.0-preview AGPL-3.0-only** | NuGet `PDFsharp` **6.2.4'e sabit** | nupkg 4,4 MB | Böl/birleştir/döndür/şifre/metadata. Görüntü XObject'lerine erişip kendimiz yeniden kodlayabiliriz (SkiaSharp ile JPEG kalitesi düşürme): hazır araç değil, bizim işimiz. Bakım ileride durabilir. |
| iText ([itext-dotnet](https://github.com/itext/itext-dotnet)) | **AGPL-3.0** (NuGet licenseUrl gnu.org/agpl, 9.8.0) | NuGet | - | **Kullanma.** |
| Ghostscript ([artifex.com/licensing](https://www.artifex.com/licensing)) | **AGPL-3.0** veya Artifex ticari (AGPLv3 doğrulandı) | Subprocess | ~30-50 MB (tahmin) | PDF sıkıştırmada altın standart ama AGPL: **kullanma.** Subprocess olsa bile dağıtım yükümlülüğü ve belirsizlik doğurur. |
| PdfSharpCore 1.3.67 | Nuspec'te "LICENSE.md", terk edilmiş fork | - | - | Kullanma. |

**Öneri:** QPDF (böl/birleştir/döndür/şifre) + gerçek sıkıştırma için PDFsharp 6.2.4 ile görüntüleri yeniden kodlama (kendi kodumuz). Sebep: hepsi Apache/MIT; görüntü yeniden örnekleme için hazır izinli araç yok, bu açık bir iş kalemi.

---

## 5. PDF redaksiyon (altındaki metin gerçekten silinsin)

İzinli lisanslı, hazır "apply redaction" yapan bir .NET kütüphanesi **bulamadım**.

| Yol | Lisans | Not |
|---|---|---|
| **Rasterize et, siyah kutu çiz, yeniden PDF yap** (PDFtoImage render + SkiaSharp kutu + PDFsharp 6.2.4 ile görüntüden PDF) | MIT/Apache | Metin gerçekten silinir (sayfa görüntüye dönüşür). Bedel: seçilebilir metin kaybolur, boyut artar. İstenirse sonra OCR ile görünmez metin katmanı eklenir (redakte alanlar hariç). Yer tespiti PdfPig kelime konumlarıyla. |
| MuPDF / PyMuPDF `apply_redactions` | **AGPL-3.0** (SPDX doğrulandı) | Gerçek içerik-akışı redaksiyonu yapar. Kullanma. |
| Aspose.PDF, Syncfusion, Docotic | Ticari | Redaksiyon özelliği satıcı iddiası, doğrulanamadı. Açık kaynak ürüne uymaz. |
| PDFium ile nesne silme | Apache-2.0 | Metin nesnelerini silmek mümkün olabilir ama güvenli, olgun bir akış olduğu doğrulanamadı. |
| Sadece üstüne dikdörtgen çizmek | - | **Yanlış/tehlikeli**: metin kopyalanabilir kalır. |

**Öneri:** v1'de rasterize-redaksiyon, "sayfalar görüntüye dönüşür" uyarısıyla. Sebep: izinli lisanslı tek ve kanıtlanabilir biçimde güvenli yol.

---

## 6. Görüntü dönüştür / boyutlandır / sıkıştır / döndür / kırp (PNG, JPG, WebP, AVIF)

| Aday | Lisans | Kullanım | Boyut | AVIF/WebP | Not |
|---|---|---|---|---|---|
| **SkiaSharp** ([repo](https://github.com/mono/SkiaSharp)) | MIT | NuGet `SkiaSharp` 4.153.1 (2026-09-30) + `SkiaSharp.NativeAssets.Win32` | libSkiaSharp.dll ~10 MB (tahmin; nupkg 82,9 MB çok platformlu) | PNG/JPG/WebP evet. **AVIF doğrulanamadı** (kodlama muhtemelen yok) | PDFtoImage zaten istiyor, ek maliyet yok. Resize/crop/rotate/kalite ayarı kolay. |
| **Magick.NET** ([repo](https://github.com/dlemstra/Magick.NET)) | Apache-2.0 | NuGet `Magick.NET-Q8-x64` / `-AnyCPU` 15.0.0 (2026-10-10) | Native ~20+ MB (tahmin; nupkg 84 MB) | **AVIF coder var** (repoda `MagickFormat.Avif` ve `TheAvifCoder` testi doğrulandı), WebP evet | En geniş format desteği. Büyük. `Strip()` ile metadata temizleme çok kolay. |
| **SixLabors.ImageSharp** 4.1.3 (2026-10-09) | **Six Labors Split License v1.0** (LICENSE metni okundu) | NuGet | nupkg 1,3 MB (saf C#) | WebP evet; `Formats/Heif` klasörü var, AVIF kapsamı doğrulanamadı | Apache-2.0 koşulları: kaynağı açık/Open Source lisanslı yazılımda kullanım, geçişli bağımlılık, <1M USD yıllık gelirli kâr amaçlı şirket/birey, kâr amacı gütmeyen kuruluş. Diğer tüm durumlar için ticari lisans satın alınır. Çark MIT olduğu sürece Apache-2.0 geçerli; **ileride kapalı/ücretli katman ve 1M USD üstü gelir = ticari lisans**. Sarı bayrak. |
| NetVips / libvips ([net-vips](https://github.com/kleisauke/net-vips)) | NetVips MIT; libvips LGPL-2.1 (SPDX); `NetVips.Native.win-x64` 8.18.7 nuspec: LGPL-3.0-or-later | NuGet `NetVips` 3.2.0 + `NetVips.Native.win-x64` | nupkg 7,7 MB | Heif/AVIF/WebP kapsamı doğrulanamadı | Hızlı, az bellek. LGPL: dinamik DLL, lisans metni, değiştirilebilirlik şartı. |

**Öneri:** v1 için SkiaSharp (zaten bağımlılık); AVIF istenirse Magick.NET'i ek paket olarak ekle. Sebep: en az ek boyut ve MIT; AVIF yalnız Magick.NET'te doğrulandı.

---

## 7. Metadata temizleme (görüntü EXIF/GPS/kamera, PDF yazar/producer)

| Konu | Aday | Lisans | Not |
|---|---|---|---|
| Görüntüde okuma (EXIF/GPS göster) | **MetadataExtractor** 2.9.3 | Apache-2.0 | Salt okunur, nupkg 1 MB. Radar için de. |
| Görüntüde temizleme (kayıpsız) | Kendi kodumuz: JPEG'de APP1 (EXIF/XMP) ve APP13 (IPTC) segmentlerini at; PNG'de `eXIf`, `tEXt/iTXt` chunk'larını at | - | Yeniden kodlama yok, kalite kaybı yok. Hafif. |
| Görüntüde temizleme (yeniden kodlayarak) | SkiaSharp yeniden kodlama veya Magick.NET `Strip()` | MIT / Apache-2.0 | Önce EXIF yönünü (orientation) uygula. JPEG yeniden kodlaması kayıplı. |
| PDF metadata | PDFsharp 6.2.4: `Info` alanlarını (Author/Producer/Creator/Title) temizle, XMP metadata akışını sil, tam yeniden yaz | MIT | Artımlı güncelleme eski metadata'yı dosyada bırakabilir; tam yeniden yazma gerekir. QPDF'in bunu doğrudan yaptığı doğrulanamadı. |

**Öneri:** JPEG/PNG için kendi segment-kesme kodu + MetadataExtractor (gösterim); PDF için PDFsharp 6.2.4. Sebep: kayıpsız ve yeni bağımlılık getirmiyor.

---

## 8. Çevrimdışı arka plan silme

Çalıştırıcı: **ONNX Runtime** ([repo](https://github.com/microsoft/onnxruntime)), MIT (SPDX), `Microsoft.ML.OnnxRuntime` 1.31.0 (2026-10-09). `.DirectML` paketi 1.24.4'te geride. Native onnxruntime.dll CPU için ~12-15 MB (tahmin; nupkg 152 MB çok platformlu).

| Model | Lisans | Boyut | Not |
|---|---|---|---|
| u2net / u2netp ([U-2-Net](https://github.com/xuebinqin/U-2-Net)) | Apache-2.0 (SPDX doğrulandı) | u2net ~176 MB, u2netp ~5 MB (tahmin) | Eski, kenarlar blokumsu (rembg README de belirtiyor). |
| isnet-general-use ([DIS](https://github.com/xuebinqin/DIS)) | Apache-2.0 (SPDX doğrulandı) | ~170 MB (tahmin) | rembg'in yeni modelleri arasında. İyi denge. |
| silueta | u2net türevi (lisans doğrulanamadı) | 43 MB (rembg README) | Küçük. |
| BiRefNet ([repo](https://github.com/ZhengPeng7/BiRefNet)) | Repo MIT (SPDX); ağırlık lisansı HF kartından doğrulanamadı | 200 MB+ (tahmin) | En kaliteli, ağır. ONNX dosyaları repo release `v1`'de. |
| BRIA RMBG-1.4 / 2.0 | Ticari olmayan lisans olduğunu hatırlıyorum, **doğrulanamadı** | - | rembg `bria-rmbg` listeliyor. Tuzak adayı, kullanma. |

rembg (Python) [danielgatis/rembg](https://github.com/danielgatis/rembg) MIT, ama Python'a gerek yok: ONNX'i ORT ile C#'tan çalıştırıp ön/son işlemi (yeniden boyutla, normalize et, maskeyi alfa yap) kendimiz yazarız.

**Öneri:** ORT + isnet-general-use (Apache-2.0), model installer'a girmesin; eklenti paketi olarak ayrı .zip. Sebep: lisans temiz, kalite/boyut dengeli. (İlk kullanımda indirme "çevrimdışı" vaadiyle çelişir; ayrı indirilebilir eklenti daha tutarlı.)

---

## 9. Çevrimdışı OCR (Türkçe)

| Aday | Lisans | Kullanım | Boyut | Not |
|---|---|---|---|---|
| **Windows.Media.Ocr** (yerleşik) | OS bileşeni | TFM `net10.0-windows10.0.19041.0`, `OcrEngine.TryCreateFromLanguage(new Language("tr"))` | 0 MB | Yalnız kullanıcının Windows'una kurulu OCR dilleri (`AvailableRecognizerLanguages`). Bu makinede yerel testte dönen liste: `tr`. Başka kullanıcıda Türkçe kurulu olmayabilir. Metin+konum verir, aranabilir PDF üretmez. Çevrimdışı. |
| **Tesseract 5.5.3** ([repo](https://github.com/tesseract-ocr/tesseract), 2026-07-24) | Apache-2.0 | Sarmalayıcı: `TesseractOCR` 5.5.2 (Apache-2.0, son push 2026-05) veya `Tesseract` 5.2.0 (charlesw, Apache-2.0, son push 2025-04) | Native ~9 MB + `tur.traineddata`: fast 4,5 MB, best 7,5 MB (indirip ölçtüm) | `tessdata_fast` Apache-2.0 (LICENSE doğrulandı). OS'tan bağımsız, hep Türkçe. Genelde Windows OCR'dan yavaş, ön işleme (eğrilik, kontrast) ister. |
| PaddleOCR (`Sdcb.PaddleOCR` 3.3.1; PaddleOCR v3.7.0) | Apache-2.0 | NuGet + Paddle Inference native | 100+ MB (tahmin) | Türkçe/Latin model kapsamı doğrulanamadı. Ağır; v1 için gereksiz. |

**Öneri:** Önce Windows.Media.Ocr (Türkçe kuruluysa), yoksa Tesseract + `tur.traineddata` yedek. Sebep: sıfır boyutla başla, garantisiz olduğu yerde ~13 MB'lık yedek koy.

---

## 10. Gizlilik radarı (hassas veri tespiti)

Metin kaynağı: PDF için PdfPig, görüntü için OCR (madde 9), DOCX/TXT için düz okuma. Tespit tamamen yerel regex + doğrulama.

### TC Kimlik No (11 hane)

1. 11 hane, hepsi rakam, ilk hane `0` olamaz.
2. `d10 = ((d1 + d3 + d5 + d7 + d9) * 7 − (d2 + d4 + d6 + d8)) mod 10` (negatif çıkarsa pozitife çevir: `((x % 10) + 10) % 10`).
3. `d11 = (d1 + d2 + ... + d10) mod 10`.

Regex: `(?<!\d)[1-9]\d{10}(?!\d)`, sonra 2. ve 3. kural doğrulanır. "TC", "T.C. Kimlik", "Kimlik No" gibi bağlam kelimeleri yanlış pozitifi azaltır. Test verisini kendin üret, gerçek numara kullanma.

### IBAN (TR)
26 karakter: `TR` + 2 kontrol hanesi + 5 hane banka kodu + 1 hane (rezerve) + 16 hane hesap no. Boşlukları at.
mod-97: ilk 4 karakteri sona taşı; harfleri sayıya çevir (A=10 ... Z=35); oluşan büyük sayının mod 97'si (BigInteger veya hane hane) **1** olmalı. Regex: `\bTR\d{2}(?:\s?\d{4}){5}\s?\d{2}\b`.

### Telefon (TR)
Mobil: `(?<!\d)(?:\+90|0090|90|0)?[\s\-\.]*\(?5\d{2}\)?[\s\-\.]*\d{3}[\s\-\.]*\d{2}[\s\-\.]*\d{2}(?!\d)`. Sabit hat için ikinci kalıp (alan kodu 2xx/3xx/4xx). Eşleşmeyi 10 haneye normalize et.

### E-posta
`[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}`.

### EXIF GPS
`MetadataExtractor` (Apache-2.0, 2.9.3): `GpsDirectory` var mı bak; enlem/boylam gösterilebilir.

### Yüz var mı (çevrimdışı)

| Aday | Lisans | Not |
|---|---|---|
| **YuNet** ([opencv_zoo](https://github.com/opencv/opencv_zoo)) | MIT (model klasörü LICENSE doğrulandı, © Shiqi Yu) | ONNX, çok küçük (~200 KB, tahmin). ORT ile doğrudan ya da OpenCvSharp4 (Apache-2.0) `FaceDetectorYN` ile. |
| libfacedetection ([repo](https://github.com/ShiqiYu/libfacedetection)) | 3-clause BSD (LICENSE metni doğrulandı; GitHub SPDX NOASSERTION) | YuNet'in kaynağı. |
| UltraFace ([repo](https://github.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB)) | MIT (SPDX) | ~1 MB, eski ama yeterli. |
| FaceAiSharp ([repo](https://github.com/georg-jung/FaceAiSharp)) | Kod MIT (0.6.35, 2026-05-29) | **Model tuzağı**: tespit modeli **SCRFD (insightface)**. insightface README: eğitim verisi ve onunla eğitilmiş modeller yalnızca ticari olmayan araştırma içindir; FaceAiSharp README de modellerin daha kısıtlayıcı olabileceğini söylüyor. Kullanma. |

Sadece "yüz var mı" için kimlik tanıma (ArcFace vb.) gerekmez, kullanma.

**Öneri:** YuNet ONNX + ONNX Runtime. Sebep: MIT model, ~200 KB. (ORT v1'de yoksa yüz tespiti v1.1'e kalabilir.)

---

## 11. Video → MP4/GIF, hedef boyuta sıkıştırma (FFmpeg)

**Bundle edilecek build:** [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (repo MIT) `lgpl-shared`, win64: `ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip` (73 MB zip; lgpl static 163 MB, gpl-shared 82 MB, gpl 184 MB). Yerelde indirip `-buildconf` ve `-encoders` ile doğruladım (n9.0.2, 2026-10-18):

- `--enable-version3` (LGPL-3.0+), **`--disable-libx264 --disable-libx265`**, `--disable-libfdk-aac`, `--enable-gpl` yok, `--enable-nonfree` yok.
- H.264 kodlayıcılar: `libopenh264`, `h264_mf` (Media Foundation), `h264_nvenc`, `h264_qsv`, `h264_amf`. HEVC: `hevc_mf`, `hevc_nvenc/qsv/amf`, `libkvazaar`. AV1: `libsvtav1`, `libaom`, `librav1e`. VP8/VP9: `libvpx`, `libvpx-vp9`. Ses: `aac`, `libmp3lame`, `libopus`. GIF: yerleşik `gif`. WebP: `libwebp`.

| Seçenek | Lisans | Not |
|---|---|---|
| **BtbN lgpl-shared** | LGPL-3.0+ | `ffmpeg.exe` subprocess, dinamik DLL'lerle dağıtım, lisans metni + kaynak edinme bilgisi yeterli. Çark MIT kalabilir. |
| BtbN gpl / gyan.dev full | GPL-2.0+/3.0 | libx264/x265 var, daha iyi sıkıştırma, ama GPL yükümlülükleri ve ticari katmanı kısıtlama riski. Kaçın. |
| nonfree (fdk-aac) | Dağıtılamaz | Yasak. |

Hedef boyuta sıkıştırma: `videoBitrate = hedefBayt*8 / süre − sesBitrate`. FFMpegCore 5.5.0 (MIT, 2026-09-20) kolaylık sağlar, doğrudan `Process` de yeterli. **Kalite notu:** LGPL build'de x264 yok; `h264_mf` ve `libopenh264` oran kontrolü x264'ten zayıf olabilir (ölçmedim), donanım kodlayıcılar kullanıcı GPU'suna bağlı. Hedef boyutu tutturmak için iki geçiş ve sonrasında kontrol/yeniden deneme döngüsü kur; alternatif `libsvtav1` / `libvpx-vp9`. **Patent notu:** H.264/HEVC patent lisansı telif lisansından ayrı; hukuki durum doğrulanamadı, ticari katmandan önce bakılmalı.

GIF: `ffmpeg -vf "fps=12,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"`. gifski daha kaliteli ama lisansı doğrulanamadı (AGPL olduğunu hatırlıyorum), kullanma.

**Öneri:** BtbN `lgpl-shared` + doğrudan subprocess. Sebep: lisans temiz ve yerelde doğrulandı.

---

## Lisans tuzakları

| Tuzak | Etki |
|---|---|
| **PDFsharp ≥ 6.5.0 / 7.x** | AGPL-3.0 (+ ticari). `6.2.4`'e sabitle, floating sürüm kullanma. |
| **iText / iText.NET** | AGPL-3.0; ticari lisans gerekir. Kullanma. |
| **Ghostscript** | AGPL-3.0 (veya Artifex ticari). Kullanma. |
| **MuPDF / PyMuPDF / MuPDF.NET** | AGPL-3.0. |
| **pdf2docx** | Kendisi MIT ama PyMuPDF (AGPL) bağımlılığı; bakımı bitmiş. |
| **ImageSharp** | Açık kaynak olduğumuz sürece Apache-2.0; kapalı/ücretli katman + 1M USD gelir üstü = ticari lisans. |
| **libvips (LGPL-2.1; `NetVips.Native` LGPL-3.0+)** | Zorunlu kopyleft değil ama dinamik bağlama, lisans metni ve değiştirme hakkı şartı. |
| **FFmpeg GPL build** (libx264/x265) | GPL yükümlülükleri; `lgpl-shared` kullan. `nonfree` dağıtılamaz. |
| **H.264/HEVC patentleri** | Telif değil patent riski, doğrulanamadı. |
| **FaceAiSharp.Bundle / SCRFD / insightface modelleri** | Ağırlıklar ticari olmayan araştırma lisanslı. |
| **BRIA RMBG-1.4 / 2.0** | Ticari olmayan olduğunu hatırlıyorum, doğrulanamadı: kullanma. |
| **Docotic, Aspose, Spire, Syncfusion, Pdfium.Net.SDK** | Ticari; Docotic örneği $995/uygulama. MIT repoyla uyumsuz. |
| **LibreOffice** | MPL-2.0: sorun değil; kendi değişikliğimiz olursa o dosyalar MPL altında yayınlanır. |
| **Model/veri dosyaları** | Kod lisansı ≠ ağırlık lisansı; her ONNX modelini ayrı doğrula. |

Ayrıca THIRD-PARTY-LICENSES dosyası hazırlayıp Apache-2.0 (PdfPig, PDFium, Magick.NET, Tesseract, QPDF, MetadataExtractor) ve MIT bileşenlerin telif/lisans metinlerini installer'a koy.

---

## Önerilen minimal v1 yığını

| İş | Seçim | Lisans | Tahmini installer etkisi |
|---|---|---|---|
| PDF → PNG/JPG | PDFtoImage 5.4.0 + bblanchon PDFium | MIT / Apache-2.0 | ~6 MB |
| Görüntü motoru (dönüştür/boyutlandır/kırp/döndür/sıkıştır) | SkiaSharp 4.153.1 | MIT | ~10 MB |
| PDF → metin | PdfPig 0.1.16 | Apache-2.0 | ~2 MB |
| PDF böl/birleştir/döndür/şifre | QPDF 12.4.2 (subprocess) | Apache-2.0 | ~12-25 MB |
| PDF metadata, görüntüden PDF, PDF görüntü sıkıştırma | PDFsharp **6.2.4** | MIT | ~2 MB |
| Metadata temizleme/gösterme | Kendi JPEG/PNG segment temizleyici + MetadataExtractor 2.9.3 | Apache-2.0 | ~1 MB |
| Gizlilik radarı | PdfPig + regex + TC/IBAN doğrulama + MetadataExtractor (GPS) | Apache-2.0 | ~0 |
| OCR (taranmış PDF/görüntü için) | Windows.Media.Ocr; Türkçe yoksa v1.1'de Tesseract + tur | OS / Apache-2.0 | 0 (+~13 MB yedek) |
| Redaksiyon | Rasterize (PDFtoImage + Skia + PDFsharp 6.2.4) | MIT | ~0 |

v1.1 adayları: AVIF (Magick.NET), video (FFmpeg `lgpl-shared`, ~73 MB zip), arka plan silme (ORT + isnet-general-use, eklenti paketi), yüz tespiti (YuNet), PDF→DOCX (LibreOffice varsa). v1 toplam etkisi tahmini 35-55 MB (uygulama kodu ve WebView2 hariç).
