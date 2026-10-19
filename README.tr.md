# Swivra

[English](README.md)

Windows için çevrimdışı dosya çarkı. Bir dosyayı sürükle, **Shift**, **Ctrl** ya da **Alt**'a bas, bir dilime bırak. Hiçbir dosya bilgisayarından çıkmaz.

![Swivra çarkı](docs/images/wheel-tr.png)

> Durum: erken sürüm (0.2.0). Bütün dilimler çalışıyor. Bazı sonuçlar isteğe bağlı programlara bağlı, aşağıdaki notlara bak.

## Özellikler

| Tuş | Çark | Bugün ne yapar |
| --- | --- | --- |
| Shift | Dönüştür | Resim: PNG, JPG, BMP, WebP, PDF. PDF: PNG, JPG (sayfa başına bir resim, 200 dpi), düz metin ya da Word. Belgeler (Word, metin, Markdown): PDF, metin ya da Word |
| Ctrl | Araçlar | Resim: kırp, sıkıştır, arka planı sil, döndür, 1080 px genişliğe boyutlandır, konum ve cihaz bilgisini sil. PDF: sıkıştır, hassas bilgileri gizle, böl, birleştir, döndür, parolayla kilitle. Diğer dosyalar: zip'le, şifreli zip'e koy, metin ve Word dosyalarında hassas bilgileri gizle |
| Alt | Gönder | WhatsApp, Mail, QR kodla Wi-Fi üzerinden telefona, dosya yolunu kopyala |

- **Zincir.** Bir dilimin üstünde yarım saniye kadar bekle, dilim zincire eklenir. Bıraktığında adımların hepsi sırayla uygulanır (örnek: Döndür, Konumu sil, JPG).
- **Hassas bilgi radarı.** Sürüklerken dosya taranır. Konum bilgisi, cihaz modeli, TC kimlik no, IBAN, e-posta ya da telefon bulunursa çarkın kenarı turuncu yanar. PDF'te ilk 20 sayfa taranır.
- **Kırpma paneli.** Bir resmi Kırp dilimine bırakınca kırpma alanını ayarladığın bir panel açılır.
- **PDF araçları.** Sıkıştır, sayfalara böl, birkaç PDF'i seçim sırasıyla birleştir, döndür, resme ya da metne çevir.
- **PDF kilidi.** PDF'i parolayla korur, AES-256 kullanır. Diğer dosyalar AES-256 şifreli bir zip'e konur (7-Zip ile açılır, Windows Gezgini AES zip açamaz).
- **Hassas bilgileri gizle.** PDF'te TC kimlik no, IBAN, e-posta ve telefon gerçekten karartılır: sayfalar resme çevrildiği için gizlenen metin geri kopyalanamaz. Çıktıdaki metin artık seçilemez. Taranmış resimlerin içindeki yazılar henüz algılanmıyor (OCR yok). Metin ve Word dosyalarında bu bilgiler ████ ile değiştirilir.
- **Arka planı sil.** Küçük bir yapay zekâ modeliyle (U-2-Net, u2netp) arka planı çevrimdışı siler. Basit nesnelerde iyi sonuç verir, saç ve ince kenarlar stüdyo kalitesinde değildir.
- **Gönder.** WhatsApp: dosya (resimler küçültülür, konum bilgisi silinir) panoya konur ve WhatsApp açılır, bir sohbette Ctrl+V yap. Mail: varsayılan e-posta programında dosya ekli yeni bir ileti açar, program yoksa dosya panoya konur ve tarayıcıda yeni bir Gmail iletisi açılır, dosyayı iletiye yapıştır ya da sürükle. Telefona: bir QR kod gösterir, aynı Wi-Fi'daki telefonla okutunca dosya doğrudan bilgisayarından iner. Bağlantıda rastgele bir anahtar vardır, panel kapanınca ya da 10 dakika sonra çalışmaz.
- **Çoklu dosya.** Birden fazla dosyayı birlikte sürükle. PDF'leri Birleştir dilimine bırakırsan tek dosya olur.
- **Orijinale dokunulmaz.** Sonuç, orijinalin yanına yeni adla kaydedilir (`Tatil-temiz.jpg`).
- **Çevrimdışı.** Hesap yok, yükleme yok, internete hiçbir şey gitmez. Her şey senin makinende çalışır. Yalnız Telefona dilimi yerel ağını kullanır, o da yalnız paneli açıkken.

Notlar:
- **Word çıktısı ve belgeden PDF'e dönüşüm** düzeni yalnız [LibreOffice](https://tr.libreoffice.org/) kuruluysa korur (Swivra onu kendisi bulur). Kurulu değilse yalnız metin çıkar, sonuç da bunu söyler. Eski `.doc`, `.rtf` ve `.odt` dosyaları için LibreOffice gerekir.
- **Telefona**: ilk seferde Windows Güvenlik Duvarı, Swivra'nın ağı kullanıp kullanamayacağını sorar. İzin ver, yoksa telefonlar bağlanamaz.

