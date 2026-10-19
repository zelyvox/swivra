# Çark (kod adı)

Windows için dosya çarkı. Bir dosyayı sürüklerken **Shift**, **Ctrl** ya da **Alt**'a bas: imlecin etrafında bir çark açılır, dosyayı bir dilime bırakırsın, iş biter. Hiçbir dosya internete çıkmaz.

> Durum: **erken deneme (spike).** Çark, cam görünüm, hassas bilgi radarı, zincir, resim ve PDF işlemleri çalışıyor. PDF sıkıştırma, şifreleme, Word'e dönüşüm ve belge işlemleri henüz yok, çarkta "Yakında" yazar.

## Çarklar

| Tuş | Çark | Örnek |
| --- | --- | --- |
| Shift | Dönüştür | PNG ↔ JPG ↔ BMP, PDF → PNG / JPG / metin |
| Ctrl | Araçlar | Resim: sıkıştır, döndür, 1080 px'e boyutlandır, konum bilgisini sil. PDF: döndür, böl, birleştir |
| Alt | Gönder | Yolu panoya kopyala |

- **Canlı önizleme:** dilimin üstüne gelince ortadaki camda ne olacağı yazar.
- **Zincir:** bir dilimin üstünde yarım saniye bekle, dilim zincire eklenir. Bıraktığında zincirdeki adımların hepsi sırayla uygulanır (örnek: Döndür → Konumu sil → JPG).
- **Radar:** sürüklediğin dosyada konum bilgisi, cihaz modeli, TC kimlik no, IBAN, e-posta ya da telefon varsa çarkın kenarı turuncu yanar. PDF'te ilk 20 sayfa taranır.
- **Çoklu dosya:** birden fazla dosyayı birlikte sürükle. PDF'leri **Birleştir** dilimine bırakırsan seçim sırasıyla tek dosya olur.
- Orijinal dosyaya dokunulmaz. Sonuç yanına yeni adla kaydedilir (`Tatil-temiz.jpg`).

## Çalıştırma

Gereken: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download), WebView2 Runtime (Windows 11'de hazır gelir).

```powershell
cd src/Cark.App
dotnet run
```

Uygulama sistem tepsisine yerleşir. Tepsi menüsünde **Çarkı göster (demo)**, **Kayıtları aç** ve **Çıkış** var. Kayıtlar `%LOCALAPPDATA%\Cark\cark.log` dosyasına yazılır.

Görünümü test etmek için: `dotnet run -- --demo --shot cikti.png`

Arayüzsüz deneme: `Cark.exe --run png,rot dosya.pdf` (adımlar virgülle) ya da `Cark.exe --radar dosya.pdf`. Sonuç kayıt dosyasına yazılır.

## Nasıl çalışıyor

- `InputHook`: sistem geneli klavye ve fare dinleme. Hiçbir tuşu yutmaz, sadece izler.
- `OverlayForm`: odak çalmayan, daire şeklinde, her şeyin üstünde duran pencere. Çarkı WebView2 çizer (`web/`). Cam görüntüsü, çark açılmadan hemen önce arkadaki alanın bulanıklaştırılmış görüntüsü.
- `CatcherForm`: çarkın üstüne yerleşen neredeyse görünmez pencere. Asıl bırakma hedefi bu. Dosya yolunu sürükleme başlar başlamaz alır (radar için) ve bırakma etkisini her zaman **kopyala** yapar, yani Explorer orijinali asla taşımaz.
- `FileActions`, `PdfActions`, `Radar`: işlemler ve hassas bilgi taraması. Tamamen çevrimdışı.

Kütüphane ve lisans araştırması: [docs/kutuphane-arastirmasi.md](docs/kutuphane-arastirmasi.md).

## Lisans

MIT
