# Çark (kod adı)

Windows için dosya çarkı. Bir dosyayı sürüklerken **Shift**, **Ctrl** ya da **Alt**'a bas: imlecin etrafında bir çark açılır, dosyayı bir dilime bırakırsın, iş biter. Hiçbir dosya internete çıkmaz.

> Durum: **erken deneme (spike).** Çark, cam görünüm, hassas bilgi radarı, zincir ve resim işlemleri çalışıyor. PDF ve belge işlemleri henüz yok, çarkta "Yakında" yazar.

## Çarklar

| Tuş | Çark | Örnek |
| --- | --- | --- |
| Shift | Dönüştür | PNG ↔ JPG ↔ BMP |
| Ctrl | Araçlar | Sıkıştır, döndür, 1080 px'e boyutlandır, konum bilgisini sil |
| Alt | Gönder | Yolu panoya kopyala |

- **Canlı önizleme:** dilimin üstüne gelince ortadaki camda ne olacağı yazar.
- **Zincir:** bir dilimin üstünde yarım saniye bekle, dilim zincire eklenir. Bıraktığında zincirdeki adımların hepsi sırayla uygulanır (örnek: Döndür → Konumu sil → JPG).
- **Radar:** sürüklediğin dosyada konum bilgisi, cihaz modeli, TC kimlik no, IBAN, e-posta ya da telefon varsa çarkın kenarı turuncu yanar.
- Orijinal dosyaya dokunulmaz. Sonuç yanına yeni adla kaydedilir (`Tatil-temiz.jpg`).

## Sağ tık

Bir ya da birkaç dosyaya sağ tıkla, **Çark ile aç**'ı seç: çark imlecin yanında, dosyalar yüklü açılır. Bu sefer bırakmak yerine dilime **tıklarsın**. Shift, Ctrl ve Alt çarkı aynı şekilde değiştirir; radar, canlı önizleme ve zincir de aynı. **Esc** ya da çarkın dışına tıklamak kapatır.

Windows 11'de seçenek klasik menüde durur: **Diğer seçenekleri göster** altında ya da doğrudan **Shift+sağ tık** ile. Menü yönetici izni istemeden yalnız senin hesabına eklenir (`HKCU`). Tepsi menüsündeki **Sağ tık menüsü** ile kapatıp açabilirsin, seçim `%LOCALAPPDATA%\Cark\settings.json` dosyasında kalır.

## Çalıştırma

Gereken: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download), WebView2 Runtime (Windows 11'de hazır gelir).

```powershell
cd src/Cark.App
dotnet run
```

Uygulama sistem tepsisine yerleşir. Tepsi menüsünde **Çarkı göster (demo)**, **Kayıtları aç** ve **Çıkış** var. Kayıtlar `%LOCALAPPDATA%\Cark\cark.log` dosyasına yazılır.

Görünümü test etmek için: `dotnet run -- --demo --shot cikti.png`

## Nasıl çalışıyor

- `InputHook`: sistem geneli klavye ve fare dinleme. Sürüklerken hiçbir tuşu yutmaz, sadece izler. Sağ tıkla açılan çark ekrandayken yalnız Alt ve Esc'i yutar, Explorer menü çubuğunu açmasın diye.
- `OverlayForm`: odak çalmayan, daire şeklinde, her şeyin üstünde duran pencere. Çarkı WebView2 çizer (`web/`). Cam görüntüsü, çark açılmadan hemen önce arkadaki alanın bulanıklaştırılmış görüntüsü.
- `CatcherForm`: çarkın üstüne yerleşen neredeyse görünmez pencere. Asıl bırakma hedefi bu. Dosya yolunu sürükleme başlar başlamaz alır (radar için) ve bırakma etkisini her zaman **kopyala** yapar, yani Explorer orijinali asla taşımaz.
- `FileActions`, `Radar`: işlemler ve hassas bilgi taraması. Tamamen çevrimdışı.

Kütüphane ve lisans araştırması: [docs/kutuphane-arastirmasi.md](docs/kutuphane-arastirmasi.md).

## Lisans

MIT