![Kırpma paneli](docs/images/crop-tr.png)

## İndirme

1. [GitHub Releases](../../releases) sayfasından `Swivra-<sürüm>-win-x64.zip` dosyasını indir.
2. Zip'i istediğin yere aç.
3. `Swivra.exe` dosyasını çalıştır. Sistem tepsisine yerleşir.

Gereken: Windows 10 ya da 11 (64 bit) ve WebView2 Runtime. Windows 11'de hazır gelir. .NET kurman gerekmez.

Uygulama imzalı değil, bu yüzden Windows SmartScreen uyarı verebilir. "Daha fazla bilgi", sonra "Yine de çalıştır" de.

## Kullanım

1. Explorer'da ya da masaüstünde bir dosyayı sürüklemeye başla.
2. **Shift** (dönüştür), **Ctrl** (araçlar) ya da **Alt**'a (gönder) bas. Çark imlecin etrafında açılır.
3. Bir dilimin üstüne gel. Ortada ne olacağı yazar.
4. Dosyayı dilime bırak.

- Adımları zincirlemek için bir dilimin üstünde numara alana kadar bekle, sonra sıradakine geç.
- İptal etmek için dosyayı çarkın ortasına bırak.
- Kırpma ve kilit panellerinde **Enter** uygular, **Esc** iptal eder.
- Dil: tepsi simgesine sağ tıkla, **Dil / Language** menüsünü kullan. İlk açılışta Windows'un görüntü dili izlenir (Türkçe ya da İngilizce).

Tepsi menüsünde ayrıca "Çarkı göster (demo)", "Kayıtları aç" ve "Çıkış" var. Kayıtlar `%LOCALAPPDATA%\Swivra\swivra.log` dosyasına yazılır.

## Kaynaktan derleme

Gereken: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download) ve WebView2 Runtime.

```powershell
cd src/Swivra.App
dotnet run
```

Yayın paketini (kendi içinde .NET'li, win-x64) üretmek için:

```powershell
pwsh scripts/publish.ps1
```

Bu komut `publish/Swivra-<sürüm>-win-x64.zip` dosyasını yazar. Uygulama tek dosya değil klasör olarak gelir: `web/` ve yerel PDFium ile SkiaSharp kütüphaneleri `Swivra.exe`'nin yanındadır.

### Geliştirici bayrakları

| Bayrak | Ne işe yarar |
| --- | --- |
| `--demo --shot cikti.png` | Çarkı sahte bir dosyayla göster, ekran görüntüsü kaydet |
| `--demo-tool crop\|lock\|tel <dosya>` | Sürüklemeden, gerçek bir dosyada araç panelini aç |
| `--run adım,adım <dosya> [...]` | Arayüzsüz çalıştır, örnek: `--run png,rot dosya.pdf`. İsteğe bağlı `--password parola` ve `--crop x,y,w,h` |
| `--radar <dosya>` | Dosyayı tara, radarın bulduğunu kayda yaz |
| `--lang tr\|en` | Yalnız bu çalıştırma için dil seç |

`--run` ve `--radar` sonuçları kayıt dosyasına yazılır.

## Nasıl çalışıyor

- `InputHook`: sistem geneli klavye ve fare dinleme. Hiçbir tuşu yutmaz, sadece izler.
- `OverlayForm`: odak çalmayan, daire şeklinde, her şeyin üstünde duran pencere. Çarkı WebView2 çizer (`web/`). Cam görüntüsü, çark açılmadan hemen önce arkadaki alanın bulanıklaştırılmış görüntüsüdür.
- `CatcherForm`: çarkın üstüne yerleşen neredeyse görünmez pencere. Asıl bırakma hedefi bu. Dosya yolunu sürükleme başlar başlamaz alır (radar için) ve bırakma etkisini her zaman **kopyala** yapar, yani Explorer orijinali asla taşımaz.
- `FileActions`, `ImageActions`, `PdfActions`, `PdfRedact`, `PdfWord`, `DocActions`, `SendActions`, `PhoneShare`, `Radar`: işlemler ve hassas bilgi taraması. Resimlerde GDI+ ve SkiaSharp (WebP), PDF'lerde görüntüye çevirme için PDFium (PDFtoImage ile), metin için PdfPig, döndürme/bölme/birleştirme/sıkıştırma/kilitleme ve gizleme çıktısı için PDFsharp kullanılır. Word dosyalarında Open XML SDK, şifreli zip'te SharpZipLib, arka plan silmede ONNX Runtime, QR kodda QRCoder kullanılır.

Kütüphane ve lisans araştırması: [docs/kutuphane-arastirmasi.md](docs/kutuphane-arastirmasi.md).

## Lisans

[MIT](LICENSE). Üçüncü taraf bileşenler ve lisansları [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasında.
